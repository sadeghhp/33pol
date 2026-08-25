using Microsoft.Extensions.DependencyInjection;
using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Billing;
using Pol33.Core.Models;
using Pol33.Core.Providers;

namespace Pol33.Api.Services;

public sealed class AdminModelProvisioningService(
    IControlPlaneCommands commands,
    IUpstreamSecretStore secretStore,
    IServiceScopeFactory scopeFactory,
    UpstreamEnvVarPolicy envVarPolicy,
    IAuditLogger audit)
{
    /// <summary>
    /// Pricing is backed by the database and so is scoped, while this service is a singleton.
    /// Resolve it per call from a fresh scope, as ModelRegistryWriter does for its repository.
    /// </summary>
    private async Task<T> WithPricingAsync<T>(Func<IRateCardAdminService, Task<T>> action)
    {
        using var scope = scopeFactory.CreateScope();
        var pricing = scope.ServiceProvider.GetRequiredService<IRateCardAdminService>();
        return await action(pricing).ConfigureAwait(false);
    }

    public Task<RegistryMutationResult> AddAsync(
        AdminModelWriteRequest request,
        CancellationToken cancellationToken = default) =>
        AddAsync(request, AdminActor.Anonymous, cancellationToken);

    /// <param name="request">The model write.</param>
    /// <param name="actor">The caller, stamped on every audit entry the write produces.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<RegistryMutationResult> AddAsync(
        AdminModelWriteRequest request,
        AdminActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var model = request.Model ?? throw new ArgumentException("Model is required.");

        var prep = PrepareModel(
            model,
            request.ApiKey,
            request.ClearApiKey,
            isUpdate: false,
            previousId: null,
            previousState: null);
        if (!prep.Success)
        {
            return RegistryMutationResult.Fail(prep.Error!);
        }

        // Secret first, model second. A model registered with a secretRef whose secret does not
        // exist is silently broken — every inference request fails with "upstream auth token not
        // configured" and nothing surfaces the cause.
        //
        // The store is keyed by model id, and the id may already belong to a registered model —
        // that is exactly the case AddModelAsync rejects with 409 below. So the secret write here
        // may be overwriting (or, with clearApiKey, deleting) a live credential, not creating a
        // fresh one. Snapshot the prior value first: a rejected add must put the store back exactly
        // as it was, where deleting "the" secret destroyed the existing model's credential.
        var hadPriorSecret = secretStore.TryGet(prep.Model!.Id, out var priorSecret);

        if (!await TryApplySecretAsync(prep, actor, cancellationToken).ConfigureAwait(false))
        {
            return RegistryMutationResult.Fail(
                "The upstream credential could not be stored, so the model was not created.");
        }

        var result = await commands.AddModelAsync(prep.Model!, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            await RestoreSecretAfterFailedAddAsync(prep, hadPriorSecret, priorSecret, actor, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }

        return await ApplyPricingAsync(prep.Model!.Id, request, result, actor, cancellationToken).ConfigureAwait(false);
    }

    public Task<RegistryMutationResult> UpdateAsync(
        string id,
        AdminModelWriteRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(id, request, AdminActor.Anonymous, cancellationToken);

    /// <param name="id">The route id or alias of the model to update.</param>
    /// <param name="request">The model write.</param>
    /// <param name="actor">The caller, stamped on every audit entry the write produces.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public async Task<RegistryMutationResult> UpdateAsync(
        string id,
        AdminModelWriteRequest request,
        AdminActor actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var model = request.Model ?? throw new ArgumentException("Model is required.");

        // The route parameter may be an alias, so resolve the model the write will actually land on:
        // a rename has to move that model's credential and rate card, both of which are keyed by the
        // canonical id.
        var existing = ResolveRegisteredModel(id);

        var prep = PrepareModel(
            model,
            request.ApiKey,
            request.ClearApiKey,
            isUpdate: true,
            previousId: existing?.Id,
            previousState: existing?.State);
        if (!prep.Success)
        {
            return RegistryMutationResult.Fail(prep.Error!);
        }

        // Update keeps the model-first order: the model already exists and is serving traffic, so
        // rewriting its credential before knowing the metadata update is accepted would disturb a
        // working model on a validation failure. A secret failure here is reported rather than
        // swallowed, so the operator knows the credential did not take effect.
        var result = await commands.UpdateModelAsync(id, prep.Model!, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        var previousId = existing?.Id;
        var renamedFrom = previousId is not null &&
                          !string.Equals(previousId, prep.Model!.Id, StringComparison.OrdinalIgnoreCase)
            ? previousId
            : null;

        if (!await TryApplySecretAsync(prep, actor, cancellationToken).ConfigureAwait(false))
        {
            return RegistryMutationResult.Fail(
                $"{result.Message} However, the upstream credential was not stored, so this model " +
                "cannot authenticate to its upstream until the credential is set again.");
        }

        if (renamedFrom is not null)
        {
            await MigrateRenamedModelAsync(renamedFrom, prep, request, actor, cancellationToken).ConfigureAwait(false);
        }

        return await ApplyPricingAsync(prep.Model!.Id, request, result, actor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminModelListItem>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var pricingByModel = await WithPricingAsync(p => p.GetPricingByModelAsync(cancellationToken))
            .ConfigureAwait(false);

        var models = commands.ListModels();

        // One bulk existence query for the whole list. This used to be a blocking
        // ExistsAsync(...).GetAwaiter().GetResult() per model, which pinned a thread-pool thread per
        // model on every admin list request and shares that pool with the inference path.
        var secretRefModelIds = models
            .Select(m => m.UpstreamAuth?.SecretRef)
            .Where(secretRef => !string.IsNullOrWhiteSpace(secretRef))
            .Select(secretRef => UpstreamSecretRefs.TryParseModelId(secretRef!, out var id) ? id : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var storedSecrets = secretRefModelIds.Count == 0
            ? (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await secretStore.ListExistingAsync(secretRefModelIds, cancellationToken).ConfigureAwait(false);

        return models
            .Select(m => new AdminModelListItem
            {
                Model = m,
                HasUpstreamCredential = HasCredential(m, storedSecrets),
                Pricing = pricingByModel.TryGetValue(m.Id, out var price) ? price : null
            })
            .ToList();
    }

    /// <summary>
    /// Whether a single model has a usable upstream credential. Prefer the list path, which resolves
    /// stored secrets in bulk; this overload exists for callers dealing with one model.
    /// </summary>
    public async Task<bool> HasCredentialAsync(ModelConfig model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (model.UpstreamAuth is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar))
        {
            return true;
        }

        return UpstreamSecretRefs.TryParseModelId(model.UpstreamAuth.SecretRef, out var modelId) &&
               await secretStore.ExistsAsync(modelId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the registered model an admin route parameter refers to, by id or by alias, so callers
    /// can work with the canonical id the registry actually stores.
    /// </summary>
    private ModelConfig? ResolveRegisteredModel(string id)
    {
        var trimmed = id.Trim();
        var models = commands.ListModels();

        return models.FirstOrDefault(m => string.Equals(m.Id, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? models.FirstOrDefault(
                   m => m.Aliases.Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Moves the things keyed by model id — the upstream credential and the rate card — after a
    /// rename. Without this a renamed model loses its credential (and silently stops authenticating)
    /// and its price (and silently bills at zero), while the old id keeps orphaned copies of both.
    /// </summary>
    private async Task MigrateRenamedModelAsync(
        string previousId,
        PrepResult prep,
        AdminModelWriteRequest request,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var newId = prep.Model!.Id;

        try
        {
            if (prep.ClearSecret || !string.IsNullOrWhiteSpace(prep.SecretToStore))
            {
                // The request already established the credential under the new id; the old copy is
                // now unreferenced.
                await secretStore.DeleteAsync(previousId, cancellationToken).ConfigureAwait(false);
            }
            else if (secretStore.TryGet(previousId, out var secret) && !string.IsNullOrWhiteSpace(secret))
            {
                await secretStore.PutAsync(newId, secret!, cancellationToken).ConfigureAwait(false);
                await secretStore.DeleteAsync(previousId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.LogAdminAction(
                "model.rename.credential_migration_failed",
                actor.ToAuditEntry(new { previousId, newId, error = ex.Message }));
        }

        // An explicit pricing instruction in the request is applied to the new id by
        // ApplyPricingAsync; only carry the old rate card over when the request said nothing.
        if (request.Pricing is null && !request.ClearPricing)
        {
            var pricingByModel = await WithPricingAsync(p => p.GetPricingByModelAsync(cancellationToken))
                .ConfigureAwait(false);

            if (pricingByModel.TryGetValue(previousId, out var previousPricing))
            {
                await WithPricingAsync(p => p.SetPricingAsync(newId, previousPricing, cancellationToken))
                    .ConfigureAwait(false);
            }
        }

        await WithPricingAsync(p => p.ClearPricingAsync(previousId, cancellationToken)).ConfigureAwait(false);

        audit.LogAdminAction(
            "model.renamed",
            actor.ToAuditEntry(new { previousId, newId }));
    }

    /// <summary>
    /// Applies pricing after the model itself is persisted, since pricing keys off the model id.
    /// A pricing failure is surfaced rather than swallowed, but the model change already stood.
    /// </summary>
    private async Task<RegistryMutationResult> ApplyPricingAsync(
        string modelId,
        AdminModelWriteRequest request,
        RegistryMutationResult modelResult,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        if (request.ClearPricing)
        {
            var cleared = await WithPricingAsync(p => p.ClearPricingAsync(modelId, cancellationToken))
                .ConfigureAwait(false);
            return cleared.Success
                ? modelResult
                : RegistryMutationResult.Fail(
                    $"{modelResult.Message} However, pricing was not cleared: {cleared.Message}",
                    cleared.StatusCode);
        }

        if (request.Pricing is null)
        {
            return modelResult;
        }

        var applied = await WithPricingAsync(p => p.SetPricingAsync(modelId, request.Pricing, cancellationToken))
            .ConfigureAwait(false);

        if (!applied.Success)
        {
            return RegistryMutationResult.Fail(
                $"{modelResult.Message} However, pricing was not saved: {applied.Message}",
                applied.StatusCode);
        }

        audit.LogAdminAction(
            "model.pricing.update",
            actor.ToAuditEntry(new
            {
                modelId,
                request.Pricing.InputPricePerMillionTokens,
                request.Pricing.OutputPricePerMillionTokens,
            }));

        return modelResult;
    }

    /// <summary>
    /// Resolves credential presence against an already-fetched set of stored secret ids, so listing
    /// N models costs one secret-store call rather than N.
    /// </summary>
    private static bool HasCredential(ModelConfig model, IReadOnlySet<string> storedSecrets)
    {
        if (model.UpstreamAuth is null)
        {
            return false;
        }

        // An env-var credential is resolved from the process environment, not the secret store.
        if (!string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar))
        {
            return true;
        }

        return UpstreamSecretRefs.TryParseModelId(model.UpstreamAuth.SecretRef, out var modelId) &&
               storedSecrets.Contains(modelId);
    }

    private PrepResult PrepareModel(
        ModelConfig model,
        string? apiKey,
        bool clearApiKey,
        bool isUpdate,
        string? previousId,
        string? previousState)
    {
        if (!ModelTypes.TryNormalize(model.ModelType, out var modelType, out var modelTypeError))
        {
            return PrepResult.Fail(modelTypeError!);
        }

        // An update never changes the route's state, however the body spells it: state moves only
        // through the dedicated stop/start endpoints. ModelConfig.State defaults to "serving", so a
        // body that simply omits the field — which is every write the admin drawer makes — is
        // indistinguishable from one that asks for "serving". Honouring it would put a stopped route
        // back into service on any unrelated edit (a url fix, a new alias). On create there is no
        // prior state to protect, so an explicit one is taken at face value.
        var requestedState = isUpdate ? previousState : model.State;
        if (!ModelRouteStates.TryNormalize(requestedState, out var state, out var stateError))
        {
            return PrepResult.Fail(stateError!);
        }

        var normalized = new ModelConfig
        {
            Id = model.Id?.Trim() ?? string.Empty,
            Url = model.Url?.Trim() ?? string.Empty,
            MaxContextLength = model.MaxContextLength > 0 ? model.MaxContextLength : 8192,
            Aliases = model.Aliases ?? [],
            PublicAccess = model.PublicAccess,
            Capabilities = model.Capabilities ?? [],
            ModelType = modelType,
            State = state,
        };

        if (string.IsNullOrWhiteSpace(normalized.Id) || string.IsNullOrWhiteSpace(normalized.Url))
        {
            return PrepResult.Fail("Model id and url are required.");
        }

        var key = apiKey?.Trim();
        var hasKey = !string.IsNullOrWhiteSpace(key);
        string? secretToStore = null;

        if (clearApiKey)
        {
            normalized.UpstreamAuth = null;
            return PrepResult.Ok(normalized, secretToStore: null, clearSecret: true);
        }

        if (hasKey)
        {
            if (LooksLikeInvalidApiKeyPlacement(key!))
            {
                return PrepResult.Fail("apiKey must be the upstream provider secret, not an environment variable name.");
            }

            normalized.UpstreamAuth = new UpstreamAuthConfig
            {
                Type = "bearer",
                SecretRef = UpstreamSecretRefs.ForModel(normalized.Id)
            };

            // Falls through to the shared validation gate below rather than returning early: an
            // early return meant any rule added to ModelConfigValidation silently did not apply to
            // models created with an inline apiKey, which is the most common admin flow.
            secretToStore = key;
        }

        if (!hasKey && model.UpstreamAuth is not null)
        {
            if (!string.Equals(model.UpstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                return PrepResult.Fail("upstreamAuth.type must be 'bearer'.");
            }

            var hasEnv = !string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar);
            var hasRef = !string.IsNullOrWhiteSpace(model.UpstreamAuth.SecretRef);

            if (hasEnv)
            {
                if (!EnvVarNameValidator.TryValidate(model.UpstreamAuth.EnvVar, out var normalizedEnvVar, out var envError))
                {
                    return PrepResult.Fail(envError!);
                }

                if (!envVarPolicy.IsAllowed(normalizedEnvVar, out var policyError))
                {
                    return PrepResult.Fail(policyError!);
                }

                normalized.UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    EnvVar = normalizedEnvVar!
                };
            }
            else if (hasRef)
            {
                var secretRef = model.UpstreamAuth.SecretRef.Trim();

                // On a rename the client echoes back the credential reference for the old id. Follow
                // the model to its new id rather than rejecting the edit; the stored secret is moved
                // to match once the rename is persisted.
                if (previousId is not null &&
                    !string.Equals(previousId, normalized.Id, StringComparison.Ordinal) &&
                    UpstreamSecretRefs.IsValidForModel(secretRef, previousId))
                {
                    secretRef = UpstreamSecretRefs.ForModel(normalized.Id);
                }

                normalized.UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = secretRef
                };
            }
            else if (!isUpdate)
            {
                normalized.UpstreamAuth = null;
            }
            else
            {
                return PrepResult.Fail("upstreamAuth requires envVar or secretRef when not supplying apiKey.");
            }
        }
        else if (!hasKey && !isUpdate)
        {
            normalized.UpstreamAuth = null;
        }

        if (!ModelConfigValidation.TryValidate(normalized, out var validationError))
        {
            return PrepResult.Fail(validationError!);
        }

        return PrepResult.Ok(normalized, secretToStore, clearSecret: false);
    }

    /// <summary>
    /// Applies the secret-store side of a provisioning request. Returns false if the store rejected
    /// the write, so the caller can abort or compensate rather than leaving a model whose credential
    /// silently does not exist.
    /// </summary>
    private async Task<bool> TryApplySecretAsync(PrepResult prep, AdminActor actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prep.Model?.Id))
        {
            return true;
        }

        try
        {
            if (prep.ClearSecret)
            {
                await secretStore.DeleteAsync(prep.Model.Id, cancellationToken).ConfigureAwait(false);
                audit.LogAdminAction(
                    "upstream_secret.deleted",
                    actor.ToAuditEntry(new { modelId = prep.Model.Id }));
                return true;
            }

            if (!string.IsNullOrWhiteSpace(prep.SecretToStore))
            {
                await secretStore.PutAsync(prep.Model.Id, prep.SecretToStore, cancellationToken).ConfigureAwait(false);
                audit.LogAdminAction(
                    "upstream_secret.updated",
                    actor.ToAuditEntry(new { modelId = prep.Model.Id }));
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.LogAdminAction(
                "upstream_secret.failed",
                actor.ToAuditEntry(new
                {
                    modelId = prep.Model.Id,
                    operation = prep.ClearSecret ? "delete" : "put",
                    error = ex.Message,
                }));
            return false;
        }
    }

    /// <summary>
    /// Puts the secret store back the way it was before a model create that was then rejected.
    /// </summary>
    /// <remarks>
    /// Restores, not deletes: when the rejected id belongs to an existing model (the 409 duplicate
    /// case), the secret this create overwrote or cleared was that model's <em>live credential</em>,
    /// and deleting it broke every subsequent request to the model with "upstream auth token not
    /// configured". Only when nothing was stored under the id before does rollback mean removal.
    /// A restore failure is audited: at that point the store disagrees with the registry, which is
    /// worth surfacing rather than inert.
    /// </remarks>
    private async Task RestoreSecretAfterFailedAddAsync(
        PrepResult prep,
        bool hadPriorSecret,
        string? priorSecret,
        AdminActor actor,
        CancellationToken cancellationToken)
    {
        var touchedStore = prep.ClearSecret || !string.IsNullOrWhiteSpace(prep.SecretToStore);
        if (!touchedStore || string.IsNullOrWhiteSpace(prep.Model?.Id))
        {
            return;
        }

        try
        {
            if (hadPriorSecret && !string.IsNullOrEmpty(priorSecret))
            {
                await secretStore.PutAsync(prep.Model.Id, priorSecret, cancellationToken).ConfigureAwait(false);
                audit.LogAdminAction(
                    "upstream_secret.restored",
                    actor.ToAuditEntry(new { modelId = prep.Model.Id }));
            }
            else if (!prep.ClearSecret)
            {
                await secretStore.DeleteAsync(prep.Model.Id, cancellationToken).ConfigureAwait(false);
                audit.LogAdminAction(
                    "upstream_secret.rolled_back",
                    actor.ToAuditEntry(new { modelId = prep.Model.Id }));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            audit.LogAdminAction(
                "upstream_secret.rollback_failed",
                actor.ToAuditEntry(new { modelId = prep.Model.Id, error = ex.Message }));
        }
    }

    /// <summary>
    /// Detects an environment variable <em>name</em> pasted into the <c>apiKey</c> field.
    /// </summary>
    /// <remarks>
    /// The signal is the shouting-snake-case convention environment variables use
    /// (<c>OPENROUTER_API_KEY</c>), not "parses as an identifier". Accepting the latter rejected real
    /// credentials: any token built from letters, digits and underscores — a Hugging Face
    /// <c>hf_…</c> token, for one — satisfies the identifier grammar, so the most common admin flow
    /// (paste the provider key) refused a whole class of valid keys and told the operator they had
    /// entered a variable name.
    /// </remarks>
    internal static bool LooksLikeInvalidApiKeyPlacement(string value)
    {
        var trimmed = value.Trim();

        // Real credentials are long. A short all-caps identifier is a variable name.
        if (trimmed.Length is 0 or > 64)
        {
            return false;
        }

        if (!EnvVarNameValidator.TryValidate(trimmed, out _, out _))
        {
            return false;
        }

        foreach (var c in trimmed)
        {
            if (char.IsLower(c))
            {
                return false;
            }
        }

        // All-caps, underscore-separated, and it names a credential — the environment variable shape.
        return trimmed.Contains('_', StringComparison.Ordinal);
    }

    private sealed record PrepResult(bool Success, ModelConfig? Model, string? SecretToStore, bool ClearSecret, string? Error)
    {
        public static PrepResult Ok(ModelConfig model, string? secretToStore, bool clearSecret) =>
            new(true, model, secretToStore, clearSecret, null);

        public static PrepResult Fail(string error) =>
            new(false, null, null, false, error);
    }
}
