namespace Pol33.Core.Security;

public enum ApiKeyValidationFailure
{
    Missing,
    Invalid,
    Expired,
    Revoked,
}

public sealed record ApiKeyValidationResult(
    bool IsSuccess,
    ApiKeyValidationFailure? Failure = null,
    Guid? TenantId = null,
    Guid? ApiKeyId = null,
    string? TenantSlug = null,
    string? PlanSlug = null,
    string? CostCenter = null,
    Pol33.Core.Identity.ApiKeyRole? Role = null)
{
    public static ApiKeyValidationResult Success(
        Guid tenantId,
        Guid apiKeyId,
        string tenantSlug,
        string? planSlug,
        string? costCenter,
        Pol33.Core.Identity.ApiKeyRole role) =>
        new(true, null, tenantId, apiKeyId, tenantSlug, planSlug, costCenter, role);

    public static ApiKeyValidationResult Fail(ApiKeyValidationFailure failure) =>
        new(false, failure);
}
