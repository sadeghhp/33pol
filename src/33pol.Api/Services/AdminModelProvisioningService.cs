using Pol33.Api.Contracts;
using Pol33.Core.Abstractions;
using Pol33.Core.Models;
using Pol33.Core.Providers;

namespace Pol33.Api.Services;

public sealed class AdminModelProvisioningService(
    IControlPlaneCommands commands,
    IUpstreamSecretStore secretStore,
    IAuditLogger audit)
{
    public async Task<RegistryMutationResult> AddAsync(
        AdminModelWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model ?? throw new ArgumentException("Model is required.");

        var prep = PrepareModel(model, request.ApiKey, request.ClearApiKey, isUpdate: false);
        if (!prep.Success)
        {
            return RegistryMutationResult.Fail(prep.Error!);
        }

        var result = await commands.AddModelAsync(prep.Model!, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        await ApplySecretAsync(prep, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<RegistryMutationResult> UpdateAsync(
        string id,
        AdminModelWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var model = request.Model ?? throw new ArgumentException("Model is required.");

        var prep = PrepareModel(model, request.ApiKey, request.ClearApiKey, isUpdate: true);
        if (!prep.Success)
        {
            return RegistryMutationResult.Fail(prep.Error!);
        }

        var result = await commands.UpdateModelAsync(id, prep.Model!, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        await ApplySecretAsync(prep, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public IReadOnlyList<AdminModelListItem> ListModels()
    {
        return commands.ListModels()
            .Select(m => new AdminModelListItem
            {
                Model = m,
                HasUpstreamCredential = HasCredential(m)
            })
            .ToList();
    }

    private bool HasCredential(ModelConfig model)
    {
        if (model.UpstreamAuth is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar))
        {
            return true;
        }

        if (UpstreamSecretRefs.TryParseModelId(model.UpstreamAuth.SecretRef, out var modelId))
        {
            return secretStore.ExistsAsync(modelId).GetAwaiter().GetResult();
        }

        return false;
    }

    private static PrepResult PrepareModel(
        ModelConfig model,
        string? apiKey,
        bool clearApiKey,
        bool isUpdate)
    {
        var normalized = new ModelConfig
        {
            Id = model.Id?.Trim() ?? string.Empty,
            Url = model.Url?.Trim() ?? string.Empty,
            MaxContextLength = model.MaxContextLength > 0 ? model.MaxContextLength : 8192,
            Aliases = model.Aliases ?? [],
            PublicAccess = model.PublicAccess,
        };

        if (string.IsNullOrWhiteSpace(normalized.Id) || string.IsNullOrWhiteSpace(normalized.Url))
        {
            return PrepResult.Fail("Model id and url are required.");
        }

        var key = apiKey?.Trim();
        var hasKey = !string.IsNullOrWhiteSpace(key);

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
            return PrepResult.Ok(normalized, secretToStore: key, clearSecret: false);
        }

        if (model.UpstreamAuth is not null)
        {
            if (!string.Equals(model.UpstreamAuth.Type, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                return PrepResult.Fail("upstreamAuth.type must be 'bearer'.");
            }

            var hasEnv = !string.IsNullOrWhiteSpace(model.UpstreamAuth.EnvVar);
            var hasRef = !string.IsNullOrWhiteSpace(model.UpstreamAuth.SecretRef);

            if (hasEnv)
            {
                if (!EnvVarNameValidator.TryValidate(model.UpstreamAuth.EnvVar, out _, out var envError))
                {
                    return PrepResult.Fail(envError!);
                }

                normalized.UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    EnvVar = model.UpstreamAuth.EnvVar.Trim()
                };
            }
            else if (hasRef)
            {
                normalized.UpstreamAuth = new UpstreamAuthConfig
                {
                    Type = "bearer",
                    SecretRef = model.UpstreamAuth.SecretRef.Trim()
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
        else
        {
            normalized.UpstreamAuth = null;
        }

        if (!ModelConfigValidation.TryValidate(normalized, out var validationError))
        {
            return PrepResult.Fail(validationError!);
        }

        return PrepResult.Ok(normalized, secretToStore: null, clearSecret: false);
    }

    private async Task ApplySecretAsync(PrepResult prep, CancellationToken cancellationToken)
    {
        if (prep.ClearSecret && !string.IsNullOrWhiteSpace(prep.Model?.Id))
        {
            await secretStore.DeleteAsync(prep.Model.Id, cancellationToken).ConfigureAwait(false);
            audit.LogAdminAction(
                "upstream_secret.deleted",
                new AuditLogEntry(null, null, new { modelId = prep.Model.Id }));
            return;
        }

        if (!string.IsNullOrWhiteSpace(prep.SecretToStore) && !string.IsNullOrWhiteSpace(prep.Model?.Id))
        {
            await secretStore.PutAsync(prep.Model.Id, prep.SecretToStore, cancellationToken).ConfigureAwait(false);
            audit.LogAdminAction(
                "upstream_secret.updated",
                new AuditLogEntry(null, null, new { modelId = prep.Model.Id }));
        }
    }

    private static bool LooksLikeInvalidApiKeyPlacement(string value)
    {
        if (EnvVarNameValidator.TryValidate(value, out _, out _))
        {
            return true;
        }

        return false;
    }

    private sealed record PrepResult(bool Success, ModelConfig? Model, string? SecretToStore, bool ClearSecret, string? Error)
    {
        public static PrepResult Ok(ModelConfig model, string? secretToStore, bool clearSecret) =>
            new(true, model, secretToStore, clearSecret, null);

        public static PrepResult Fail(string error) =>
            new(false, null, null, false, error);
    }
}
