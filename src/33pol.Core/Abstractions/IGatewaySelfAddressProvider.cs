namespace Pol33.Core.Abstractions;

/// <summary>
/// Answers whether a URL points back at this gateway's own listener.
/// </summary>
/// <remarks>
/// A route whose upstream is the gateway itself is always a misconfiguration, and it is a
/// self-concealing one: the probe asks the gateway for <c>/v1/models</c>, the gateway answers 200 —
/// that path is anonymous — and the backend is scored healthy forever on the strength of the
/// gateway's own reply. The shipped demo registry did exactly this with two routes pointing at
/// <c>http://localhost:8080</c>.
///
/// The listener is an ASP.NET Core concept and the registry may not depend on that assembly, so the
/// question is asked through this abstraction and answered in the host.
/// </remarks>
public interface IGatewaySelfAddressProvider
{
    /// <summary>
    /// True when <paramref name="url"/> resolves to an address this process is listening on.
    /// False when it does not, when the URL cannot be parsed, or when the listener is not yet known —
    /// never a guess, because a false positive takes a working backend out of service.
    /// </summary>
    bool IsSelf(string? url);
}
