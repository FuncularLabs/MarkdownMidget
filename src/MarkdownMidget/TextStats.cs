namespace MarkdownMidget;

/// <summary>Document counts for the status bar.</summary>
internal readonly record struct TextStats(int Words, int Characters)
{
    /// <summary>
    /// Count words and characters in markdown source.
    ///
    /// A token only counts as a word if it contains a letter or a digit, so the
    /// syntax a writer doesn't think of as words — <c>##</c>, <c>-</c>, <c>&gt;</c>,
    /// <c>|</c>, <c>---</c> — is not added to their total, while <c>**bold**</c> and
    /// <c>[link](url)</c> still count as the words they read as. This is an
    /// approximation of the rendered text, not a parse: getting it exact would mean
    /// serialising the document on every keystroke.
    /// </summary>
    public static TextStats Measure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new TextStats(0, 0);

        var words = 0;
        var inWord = false;
        var wordHasAlphanumeric = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (inWord && wordHasAlphanumeric) words++;
                inWord = false;
                wordHasAlphanumeric = false;
                continue;
            }
            inWord = true;
            if (char.IsLetterOrDigit(c)) wordHasAlphanumeric = true;
        }
        if (inWord && wordHasAlphanumeric) words++;

        return new TextStats(words, text.Length);
    }

    public string ToStatusText() =>
        $"{Words:N0} word{(Words == 1 ? "" : "s")}   {Characters:N0} char{(Characters == 1 ? "" : "s")}";
}
