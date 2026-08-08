namespace Pol33.Core.Security;

public static class GatewayAuthPolicies
{
    public const string Inference = "GatewayInference";

    public const string Admin = "GatewayAdmin";
}

public static class GatewayAuthSchemes
{
    public const string ApiKey = "ApiKey";
}

public static class GatewayAuthClaims
{
    public const string TenantId = "tenant_id";

    public const string ApiKeyId = "api_key_id";

    public const string Role = "api_key_role";
}

public static class GatewayAuthContextItems
{
    /// <summary>
    /// Set when a credential was presented and rejected, carrying the error code to report.
    /// </summary>
    /// <remarks>
    /// Distinguishes "no key supplied" from "a key was supplied and it is invalid". Only the former
    /// may fall through to the anonymous paths (public models, model listing); treating the latter
    /// as anonymous returned 200 to callers holding a revoked or expired key, so nothing downstream
    /// could tell that the credential had stopped working.
    /// </remarks>
    public const string AuthFailureCode = "Gateway:AuthFailureCode";
}
