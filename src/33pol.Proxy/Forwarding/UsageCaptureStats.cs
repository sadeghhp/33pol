namespace Pol33.Proxy.Forwarding;

/// <summary>
/// What the gateway observed of a response body, independent of whether usage could be parsed from
/// it.
/// </summary>
/// <param name="FrameCount">
/// Number of complete SSE frames forwarded to the client. For OpenAI-style streaming each frame
/// carries roughly one token of content, which is what makes it usable as a completion-token
/// estimate when the authoritative usage frame never arrives.
/// </param>
/// <param name="TotalBytes">Total bytes forwarded, used to tell "produced output" from "produced nothing".</param>
internal readonly record struct UsageCaptureStats(long FrameCount, long TotalBytes)
{
    /// <summary>
    /// True when the upstream actually streamed content. A cancelled request that produced nothing
    /// must not have usage fabricated for it.
    /// </summary>
    public bool ProducedOutput => TotalBytes > 0 && FrameCount > 0;
}
