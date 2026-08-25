using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Pol33.Core.Models;

namespace Pol33.Core.Diagnostics;

/// <summary>
/// Collapses occurrences of the same underlying failure onto one stable key.
/// </summary>
/// <remarks>
/// The whole value of the Errors tab rests on this being neither too coarse nor too fine. Too
/// coarse and two unrelated faults merge into one row; too fine and a single fault shatters into
/// thousands of one-occurrence groups, which is just the flat list again. The rules that matter:
/// <list type="bullet">
/// <item>Route <em>kind</em>, never the raw path — otherwise per-tenant path variants split a group.</item>
/// <item>Message with ids, GUIDs and numbers normalized away, since those vary per occurrence.</item>
/// <item>Upstream host excluded — one failing replica out of five must not look like a distinct fault.</item>
/// <item>Source included, so the same failure seen by two capture points shows as two honest groups
/// rather than one double-counted total.</item>
/// </list>
/// </remarks>
public static partial class GatewayErrorFingerprint
{
    private const char Separator = '\u001f';
    private const int MaxNormalizedMessageLength = 200;
    private const string None = "none";

    /// <summary>Computes the fingerprint for a record. Called by the recorder, never by call sites.</summary>
    public static string Compute(GatewayErrorRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var builder = new StringBuilder();
        Append(builder, record.Level);
        Append(builder, $"{record.Source}:{record.Category}");
        Append(builder, record.EventCode);
        Append(builder, record.StatusCode.ToString(CultureInfo.InvariantCulture));
        Append(builder, record.ModelId);
        Append(builder, record.ExceptionType);
        Append(builder, record.RouteKind);
        Append(builder, NormalizeMessage(record.Message));
        Append(builder, FirstOwnedFrame(record.StackTrace));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Strips the parts of a message that differ between occurrences of one fault: request ids,
    /// GUIDs, free-standing numbers, and the variable tail of absolute URLs. Digits that are part of
    /// a name (<c>gpt-4o</c>, <c>Qwen3</c>, <c>x86</c>) are kept — they identify <em>which</em>
    /// model or component failed, and collapsing them merged unrelated faults into one group.
    /// </summary>
    public static string NormalizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return None;
        }

        var normalized = message;
        normalized = RequestIdPattern().Replace(normalized, "#req");
        normalized = GuidPattern().Replace(normalized, "#guid");
        normalized = UrlPattern().Replace(normalized, "$1://$2");
        normalized = NumberPattern().Replace(normalized, "#");
        normalized = WhitespacePattern().Replace(normalized, " ").Trim();

        if (normalized.Length > MaxNormalizedMessageLength)
        {
            normalized = normalized[..MaxNormalizedMessageLength];
        }

        return normalized.Length == 0 ? None : normalized.ToLowerInvariant();
    }

    /// <summary>
    /// The first frame in the gateway's own code, without file or line. Line numbers move with
    /// every edit, so including them would silently re-fingerprint every existing fault on release.
    /// </summary>
    private static string FirstOwnedFrame(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return None;
        }

        foreach (var line in stackTrace.Split('\n'))
        {
            var match = OwnedFramePattern().Match(line);
            if (match.Success)
            {
                return StripCompilerNames(match.Groups[1].Value);
            }
        }

        return None;
    }

    /// <summary>
    /// <c>Ns.Type.&lt;&gt;c__DisplayClass3_0.&lt;Method&gt;b__0</c> and
    /// <c>Ns.Type.&lt;Method&gt;d__12.MoveNext</c> both become <c>Ns.Type.Method</c>. The numbers in
    /// those names move whenever a lambda or await is added anywhere in the type, which silently
    /// re-keyed every existing group on release.
    /// </summary>
    private static string StripCompilerNames(string frame)
    {
        var stripped = DisplayClassPattern().Replace(frame, string.Empty);
        stripped = StateMachinePattern().Replace(stripped, "$1");
        return stripped;
    }

    private static void Append(StringBuilder builder, string? component)
    {
        if (builder.Length > 0)
        {
            builder.Append(Separator);
        }

        builder.Append(string.IsNullOrWhiteSpace(component) ? None : component.ToLowerInvariant());
    }

    [GeneratedRegex(@"req_[0-9a-fA-F]{32}", RegexOptions.None, 100)]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.None, 100)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b(https?)://([^/\s""']+)[^\s""']*", RegexOptions.None, 100)]
    private static partial Regex UrlPattern();

    // A digit run not glued to a letter (optionally through a hyphen): "HTTP 401" and "after 30s"
    // normalize, "gpt-4o" and "Qwen3" do not.
    [GeneratedRegex(@"(?<![A-Za-z]-?)\d+", RegexOptions.None, 100)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"<>c__DisplayClass\d+_\d+\.", RegexOptions.None, 100)]
    private static partial Regex DisplayClassPattern();

    // "<Method>d__12.MoveNext" / "<Method>b__0" -> "Method"
    [GeneratedRegex(@"<([A-Za-z0-9_]+)>[a-z]__\d+(?:_\d+)?(?:\.MoveNext)?", RegexOptions.None, 100)]
    private static partial Regex StateMachinePattern();

    [GeneratedRegex(@"\s+", RegexOptions.None, 100)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"at\s+(Pol33\.[A-Za-z0-9_.<>`+]+\.[A-Za-z0-9_<>`]+)", RegexOptions.None, 100)]
    private static partial Regex OwnedFramePattern();
}
