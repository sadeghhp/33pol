using Pol33.Core.RateLimiting;

namespace Pol33.Core.Abstractions;

public interface IDistributedRateLimitStore
{
    RateLimitAcquireResult TryAcquireRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now);

    /// <summary>
    /// Admits a request against every rule in <paramref name="rules"/> at once.
    /// </summary>
    /// <remarks>
    /// <para>The scopes compose rather than override: a request is admitted only if every rule
    /// admits it, so adding a narrower rule can only ever tighten what a caller may do. That is what
    /// makes the outcome deterministic no matter what order an operator configures things in.</para>
    ///
    /// <para>Admission is all-or-nothing. Tokens are taken in the order given, and if a later rule
    /// refuses, the tokens already taken are handed back before returning — otherwise a caller
    /// blocked by its narrowest limit would still burn its tenant-wide and gateway-wide budget on
    /// every attempt, and a single over-limit key could rate-limit its whole tenant. The refund is
    /// exact but not instantaneous: for the few microseconds between the take and the refund a
    /// concurrent request can see the token missing, which costs it a token it would otherwise have
    /// had. That is bounded by the number of in-flight rejections and self-correcting, where the
    /// alternative — locking every bucket in the set for the duration — is a cross-partition
    /// bottleneck on the hot path.</para>
    ///
    /// <para>On success the result describes the <em>tightest</em> rule (the smallest fraction of
    /// its budget remaining), which is the one a client should pace itself against. On failure it
    /// describes the rule that refused.</para>
    /// </remarks>
    RateLimitAcquireResult TryAcquireAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now);

    /// <summary>
    /// Hands back one token to each rule's partition, for a request that was admitted by these rules
    /// and then refused by a later stage.
    /// </summary>
    /// <remarks>
    /// The gateway evaluates model-independent scopes before it parses the body and model-dependent
    /// scopes after, so a rejection in the second stage has to undo the first. Refunding is capped at
    /// the bucket's capacity, so a stray refund can never inflate a partition past its tier.
    /// </remarks>
    void RefundAll(ReadOnlySpan<RateLimitRule> rules, DateTimeOffset now);

    /// <summary>
    /// Reports what <see cref="TryAcquireRequest"/> would answer right now <em>without</em> taking a
    /// token, for callers that only debit the partition once the request's outcome is known.
    /// </summary>
    /// <remarks>
    /// Refilling is still applied, so a peek never reports a stale fill. Because the check and the
    /// later debit are two separate operations, concurrent callers can each be told there is a token
    /// and then debit past the limit — acceptable where the peek guards a rejection path rather than
    /// the metered one.
    /// </remarks>
    RateLimitAcquireResult PeekRequest(
        string partitionKey,
        RateLimitPolicy policy,
        DateTimeOffset now);

    /// <summary>Takes one token without a limit check, creating the partition if it does not exist.</summary>
    /// <remarks>
    /// The counterpart to <see cref="PeekRequest"/>: the decision was already made, this only records
    /// the cost. A bucket with nothing left goes into debt rather than flooring at zero, bounded at
    /// one full window — because a peek does not consume, every request that peeked before any of them
    /// debited was admitted on the same token, and writing the surplus off let one token buy a whole
    /// round of concurrent requests instead of one request. Owing it means the debt is refilled away
    /// before the next token is available, so the sustained rate is the configured one.
    /// </remarks>
    void DebitRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now);

    /// <summary>Hands one token back to a single partition, capped at its capacity.</summary>
    /// <remarks>
    /// The single-partition counterpart to <see cref="RefundAll"/>, for a budget that is taken
    /// speculatively and released once the outcome shows it need not have been spent — the
    /// auth-failure limiter's validation allowance, which charges for a lookup and gives the token
    /// back when the lookup was answered from cache.
    /// </remarks>
    void RefundRequest(string partitionKey, RateLimitPolicy policy, DateTimeOffset now);

    RateLimitAcquireResult TryAcquireStreamSlot(string partitionKey, RateLimitPolicy policy);

    /// <summary>
    /// Takes one concurrency slot in every rule that caps concurrency, releasing the ones already
    /// taken if a later rule is full.
    /// </summary>
    /// <param name="rules">The request's rule set; rules with no concurrency cap are skipped.</param>
    /// <param name="held">
    /// The rules a slot was actually taken in, to be passed to <see cref="ReleaseStreamSlots"/> when
    /// the response ends. Empty on rejection — nothing is held when the call fails.
    /// </param>
    RateLimitAcquireResult TryAcquireStreamSlots(
        ReadOnlySpan<RateLimitRule> rules,
        out RateLimitSlotLease held);

    void ReleaseStreamSlot(string partitionKey);

    /// <summary>Releases every slot taken by a successful <see cref="TryAcquireStreamSlots"/>.</summary>
    void ReleaseStreamSlots(RateLimitSlotLease held);

    /// <summary>
    /// Sweeps partitions no request has touched inside the retention window and enforces the
    /// partition ceiling. Called on a timer by the maintenance service rather than from the request
    /// path.
    /// </summary>
    /// <returns>How many partitions were removed, for the maintenance service's log and metric.</returns>
    int Compact(DateTimeOffset now);

    /// <summary>Live partition counts per dimension, for the ceiling gauge and the admin report.</summary>
    RateLimitStoreStats GetStats();
}

/// <summary>The concurrency slots one request holds, so exactly those can be given back.</summary>
/// <remarks>
/// A fixed-size buffer rather than a list: the rule set is capped at
/// <see cref="RateLimitRuleBuffer.MaxRules"/>, so the lease never needs to allocate, and a streaming
/// response holds one of these for its whole lifetime.
/// </remarks>
public readonly struct RateLimitSlotLease
{
    private readonly string[]? _keys;

    public RateLimitSlotLease(string[]? keys, int count)
    {
        _keys = keys;
        Count = count;
    }

    public int Count { get; }

    public ReadOnlySpan<string> Keys =>
        _keys is null ? ReadOnlySpan<string>.Empty : _keys.AsSpan(0, Count);

    public static RateLimitSlotLease Empty => default;
}

/// <param name="RequestPartitions">Live token buckets.</param>
/// <param name="StreamPartitions">Live concurrency-slot states.</param>
/// <param name="MaxPartitions">The configured ceiling each dimension is held to.</param>
/// <param name="ForcedEvictions">
/// Partitions evicted since start with budget still spent, each of which starts full again on its
/// next request.
/// </param>
/// <remarks>
/// <see cref="ForcedEvictions"/> is the one number here that says enforcement has stopped being
/// correct rather than merely being busy: past the partition ceiling the sweep has to drop buckets
/// that were still holding a caller to its tier, and dropping one hands that caller a full budget it
/// did not earn. It was reported only as a log line, emitted once when the condition started — so the
/// single state in which limits are silently not enforced was the one state nothing could alert on.
/// </remarks>
public readonly record struct RateLimitStoreStats(
    int RequestPartitions,
    int StreamPartitions,
    int MaxPartitions,
    long ForcedEvictions = 0);
