namespace Pol33.Core.Security;

public enum ApiKeyValidationFailure
{
    /// <summary>No credential was presented.</summary>
    Missing,

    /// <summary>A credential was presented and it matches no stored key.</summary>
    Invalid,

    Expired,
    Revoked,

    /// <summary>The key matched a stored record whose tenant is deactivated.</summary>
    TenantInactive,
}

public static class ApiKeyValidationFailureExtensions
{
    /// <summary>
    /// True when the presented key matched a stored record that has stopped working, as opposed to
    /// one the gateway has never issued.
    /// </summary>
    /// <remarks>
    /// This is the line between "your credential was withdrawn" and "that is not one of our keys",
    /// and only the gateway can tell them apart — both look identical to the caller. Routes that
    /// serve anonymous callers use it to decide whether an unusable key may be ignored: an
    /// unrecognised key grants nothing a bare request would not already get, so it is treated as no
    /// key at all, while a revoked, expired, or deactivated-tenant key must fail loudly rather than
    /// answer 200 and leave its holder believing the credential still works.
    /// </remarks>
    public static bool IsRecognizedCredential(this ApiKeyValidationFailure failure) =>
        failure is ApiKeyValidationFailure.Expired
            or ApiKeyValidationFailure.Revoked
            or ApiKeyValidationFailure.TenantInactive;
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
