namespace NextcloudWidget.Core.Translation;

internal static class TranslationText
{
    // Chunks concatenate back to the exact input; each chunk is at most maxChars characters.
    // A split prefers the last newline or whitespace in the second half of a chunk, and never
    // separates a UTF-16 surrogate pair or a CRLF pair.
    internal static IEnumerable<string> Chunks(string body, int maxChars = 2400)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 4);
        return ChunkCore(body, maxChars);
    }

    private static IEnumerable<string> ChunkCore(string body, int maxChars)
    {
        var start = 0;
        while (start < body.Length)
        {
            // The remainder fits within a single chunk.
            if (maxChars >= body.Length - start)
            {
                yield return body.Substring(start);
                yield break;
            }

            var limit = start + maxChars;
            var half = start + maxChars / 2;
            var cut = -1;

            // Prefer a split just after the last newline or whitespace in the second half of the window.
            for (var i = limit - 1; i >= half; i--)
            {
                if (!char.IsWhiteSpace(body[i])) continue;
                // Splitting after a '\r' would break a CRLF pair; split before it instead.
                cut = body[i] == '\r' && body[i + 1] == '\n' ? i : i + 1;
                break;
            }

            if (cut < 0)
            {
                // No break point in the second half: hard cut, stepping back if it would split a pair.
                cut = limit;
                if ((body[cut - 1] == '\r' && body[cut] == '\n')
                    || (char.IsHighSurrogate(body[cut - 1]) && char.IsLowSurrogate(body[cut])))
                    cut--;
            }

            yield return body.Substring(start, cut - start);
            start = cut;
        }
    }
}
