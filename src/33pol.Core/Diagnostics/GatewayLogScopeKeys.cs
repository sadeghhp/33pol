namespace Pol33.Core.Diagnostics;

/// <summary>
/// Property names the gateway puts into <c>ILogger.BeginScope</c> and the admin log sink reads
/// back out. Shared constants rather than string literals at both ends: a typo on either side is
/// silent — the field simply stays null, which is exactly how the Logs tab's Request ID column
/// ended up permanently empty.
/// </summary>
public static class GatewayLogScopeKeys
{
    public const string RequestId = "GatewayRequestId";

    public const string ModelId = "ModelId";

    public const string TenantId = "TenantId";
}
