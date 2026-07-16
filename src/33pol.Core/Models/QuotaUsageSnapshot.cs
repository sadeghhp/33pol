namespace Pol33.Core.Models;

/// <summary>
/// A durable snapshot of one partition's monthly token usage. Persisted so per-key monthly quota
/// usage is not silently reset (and the limit bypassed) when the gateway container is recreated.
/// </summary>
/// <param name="PartitionKey">The quota partition (typically the API key / tenant partition).</param>
/// <param name="Period">The billing month the usage applies to, formatted <c>yyyy-MM</c> (UTC).</param>
/// <param name="Used">Tokens consumed in the period.</param>
public sealed record QuotaUsageSnapshot(string PartitionKey, string Period, long Used);
