namespace Pol33.Core.RateLimiting;

/// <summary>
/// One rate-limit tier, applied to a single partition — a tenant, an API key, a model, or a
/// combination of them (see <see cref="RateLimitScope"/>).
/// </summary>
/// <param name="Rpm">
/// Sustained requests per minute. The token bucket refills at <c>Rpm / 60</c> per second, so this is
/// the long-run ceiling.
/// <b>Zero means this rule does not limit the request rate at all</b>, which is how a rule that
/// exists only to cap concurrency is expressed. The default tier is still floored at 1 by the
/// resolver, because a default of zero would silently disable the gateway's only universal limit;
/// use the gateway-wide master switch to turn enforcement off.
/// </param>
/// <param name="Burst">
/// Extra tokens the bucket may hold above <paramref name="Rpm"/>, so capacity is
/// <c>Rpm + Burst</c>. This is what an idle partition may spend at once. Zero means no burst
/// allowance beyond one minute's worth of tokens.
/// </param>
/// <param name="MaxConcurrentStreams">
/// Streaming responses the partition may have open at once. <b>Zero means unlimited</b>, not
/// "streaming denied" — there is no per-tier way to refuse streaming, and a tier with the cap left
/// at zero is not throttled on concurrency at all.
/// </param>
public sealed record RateLimitPolicy(int Rpm, int Burst, int MaxConcurrentStreams)
{
    public static RateLimitPolicy Default { get; } = new(60, 10, 5);

    /// <summary>A rule that enforces neither control. Emitted for a scope the operator left unset.</summary>
    public static RateLimitPolicy Unlimited { get; } = new(0, 0, 0);

    /// <summary>Total tokens the bucket may hold. Zero or less means the rate control is off.</summary>
    public int Capacity => Rpm + Burst;

    public bool EnforcesRate => Capacity > 0;

    public bool EnforcesConcurrency => MaxConcurrentStreams > 0;

    public bool EnforcesNothing => !EnforcesRate && !EnforcesConcurrency;

    /// <summary>
    /// This tier reduced to <paramref name="factor"/> of its configured rate, for adaptive
    /// enforcement.
    /// </summary>
    /// <remarks>
    /// <para>The factor only ever shrinks a tier: it is clamped to <c>(0, 1]</c> here as well as at
    /// the governor, so no adaptive path can hand a partition more than the operator configured.
    /// That invariant is the whole safety argument for letting a background process move these
    /// numbers at all, so it is enforced at both ends rather than trusted from one.</para>
    ///
    /// <para>Burst scales with the rate, keeping the shape of the tier — a reduced tier that kept
    /// its full burst would still admit the original spike and only throttle afterwards, which is
    /// the opposite of what shedding load needs. Both floor at 1 and 0 respectively so a heavily
    /// reduced tier still admits traffic rather than becoming a total outage. Concurrency is left
    /// alone: it is a bound on simultaneous work, and the load signals that drive adaptation are
    /// already derived from it.</para>
    /// </remarks>
    public RateLimitPolicy Scale(double factor)
    {
        if (factor >= 1.0 || !EnforcesRate)
        {
            return this;
        }

        var clamped = Math.Clamp(factor, 0.0, 1.0);
        return this with
        {
            Rpm = Math.Max(1, (int)Math.Round(Rpm * clamped)),
            Burst = Math.Max(0, (int)Math.Round(Burst * clamped)),
        };
    }
}
