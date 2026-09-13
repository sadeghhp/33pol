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
    private const string AtKeyword = "at";
    private const string OwnedPrefix = "Pol33.";

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
    /// <remarks>
    /// Parsed by hand rather than by regex. This runs on the error path, and a
    /// <see cref="System.Text.RegularExpressions.Regex"/> match timeout is wall-clock, not
    /// work-based: it fires whenever the thread loses its slice for longer than the budget, however
    /// little the match actually did. Recording a fault must not fail because the box was busy.
    /// </remarks>
    private static string FirstOwnedFrame(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return None;
        }

        var remaining = stackTrace.AsSpan();
        while (!remaining.IsEmpty)
        {
            var newline = remaining.IndexOf('\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? default : remaining[(newline + 1)..];

            var frame = OwnedFrame(line);
            if (frame is not null)
            {
                return StripCompilerNames(frame);
            }
        }

        return None;
    }

    /// <summary>
    /// The qualified name of the first owned frame on one stack-trace line, or <c>null</c> when the
    /// line names no owned frame. Equivalent to the former
    /// <c>at\s+(Pol33\.[A-Za-z0-9_.&lt;&gt;`+]+\.[A-Za-z0-9_&lt;&gt;`]+)</c> match, including its
    /// greedy choice of the last dot that still leaves a member segment behind it.
    /// </summary>
    /// <remarks>
    /// One left-to-right pass. A name run never spans the whitespace that has to precede the next
    /// candidate, so the scan stays linear in the length of the line.
    /// </remarks>
    private static string? OwnedFrame(ReadOnlySpan<char> line)
    {
        for (var start = 0; start + AtKeyword.Length <= line.Length; start++)
        {
            var found = line[start..].IndexOf(AtKeyword, StringComparison.Ordinal);
            if (found < 0)
            {
                return null;
            }

            start += found;

            // "at" has to be followed by at least one space before the frame name.
            var cursor = start + AtKeyword.Length;
            var afterSpace = cursor;
            while (afterSpace < line.Length && char.IsWhiteSpace(line[afterSpace]))
            {
                afterSpace++;
            }

            if (afterSpace == cursor)
            {
                continue;
            }

            var candidate = line[afterSpace..];
            if (!candidate.StartsWith(OwnedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var frame = QualifiedName(candidate);
            if (frame is not null)
            {
                return frame;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads <c>Pol33.Some.Type.Member</c> off the front of <paramref name="candidate"/>, stopping
    /// at the first character that cannot appear in a frame name. Returns <c>null</c> when nothing
    /// follows the namespace but a single segment, since a type without a member is not a frame.
    /// </summary>
    private static string? QualifiedName(ReadOnlySpan<char> candidate)
    {
        var nameEnd = OwnedPrefix.Length;
        while (nameEnd < candidate.Length && IsNameChar(candidate[nameEnd]))
        {
            nameEnd++;
        }

        // The last dot inside the run that still has a member segment after it, mirroring the
        // greedy quantifier the pattern used. Everything past that member (an argument list, the
        // "in File.cs:line 42" tail) is deliberately dropped.
        var memberDot = -1;
        for (var i = nameEnd - 1; i > OwnedPrefix.Length; i--)
        {
            if (candidate[i] == '.' && i + 1 < candidate.Length && IsMemberChar(candidate[i + 1]))
            {
                memberDot = i;
                break;
            }
        }

        if (memberDot < 0)
        {
            return null;
        }

        var memberEnd = memberDot + 1;
        while (memberEnd < candidate.Length && IsMemberChar(candidate[memberEnd]))
        {
            memberEnd++;
        }

        return candidate[..memberEnd].ToString();
    }

    // Namespace and type separators plus the mangling the compiler emits: "<>c__DisplayClass3_0",
    // "<InvokeAsync>d__12", the "`1" of a generic arity, the "+" of a nested type. Deliberately
    // ASCII-only, as the pattern's character classes were.
    private static bool IsNameChar(char c) => c is '.' or '+' || IsMemberChar(c);

    private static bool IsMemberChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '<' or '>' or '`';

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

    // No match timeout on any of these. Each is linear in its input, and the inputs are capped
    // upstream (GatewayErrorTrackingOptions caps a message at 1000 chars and a stack trace at
    // 8000), so a timeout bought no protection against runaway backtracking. What it did buy was
    // failure: the budget is wall-clock, so a match that needs microseconds still throws
    // RegexMatchTimeoutException if the thread loses its slice to a GC pause or a busy box. That
    // turned recording a fault into a second fault. Bound the input, not the clock.
    [GeneratedRegex(@"req_[0-9a-fA-F]{32}")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b(https?)://([^/\s""']+)[^\s""']*")]
    private static partial Regex UrlPattern();

    // A digit run not glued to a letter (optionally through a hyphen): "HTTP 401" and "after 30s"
    // normalize, "gpt-4o" and "Qwen3" do not.
    [GeneratedRegex(@"(?<![A-Za-z]-?)\d+")]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"<>c__DisplayClass\d+_\d+\.")]
    private static partial Regex DisplayClassPattern();

    // "<Method>d__12.MoveNext" / "<Method>b__0" -> "Method"
    [GeneratedRegex(@"<([A-Za-z0-9_]+)>[a-z]__\d+(?:_\d+)?(?:\.MoveNext)?")]
    private static partial Regex StateMachinePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
