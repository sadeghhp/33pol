namespace Pol33.Core.Errors;

public static class GatewayHeaders
{
    public const string RequestId = "X-Request-Id";
    public const string ErrorCode = "X-33pol-Error-Code";
    public const string RetryAfter = "Retry-After";

    public const string QuotaWarning = "X-33pol-Quota-Warning";
}
