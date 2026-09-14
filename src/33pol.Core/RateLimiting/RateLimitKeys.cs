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

    /// <summary>
    /// The bucket a caller's admin-API and model-listing requests are counted against.
    /// </summary>
    /// <remarks>
    /// <para>Separate from the caller's inference bucket: the two are different kinds of work, and
    /// letting an operator's console polling eat into the tenant's inference budget — or the reverse —
    /// would make either one's limit unpredictable from the other's traffic.</para>
    ///
    /// <para>Keyed on the credential rather than the tenant, falling back to the tenant partition only
    /// when there is no credential to key on. Every operator key belongs to the one operator tenant,
    /// so a tenant-wide bucket was shared by every console session, wallboard and scripted admin
    /// client at once: a handful of open Overview tabs polling twice a second reach the budget
    /// together, and the answer — a <c>429</c> on every admin call — locks out the console that is the
    /// only place to see what is happening. The tier is an appsettings guard rail read once at
    /// startup, so there is no way to widen it from inside a running process either. Per-credential,
    /// one runaway session spends its own budget and no one else's.</para>
    /// </remarks>
    public static string ControlPlane(string partitionKey, string? apiKeyId = null) =>
        "cp:" + (string.IsNullOrEmpty(apiKeyId) ? partitionKey : apiKeyId);

    public static string Model(string modelId) => "m:" + modelId;

    /// <summary>
    /// The model bucket anonymous callers are counted against, separate from the one authenticated
    /// tenants share.
    /// </summary>
    /// <remarks>
    /// A <c>model</c> rule is the model's own gateway-wide capacity, shared by every caller of it.
    /// With a <c>publicAccess</c> model, unauthenticated traffic charged that same bucket, so
    /// distributed anonymous callers — each individually inside the anonymous tier — could exhaust
    /// the model's budget and every paying tenant saw 429s for a model they were granted. Giving
    /// anonymous traffic its own bucket under the same rule means the two cannot starve each other;
    /// the model's total exposure is bounded by the per-model bulkhead, which is what actually
    /// protects the upstream.
    /// </remarks>
    public static string AnonymousModel(string modelId) => "m!:" + modelId;

    /// <summary>
    /// The allowance for validating credentials from an address that has spent its auth-failure
    /// budget. Namespaced away from that budget so the two cannot be confused for one bucket.
    /// </summary>
    public static string AuthProbe(string authFailurePartitionKey) => "ap:" + authFailurePartitionKey;

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
