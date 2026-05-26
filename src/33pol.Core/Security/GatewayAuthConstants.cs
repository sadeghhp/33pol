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
