namespace Pol33.Core.Models.Overview;

/// <summary>
/// Why the gateway refused a request before (or instead of) forwarding it. The windowed statistics
/// behind the Overview count rejections per reason so the policy card can say <em>which</em> control
/// is biting, not just that one is.
/// </summary>
public enum RejectionReason
{
    RateLimit = 0,
    Quota = 1,
    Budget = 2,
    Bulkhead = 3,
    StreamConcurrency = 4,
    GrantDenied = 5,
    ModelNotFound = 6,
}

public static class RejectionReasonExtensions
{
    /// <summary>The wire label used in the summary JSON and matching the Prometheus <c>reason</c> tag.</summary>
    public static string ToLabel(this RejectionReason reason) => reason switch
    {
        RejectionReason.RateLimit => "rate_limit",
        RejectionReason.Quota => "quota",
        RejectionReason.Budget => "budget",
        RejectionReason.Bulkhead => "bulkhead",
        RejectionReason.StreamConcurrency => "stream_concurrency",
        RejectionReason.GrantDenied => "grant_denied",
        RejectionReason.ModelNotFound => "model_not_found",
        _ => reason.ToString().ToLowerInvariant(),
    };
}
