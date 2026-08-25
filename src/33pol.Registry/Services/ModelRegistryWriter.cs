using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;

namespace Pol33.Registry.Services;

/// <summary>
/// Applies admin mutations to the model registry: reads the current route set from the database
/// (<see cref="IModelRouteRepository"/>), applies the change, validates the <em>result</em>, persists
/// it, then swaps the in-memory registry so the change is live immediately. Writes are serialized by
/// <see cref="RegistryGate"/> within a process and by the route version across processes. Requires a
/// configured database; DB-less deployments are read-only from models.json.
/// </summary>
/// <remarks>
/// Two ordering rules keep the database and the live registry from diverging, and both were learned
/// the hard way:
/// <list type="bullet">
/// <item>The candidate set is validated before it is persisted. Persisting first and validating
/// during the in-memory swap left rejected models in the database, which then failed to load at the
/// next startup — an empty registry, every request failing, and the next successful write deleting
/// every other route.</item>
/// <item>The current set is read from the database, not from the in-memory registry. Memory is a
/// per-process cache that a second replica, a failed load, or a concurrent write can make stale, and
/// a stale read here rewrites the whole table from that stale state.</item>
/// </list>
/// </remarks>
public sealed class ModelRegistryWriter(
    ModelRegistryService registry,
    RegistryGate gate,
    IServiceScopeFactory scopeFactory,
    IUpstreamSecretStore secretStore,
    ILogger<ModelRegistryWriter> logger) : IModelRegistryWriter
{
    public async Task<RegistryMutationResult> AddModelAsync(
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (string.IsNullOrWhiteSpace(model.Id) || string.IsNullOrWhiteSpace(model.Url))
        {
            return RegistryMutationResult.Fail("Model id and url are required.");
        }

        if (!ModelConfigValidation.TryValidate(model, out var validationError))
        {
            return RegistryMutationResult.Fail(validationError!);
        }

        return await MutateAsync(
            current =>
            {
                if (TryFindById(current, model.Id) is not null)
                {
                    return MutationPlan.Rejected($"Model '{model.Id}' already exists.", 409);
                }

                if (FindAliasOwner(current, model.Id) is { } aliasOwner)
                {
                    // Not "already exists": no model carries this id, so the operator would look for
                    // it in the list and not find it. Name the model that actually holds the alias.
                    return MutationPlan.Rejected(
                        $"'{model.Id}' is already an alias of model '{aliasOwner.Id}'. " +
                        "Remove that alias first, or choose a different model id.",
                        409);
                }

                return MutationPlan.Ok([.. current, ModelRegistryPersistence.CloneModel(model)]);
            },
            $"Model '{model.Id.Trim()}' added.",
            "add model",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RegistryMutationResult> UpdateModelAsync(
        string id,
        ModelConfig model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(model);

        if (!ModelConfigValidation.TryValidate(model, out var validationError))
        {
            return RegistryMutationResult.Fail(validationError!);
        }

        string? renamedTo = null;

        var result = await MutateAsync(
            current =>
            {
                var existing = TryFindById(current, id) ?? FindAliasOwner(current, id);
                if (existing is null)
                {
                    return MutationPlan.Rejected($"Model '{id}' was not found.", 404);
                }

                // A blank id in the body means "leave the id alone"; a different one is a rename.
                // Silently pinning the id to the existing one (the old behaviour) reported success
                // for a rename that never happened.
                // Ids are matched case-insensitively everywhere else (routing, grants, rate cards), so
                // differently-cased spelling of the same id addresses the same model rather than
                // renaming it — treating it as a rename would silently re-key its price and credential.
                var targetId = string.IsNullOrWhiteSpace(model.Id) ? existing.Id : model.Id.Trim();
                if (string.Equals(targetId, existing.Id, StringComparison.OrdinalIgnoreCase))
                {
                    targetId = existing.Id;
                }

                if (!string.Equals(targetId, existing.Id, StringComparison.Ordinal))
                {
                    var clash = TryFindById(current, targetId);
                    if (clash is not null && !ReferenceEquals(clash, existing))
                    {
                        return MutationPlan.Rejected($"Model '{targetId}' already exists.", 409);
                    }

                    var aliasOwner = FindAliasOwner(current, targetId);
                    if (aliasOwner is not null && !ReferenceEquals(aliasOwner, existing))
                    {
                        return MutationPlan.Rejected(
                            $"'{targetId}' is already an alias of model '{aliasOwner.Id}'. " +
                            "Remove that alias first, or choose a different model id.",
                            409);
                    }

                    renamedTo = targetId;
                }

                var updated = ModelRegistryPersistence.CloneModel(model);
                updated.Id = targetId;

                return MutationPlan.Ok(
                    current
                        .Select(m => string.Equals(m.Id, existing.Id, StringComparison.OrdinalIgnoreCase) ? updated : m)
                        .ToList());
            },
            () => renamedTo is null
                ? $"Model '{id}' updated."
                : $"Model '{id}' renamed to '{renamedTo}' and updated.",
            "update model",
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<RegistryMutationResult> RemoveModelAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        string? removedId = null;

        var result = await MutateAsync(
            current =>
            {
                var existing = TryFindById(current, id) ?? FindAliasOwner(current, id);
                if (existing is null)
                {
                    return MutationPlan.Rejected($"Model '{id}' was not found.", 404);
                }

                removedId = existing.Id;

                // Removing the last model is allowed: "no routes configured" is a state an operator
                // is entitled to reach, and refusing it left the row in the database while the admin
                // UI reported a failure the operator could not act on.
                return MutationPlan.Ok(
                    current
                        .Where(m => !string.Equals(m.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
                        .ToList());
            },
            () => $"Model '{removedId}' removed.",
            "remove model",
            cancellationToken).ConfigureAwait(false);

        if (result.Success && removedId is not null)
        {
            await secretStore.DeleteAsync(removedId, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async Task<RegistryMutationResult> SetModelStateAsync(
        string id,
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (!ModelRouteStates.TryNormalize(state, out var targetState, out var stateError) ||
            string.IsNullOrWhiteSpace(state))
        {
            // Blank normalizes to "serving" on the write paths that treat it as "unspecified";
            // here the state *is* the request, so an absent one is a caller error, not a default.
            return RegistryMutationResult.Fail(
                stateError ?? $"state is required and must be one of: {string.Join(", ", ModelRouteStates.All)}.",
                400);
        }

        string? targetId = null;
        var alreadyInState = false;

        return await MutateAsync(
            current =>
            {
                var existing = TryFindById(current, id) ?? FindAliasOwner(current, id);
                if (existing is null)
                {
                    return MutationPlan.Rejected($"Model '{id}' was not found.", 404);
                }

                targetId = existing.Id;

                // Idempotent: stopping an already-stopped route is not an error, and rewriting the
                // whole table for a no-op change would bump the route version for every replica.
                if (string.Equals(existing.State, targetState, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyInState = true;
                    return MutationPlan.NoChange();
                }

                var updated = ModelRegistryPersistence.CloneModel(existing);
                updated.State = targetState;

                return MutationPlan.Ok(
                    current
                        .Select(m => string.Equals(m.Id, existing.Id, StringComparison.OrdinalIgnoreCase) ? updated : m)
                        .ToList());
            },
            () => alreadyInState
                ? $"Model '{targetId}' is already {targetState}."
                : targetState == ModelRouteStates.Stopped
                    ? $"Model '{targetId}' stopped; it no longer appears in /v1/models and requests for it are rejected."
                    : $"Model '{targetId}' is serving again.",
            targetState == ModelRouteStates.Stopped ? "stop model" : "start model",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RegistryMutationResult> ReplaceAllAsync(
        IReadOnlyList<ModelConfig> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        // Unlike a targeted remove, a bulk replace with nothing in it is far more likely to be a
        // truncated payload than an intentional "delete every route", so it stays rejected.
        if (models.Count == 0)
        {
            logger.LogWarning("ReplaceAll rejected: empty model list would clear the registry.");
            return RegistryMutationResult.Fail(
                "Cannot replace registry with an empty model list.",
                400);
        }

        foreach (var model in models)
        {
            if (!ModelConfigValidation.TryValidate(model, out var validationError))
            {
                return RegistryMutationResult.Fail(validationError!);
            }
        }

        var cloned = models.Select(ModelRegistryPersistence.CloneModel).ToList();

        return await MutateAsync(
            _ => MutationPlan.Ok(cloned),
            $"Registry replaced with {cloned.Count} model(s).",
            "replace registry",
            cancellationToken).ConfigureAwait(false);
    }

    private Task<RegistryMutationResult> MutateAsync(
        Func<IReadOnlyList<ModelConfig>, MutationPlan> plan,
        string successMessage,
        string action,
        CancellationToken cancellationToken) =>
        MutateAsync(plan, () => successMessage, action, cancellationToken);

    /// <summary>
    /// Read (from the database) → plan → validate → persist (version-checked) → swap memory, all
    /// under the process gate. Every failure mode leaves both the database and the registry untouched.
    /// </summary>
    private async Task<RegistryMutationResult> MutateAsync(
        Func<IReadOnlyList<ModelConfig>, MutationPlan> plan,
        Func<string> successMessage,
        string action,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetService<IModelRouteRepository>();
            if (repository is null)
            {
                return RegistryMutationResult.Fail("Model registry updates require a configured database.", 503);
            }

            ModelRouteSnapshot current;
            try
            {
                current = await repository.ListWithVersionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ModelRegistryPersistErrors.FromException(ex, "database", logger, $"read routes for {action}");
            }

            var planned = plan(current.Models);
            if (!planned.Success)
            {
                return planned.Failure!;
            }

            // A plan that changes nothing must not be persisted. Rewriting the identical route set
            // still bumps the route version, which every other replica polls and reacts to — an
            // idempotent no-op would fan out a pointless reload across the fleet.
            if (planned.Unchanged)
            {
                return RegistryMutationResult.Ok(successMessage());
            }

            // The candidate set must be loadable before it is durable. This is the guard that keeps a
            // rejected write out of the database.
            (Dictionary<string, ModelConfig> Lookup, List<ModelConfig> Models) snapshot;
            try
            {
                snapshot = ModelRegistryPersistence.BuildLookup(planned.Models!);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Rejected {Action}: the resulting registry would not be loadable.", action);
                return RegistryMutationResult.Fail(ex.Message, 400);
            }

            try
            {
                var version = await repository
                    .ReplaceAllAsync(planned.Models!, current.Version, cancellationToken)
                    .ConfigureAwait(false);

                registry.ApplyModels(snapshot, version);
                logger.LogInformation(
                    "Registry {Action}: {ModelCount} model(s) at route version {Version}.",
                    action,
                    planned.Models!.Count,
                    version);

                return RegistryMutationResult.Ok(successMessage());
            }
            catch (ModelRouteVersionConflictException ex)
            {
                logger.LogWarning(
                    "Rejected {Action}: routes changed concurrently (expected version {Expected}, found {Actual}).",
                    action,
                    ex.ExpectedVersion,
                    ex.ActualVersion);
                return RegistryMutationResult.Fail(
                    "The model routes were changed by someone else while this edit was in flight. " +
                    "Reload the model list and try again.",
                    409);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ModelRegistryPersistErrors.FromException(ex, "database", logger, action);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static ModelConfig? TryFindById(IReadOnlyList<ModelConfig> models, string id) =>
        models.FirstOrDefault(m => string.Equals(m.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The model whose alias list claims <paramref name="name"/>, if any. Kept separate from the id
    /// lookup so a name that is only an alias is never reported as an existing model.
    /// </summary>
    private static ModelConfig? FindAliasOwner(IReadOnlyList<ModelConfig> models, string name)
    {
        var trimmed = name.Trim();
        return models.FirstOrDefault(
            m => m.Aliases.Any(alias => string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record MutationPlan(
        bool Success,
        List<ModelConfig>? Models,
        RegistryMutationResult? Failure,
        bool Unchanged = false)
    {
        public static MutationPlan Ok(List<ModelConfig> models) => new(true, models, null);

        /// <summary>The request was valid and the registry already satisfies it; nothing to write.</summary>
        public static MutationPlan NoChange() => new(true, null, null, Unchanged: true);

        public static MutationPlan Rejected(string message, int statusCode) =>
            new(false, null, RegistryMutationResult.Fail(message, statusCode));
    }
}
