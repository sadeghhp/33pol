namespace Pol33.Core.RateLimiting;

/// <summary>
/// Builds the bucket keys and the composite configuration keys, so the request path and the admin
/// path can never disagree about what a rule is called.
/// </summary>
/// <remarks>
/// Every bucket key carries a scope prefix. Without one, a tenant whose id happens to equal a model
/// id would share that model's bucket, and a per-key rule would land in the same table as a
/// per-tenant rule — cross-scope collisions that are invisible until they silently limit the wrong
/// traffic.
/// </remarks>
public static class RateLimitKeys
{
    /// <summary>Separates the two halves of a combined key, in config and in bucket keys alike.</summary>
    public const char PairSeparator = '|';

    /// <summary>The single partition every request shares in <see cref="RateLimitScope.Global"/>.</summary>
    public const string GlobalPartition = "g:*";

    public static string Tenant(string partitionKey) => "t:" + partitionKey;

    public static string ApiKey(string apiKeyId) => "k:" + apiKeyId;

    public static string Model(string modelId) => "m:" + modelId;

    public static string TenantModel(string partitionKey, string modelId) =>
        "tm:" + partitionKey + PairSeparator + modelId;

    public static string ApiKeyModel(string apiKeyId, string modelId) =>
        "km:" + apiKeyId + PairSeparator + modelId;

    /// <summary>
    /// The configuration key for a combined rule, as an operator writes it: <c>subject|model</c>.
    /// </summary>
    public static string Pair(string subject, string modelId) => subject + PairSeparator + modelId;

    /// <summary>
    /// Splits a combined configuration key. Returns false for a key with no separator or an empty
    /// half, which the validator reports rather than letting it silently never match.
    /// </summary>
    public static bool TrySplitPair(string? key, out string subject, out string modelId)
    {
        subject = string.Empty;
        modelId = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var index = key.IndexOf(PairSeparator);
        if (index <= 0 || index == key.Length - 1)
        {
            return false;
        }

        // Exactly one separator: a model id containing a pipe would otherwise parse into a subject
        // that matches nothing, and the rule would look configured while never applying.
        if (key.IndexOf(PairSeparator, index + 1) >= 0)
        {
            return false;
        }

        subject = key[..index];
        modelId = key[(index + 1)..];
        return true;
    }
}
