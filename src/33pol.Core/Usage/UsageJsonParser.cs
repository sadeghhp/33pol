using System.Text;
using System.Text.Json;

namespace Pol33.Core.Usage;

public static class UsageJsonParser
{
    /// <summary>
    /// Reads a <c>usage</c> object from a JSON response body.
    /// </summary>
    /// <remarks>
    /// Every numeric access is guarded by <see cref="JsonValueKind"/> and <c>TryGetInt64</c>. The
    /// previous implementation called <c>GetInt64()</c> after a bare <c>TryGetProperty</c>, so a
    /// <c>null</c>, string, or fractional token count threw <see cref="InvalidOperationException"/>
    /// or <see cref="FormatException"/> — neither of which the surrounding <c>catch (JsonException)</c>
    /// caught. Since parsing runs from the response stream's Dispose, that exception escaped after
    /// the body had already been written to the client and faulted an otherwise-successful request.
    /// </remarks>
    public static ParsedUsage Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            return ParsedUsage.None;
        }

        try
        {
            var reader = new Utf8JsonReader(json);
            using var doc = JsonDocument.ParseValue(ref reader);

            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return ParsedUsage.None;
            }

            return FromUsageElement(usage);
        }
        catch (JsonException)
        {
            return ParsedUsage.None;
        }
    }

    /// <summary>Reads the token counts out of an already-located <c>usage</c> object.</summary>
    private static ParsedUsage FromUsageElement(JsonElement usage)
    {
        var hasPrompt = TryReadTokenCount(usage, "prompt_tokens", out var promptTokens);
        var hasCompletion = TryReadTokenCount(usage, "completion_tokens", out var completionTokens);
        var hasTotal = TryReadTokenCount(usage, "total_tokens", out var totalTokens);

        // Exactly one side plus a total: the other side is known exactly (total - present). Some
        // OpenAI-compatible servers omit a zero-valued field or report only prompt/total, and
        // billing the missing side as zero under-charged when it was derivable.
        if (hasPrompt != hasCompletion && hasTotal)
        {
            if (hasPrompt && totalTokens >= promptTokens)
            {
                completionTokens = totalTokens - promptTokens;
            }
            else if (hasCompletion && totalTokens >= completionTokens)
            {
                promptTokens = totalTokens - completionTokens;
            }
        }

        if ((hasPrompt || hasCompletion) && (promptTokens > 0 || completionTokens > 0))
        {
            return ParsedUsage.Split(promptTokens, completionTokens);
        }

        // Only a combined total. Deliberately NOT folded into prompt tokens: the split is
        // genuinely unknown and pricing must be told so.
        if (hasTotal && totalTokens > 0)
        {
            return ParsedUsage.TotalOnly(totalTokens);
        }

        return ParsedUsage.None;
    }

    private static ReadOnlySpan<byte> UsagePropertyToken => "\"usage\""u8;

    /// <summary>
    /// Recovers usage from a <em>fragment</em> of a JSON body — bytes that are not a complete,
    /// parseable document — by locating the last <c>"usage"</c> property and parsing only the object
    /// that follows it.
    /// </summary>
    /// <remarks>
    /// This exists because a non-streaming response can be far larger than any bounded capture
    /// buffer (a batch embeddings body is routinely megabytes). Retaining only a truncated prefix
    /// made <see cref="Parse"/> throw and the request record zero usage — i.e. go unbilled entirely.
    /// The <c>usage</c> object sits at the end of an OpenAI-shaped body, so scanning the retained
    /// tail recovers it exactly, without needing the surrounding document to be well-formed.
    /// </remarks>
    public static ParsedUsage ParseUsageFragment(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            return ParsedUsage.None;
        }

        // Scan candidates newest-first: the terminal usage object is the authoritative one, and an
        // earlier match may be a truncated or nested occurrence.
        var searchLimit = json.Length;
        while (searchLimit >= UsagePropertyToken.Length)
        {
            var index = json[..searchLimit].LastIndexOf(UsagePropertyToken);
            if (index < 0)
            {
                return ParsedUsage.None;
            }

            searchLimit = index;

            var objectStart = FindObjectStart(json, index + UsagePropertyToken.Length);
            if (objectStart < 0)
            {
                continue;
            }

            var objectEnd = FindMatchingBrace(json, objectStart);
            if (objectEnd < 0)
            {
                continue;
            }

            try
            {
                var reader = new Utf8JsonReader(json[objectStart..(objectEnd + 1)]);
                using var doc = JsonDocument.ParseValue(ref reader);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var parsed = FromUsageElement(doc.RootElement);
                    if (parsed.HasUsage)
                    {
                        return parsed;
                    }
                }
            }
            catch (JsonException)
            {
                // Not a usable occurrence; keep scanning backwards.
            }
        }

        return ParsedUsage.None;
    }

    /// <summary>Expects <c>: {</c> after a property name, tolerating whitespace. Returns the index of the brace.</summary>
    private static int FindObjectStart(ReadOnlySpan<byte> json, int from)
    {
        var i = SkipWhitespace(json, from);
        if (i >= json.Length || json[i] != (byte)':')
        {
            return -1;
        }

        i = SkipWhitespace(json, i + 1);
        return i < json.Length && json[i] == (byte)'{' ? i : -1;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> json, int from)
    {
        var i = from;
        while (i < json.Length && json[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Returns the index of the brace closing the object that opens at <paramref name="openIndex"/>,
    /// or -1 if the fragment ends first. String literals are skipped so a brace inside a string
    /// value cannot unbalance the count.
    /// </summary>
    private static int FindMatchingBrace(ReadOnlySpan<byte> json, int openIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = openIndex; i < json.Length; i++)
        {
            var c = json[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == (byte)'\\')
                {
                    escaped = true;
                }
                else if (c == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case (byte)'"':
                    inString = true;
                    break;
                case (byte)'{':
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// Reads one token count. Rejects anything that is not a non-negative integer that fits in
    /// <see cref="long"/>: nulls, strings, fractions, negatives and values beyond Int64 all mean the
    /// upstream did not report a usable count, not that the count is zero.
    /// </summary>
    private static bool TryReadTokenCount(JsonElement usage, string propertyName, out long value)
    {
        value = 0;

        if (!usage.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        // TryGetInt64 fails for fractional values and for anything outside Int64's range.
        if (!element.TryGetInt64(out var parsed) || parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Parses OpenAI-style SSE bodies; uses the last <c>data:</c> line that carries a usable
    /// <c>usage</c> object.
    /// </summary>
    public static ParsedUsage ParseSseText(string sseText)
    {
        if (string.IsNullOrWhiteSpace(sseText))
        {
            return ParsedUsage.None;
        }

        // Scanned newest-first: the terminal usage frame is at the end of the stream, and the head of
        // a retained tail buffer is usually a partial frame that simply fails to parse.
        var lines = sseText.Split('\n');
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].AsSpan().Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload.SequenceEqual("[DONE]"))
            {
                continue;
            }

            var parsed = Parse(Encoding.UTF8.GetBytes(payload.ToString()));
            if (parsed.HasUsage)
            {
                return parsed;
            }
        }

        return ParsedUsage.None;
    }

    /// <summary>
    /// Backwards-compatible shape for callers that only need a prompt/completion split.
    /// </summary>
    /// <remarks>
    /// Reports <c>false</c> for total-only usage: collapsing it into the prompt field is exactly the
    /// mis-pricing this overload's callers must not reintroduce. Use <see cref="Parse"/> to handle
    /// total-only usage explicitly.
    /// </remarks>
    public static bool TryParseUsage(ReadOnlySpan<byte> json, out long promptTokens, out long completionTokens)
    {
        var parsed = Parse(json);
        promptTokens = parsed.PromptTokens;
        completionTokens = parsed.CompletionTokens;
        return parsed.Kind == UsageParseKind.Split;
    }

    /// <inheritdoc cref="TryParseUsage(ReadOnlySpan{byte}, out long, out long)"/>
    public static bool TryParseUsageFromSseText(string sseText, out long promptTokens, out long completionTokens)
    {
        var parsed = ParseSseText(sseText);
        promptTokens = parsed.PromptTokens;
        completionTokens = parsed.CompletionTokens;
        return parsed.Kind == UsageParseKind.Split;
    }
}
