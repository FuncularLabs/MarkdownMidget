using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MarkdownMidget;

/// <summary>
/// Where the reader was, expressed as a topic rather than a line number.
/// </summary>
/// <param name="Heading">Normalized text of the nearest heading at or above the
/// viewport, or null if the document has none above that point.</param>
/// <param name="HeadingOrdinal">Which occurrence, when several headings share the
/// same text (e.g. repeated "Notes" sections).</param>
/// <param name="Fingerprint">Normalized text of the first visible line, used to
/// land back on the exact spot within the topic when that line survived.</param>
/// <param name="Ratio">How far down the document we were, 0..1 — the last resort
/// for documents with no headings.</param>
internal sealed record DocAnchor(string? Heading, int HeadingOrdinal, string? Fingerprint, double Ratio);

/// <summary>
/// Captures and restores reading position across an external rewrite of the file.
///
/// Line numbers are the wrong anchor: when a document is regenerated (the common
/// case — an AI tool rewriting a file being watched), line 200 is a different topic
/// than it was. Headings survive regeneration far better than prose, so the topic
/// is the anchor and the exact line is only a refinement within it.
/// </summary>
internal static class ScrollAnchor
{
    // ATX headings only ("## Topic"). Setext underlines are vanishingly rare in the
    // generated markdown this feature exists for.
    private static readonly Regex HeadingRx = new(@"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);

    private const int FingerprintMax = 80;

    public static DocAnchor Capture(string markdown, int firstVisibleLine, double ratio)
    {
        var lines = SplitLines(markdown);
        if (lines.Length == 0) return new DocAnchor(null, 0, null, 0);
        var start = Math.Clamp(firstVisibleLine, 0, lines.Length - 1);

        // Fingerprint: the first line with actual content at/below the viewport top.
        string? fingerprint = null;
        for (var i = start; i < lines.Length && i < start + 6; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            fingerprint = Normalize(lines[i]);
            break;
        }

        // Topic: nearest heading at or above the viewport top.
        string? heading = null;
        var ordinal = 0;
        for (var i = start; i >= 0; i--)
        {
            if (!TryHeading(lines[i], out var text)) continue;
            heading = text;
            for (var j = 0; j < i; j++)
                if (TryHeading(lines[j], out var earlier) && earlier == heading) ordinal++;
            break;
        }

        return new DocAnchor(heading, ordinal, fingerprint, Math.Clamp(ratio, 0, 1));
    }

    /// <summary>
    /// Best line to scroll to in the rewritten document, or -1 to leave the view alone.
    /// Topic first, exact line second, proportion last.
    /// </summary>
    public static int ResolveLine(string markdown, DocAnchor anchor)
    {
        if (anchor is null) return -1;
        var lines = SplitLines(markdown);
        if (lines.Length == 0) return -1;

        // 1) Find the topic. Within it, prefer the exact line if it survived.
        var headingLine = FindHeading(lines, anchor.Heading, anchor.HeadingOrdinal);
        if (headingLine >= 0)
        {
            if (anchor.Fingerprint is not null)
            {
                var end = NextHeadingAfter(lines, headingLine);
                var within = FindFingerprint(lines, anchor.Fingerprint, headingLine, end);
                if (within >= 0) return within;
            }
            return headingLine;
        }

        // 2) Topic is gone — fall back to the exact line anywhere in the document.
        if (anchor.Fingerprint is not null)
        {
            var anywhere = FindFingerprint(lines, anchor.Fingerprint, 0, lines.Length);
            if (anywhere >= 0) return anywhere;
        }

        // 3) Nothing recognizable survived; approximate by position.
        if (anchor.Ratio > 0)
            return Math.Clamp((int)Math.Round(anchor.Ratio * (lines.Length - 1)), 0, lines.Length - 1);

        return -1;
    }

    private static int FindHeading(string[] lines, string? heading, int ordinal)
    {
        if (string.IsNullOrEmpty(heading)) return -1;
        var seen = 0;
        var first = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!TryHeading(lines[i], out var text) || text != heading) continue;
            if (first < 0) first = i;
            if (seen == ordinal) return i;
            seen++;
        }
        return first; // occurrence count changed — settle for the first match
    }

    private static int NextHeadingAfter(string[] lines, int from)
    {
        for (var i = from + 1; i < lines.Length; i++)
            if (TryHeading(lines[i], out _)) return i;
        return lines.Length;
    }

    private static int FindFingerprint(string[] lines, string fingerprint, int from, int to)
    {
        for (var i = Math.Max(0, from); i < Math.Min(to, lines.Length); i++)
            if (!string.IsNullOrWhiteSpace(lines[i]) && Normalize(lines[i]) == fingerprint) return i;
        return -1;
    }

    private static bool TryHeading(string line, out string text)
    {
        var m = HeadingRx.Match(line);
        text = m.Success ? Normalize(m.Groups[2].Value) : string.Empty;
        return m.Success;
    }

    /// <summary>Collapse whitespace + case so cosmetic reflow doesn't break a match.</summary>
    private static string Normalize(string s)
    {
        var collapsed = Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
        return collapsed.Length > FingerprintMax ? collapsed[..FingerprintMax] : collapsed;
    }

    private static string[] SplitLines(string s) =>
        string.IsNullOrEmpty(s) ? Array.Empty<string>() : s.Replace("\r\n", "\n").Split('\n');
}
