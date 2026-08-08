using System;

namespace MarkdownMidget.Themes;

/// <summary>Where a theme file is wrong, and what to say about it.</summary>
internal readonly record struct CssProblem(int Line, int Column, string Message)
{
    /// <summary>What the menu shows in the tooltip of a disabled theme.</summary>
    public override string ToString() => $"line {Line}, column {Column}: {Message}";
}

/// <summary>
/// Decides whether a theme file is fit to inject.
///
/// Two jobs, and only one of them is about syntax.
///
/// **Is it well-formed enough to be worth applying?** A theme is dropped into a
/// folder by hand, so it goes wrong in the ways hand-written CSS goes wrong: a
/// brace that never closes, a comment that never ends, a declaration missing its
/// colon. The menu lists the file either way and disables the broken one with the
/// first problem in its tooltip, because "my theme isn't in the list" is a worse
/// experience than "my theme is greyed out and says line 12 is missing a colon".
///
/// **Does it reach the network?** This used to be the control, and it was the wrong
/// place for one. Four review rounds each found another spelling of a URL that this
/// could not see — the function name written with escapes, a bare string in
/// `image-set()`, a leading control character, a tab in the middle, a scheme with no
/// slashes — because it matches text that Chromium normalises before it parses. Every
/// round fixed the spellings that round had found.
///
/// The control now sits where the request is: `MainWindow.OnResourceRequested`
/// refuses every off-origin request, and the editor page carries a CSP. Neither needs
/// to know how a URL was written.
///
/// So what remains here is an explanation, and it is worth keeping as one. A theme
/// that references a remote address will not work, and being told which line — while
/// the menu entry is greyed out — is better than a silently broken image. A spelling
/// missed here now costs a rendering glitch rather than a beacon, which is the
/// property this check could never actually deliver on its own.
///
/// What this deliberately does NOT do is judge whether a property or value means
/// anything. An unknown property is the user being clever, and the browser already
/// ignores what it cannot use.
///
/// It is also not a sandbox. The layer order in bundle.css stops a theme removing
/// what chrome declares; nothing here stops a determined author making the editor
/// unusable through `display`, `visibility` or absolute positioning. The goal is to
/// catch accidents and drive-by hostility in a file someone was handed.
/// </summary>
internal static class CssValidator
{
    /// <summary>The first problem in <paramref name="css"/>, or null if it is fit to use.</summary>
    public static CssProblem? Validate(string? css)
    {
        if (string.IsNullOrEmpty(css)) return null;   // an empty theme is a no-op, not an error

        var line = 1;
        var column = 1;
        var depth = 0;

        // Parenthesis depth, which is what stops `url(data:image/gif;base64,…)` being
        // read as a statement. Inside a function, `;` `{` and `}` are ordinary
        // characters — and a semicolon appears in essentially every data URI, so
        // without this the validator refuses the exact thing its own error message
        // tells the author to write instead.
        var paren = 0;
        var parenLine = 1;
        var parenColumn = 1;

        // Attribute-selector depth, and what is inside one. A theme may test that an
        // attribute EXISTS — `[disabled]` — but not what it contains, so this tracks
        // whether the current bracket has met a value operator.
        var bracket = 0;
        var bracketLine = 1;
        var bracketColumn = 1;

        // Start of the run of text since the last ; { or } — where a declaration
        // missing its colon has to be reported, rather than at the punctuation that
        // revealed it.
        var runLine = 1;
        var runColumn = 1;
        var runHasColon = false;
        var runHasContent = false;
        var runIsAtRule = false;

        for (var i = 0; i < css.Length; i++)
        {
            var c = css[i];

            if (c == '/' && i + 1 < css.Length && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return new CssProblem(line, column, "this comment is never closed (no `*/`)");
                Advance(css, i, end + 2, ref line, ref column);
                i = end + 1;
                continue;
            }

            if (c is '"' or '\'')
            {
                var closed = false;
                var startLine = line;
                var startColumn = column;
                var contentStart = i + 1;
                var j = i + 1;
                Step(c, ref line, ref column);
                for (; j < css.Length; j++)
                {
                    // A backslash escapes the next character, including the quote, and
                    // including a newline — which is how a string legally spans lines.
                    // CRLF counts as ONE newline there: CSS folds CR, FF and CRLF into
                    // a single LF before tokenizing. Miss that and a theme saved on
                    // Windows, which is every hand-edited theme, gets told its string
                    // is unterminated.
                    if (css[j] == '\\' && j + 1 < css.Length)
                    {
                        Step(css[j], ref line, ref column);
                        Step(css[j + 1], ref line, ref column);
                        if (css[j + 1] is '\r' && j + 2 < css.Length && css[j + 2] is '\n')
                        {
                            Step(css[j + 2], ref line, ref column);
                            j++;
                        }
                        j++;
                        continue;
                    }
                    if (css[j] == c) { closed = true; Step(css[j], ref line, ref column); break; }
                    // A newline inside a quoted string is an error in CSS, and in
                    // practice it means a missing closing quote several lines up —
                    // so report it there rather than at the end of the file.
                    //
                    // All three spellings, for the same reason the escape branch above
                    // takes all three: CR and FF ARE newlines to CSS, folded to LF
                    // before tokenizing. Stepping over a lone CR made this scanner
                    // pair quotes differently from the browser — three quotes consumed
                    // where Chromium consumes one — which shifted every later string
                    // and hid a payload's URL inside a span this never inspected.
                    if (css[j] is '\n' or '\r' or '\f') break;
                    Step(css[j], ref line, ref column);
                }
                if (!closed)
                    return new CssProblem(startLine, startColumn,
                        $"this {c} is never closed — the text after it is being read as a string");

                // A string can BE a URL with no `url(` in sight. `image-set("https://…")`
                // takes one directly and loads it, and the branch above skips
                // everything inside quotes — so a check that only knows about `url(`
                // never sees it. Verified: it fetched.
                //
                // Decoded first, because escapes are legal INSIDE a string even though
                // they are refused outside one, and `image-set("\68ttp://…")` fetched
                // straight through the undecoded version of this check.
                //
                // And matched as a URL rather than as "has a scheme": a bare
                // `scheme:` prefix is far too common in ordinary text. `content:
                // "Note: "` was being refused, with a message accusing the theme of
                // making a network request.
                //
                // Not inside an attribute selector, though. `a[href^="https://"]` is
                // the commonest string containing a URL in a documentation theme, and
                // it is matched against the page, never fetched. Nothing about the
                // string itself distinguishes it — only where it sits.
                if (LooksLikeUrl(Decode(css.AsSpan(contentStart, j - contentStart))))
                    return new CssProblem(startLine, startColumn, RemoteMessage);

                if (!runHasContent) { runLine = startLine; runColumn = startColumn; }
                runHasContent = true;
                i = j;
                continue;
            }

            // Escapes are how `url` stops looking like `url`: `\75 rl(…)` IS `url(…)`
            // once idents are decoded, and it fetched a real image while an earlier
            // version of this file waved it through. Outside a string the whole class
            // goes rather than being decoded — nothing in a palette needs to spell an
            // identifier sideways, and refusing is the answer that cannot be
            // out-thought. Inside a string escapes are ordinary and are decoded above.
            if (c == '\\')
                return new CssProblem(line, column,
                    "backslash escapes are not allowed in a theme — they can spell `url(` in " +
                    "a way that hides a network request. Write the character directly");

            switch (c)
            {
                case '(':
                    paren++;
                    if (paren == 1) { parenLine = line; parenColumn = column; }
                    break;

                case ')':
                    if (paren > 0) paren--;
                    break;

                case '[':
                    bracket++;
                    bracketLine = line;
                    bracketColumn = column;
                    break;

                case '=' when bracket > 0:
                    // The oracle, refused at its selector rather than at its payload.
                    //
                    // `span[data-value^="a"] { background-image: url(…) }` fires only
                    // when it matches, so a few hundred of those read a value out of
                    // the document one character at a time — and `data-value` carries
                    // the original unsanitized HTML. Each match is an image request,
                    // which is the one thing that has to stay allowed off-origin,
                    // because documents legitimately reference pictures.
                    //
                    // Nothing downstream can tell that request from a document's. So
                    // it goes here: with no way to select on what an attribute
                    // CONTAINS, a beacon that slips through the url() rules fires once
                    // and says nothing. `[disabled]` and `[open]` still work — this
                    // refuses the comparison, not the attribute.
                    return new CssProblem(bracketLine, bracketColumn,
                        "an attribute selector that tests a VALUE is not allowed in a theme — " +
                        "matching on document content is how a stylesheet reads it back out. " +
                        "Testing that an attribute is present, like `[disabled]`, is fine");

                case ']':
                    if (bracket > 0) bracket--;
                    break;

                case '{' when paren == 0:
                    depth++;
                    ResetRun(ref runHasColon, ref runHasContent, ref runIsAtRule);
                    Step(c, ref line, ref column);
                    continue;

                case '}' when paren == 0:
                    if (depth == 0)
                        return new CssProblem(line, column, "this `}` closes a block that was never opened");
                    // A run ending at `}` is the last declaration in the block, which
                    // is allowed to omit its semicolon.
                    if (MissingColon(runHasContent, runHasColon, runIsAtRule))
                        return new CssProblem(runLine, runColumn, MissingColonMessage);
                    depth--;
                    ResetRun(ref runHasColon, ref runHasContent, ref runIsAtRule);
                    Step(c, ref line, ref column);
                    continue;

                case ';' when paren == 0:
                    // No `depth > 0` here. At the top level the only valid statement
                    // with no colon is an at-rule, which `runIsAtRule` already spares,
                    // so the extra guard could only ever excuse malformed CSS — and an
                    // untested branch that excuses malformed input is worse than a
                    // message.
                    if (MissingColon(runHasContent, runHasColon, runIsAtRule))
                        return new CssProblem(runLine, runColumn, MissingColonMessage);
                    ResetRun(ref runHasColon, ref runHasContent, ref runIsAtRule);
                    Step(c, ref line, ref column);
                    continue;

                case ':':
                    runHasColon = true;
                    break;

                case '@' when !runHasContent:
                    runIsAtRule = true;
                    break;
            }

            if (!char.IsWhiteSpace(c))
            {
                if (!runHasContent) { runLine = line; runColumn = column; }
                runHasContent = true;
            }

            // The network rules. Best-effort by nature — see the note at the top;
            // what stops a request is the host filter, not this.
            if (Matches(css, i, "url("))
            {
                var problem = CheckUrl(css, i, line, column);
                if (problem is not null) return problem;

                // An UNQUOTED url() is one token to CSS — a `url-token` — and it ends
                // at the first `)`. Nothing inside it is punctuation, so the whole
                // thing is stepped over rather than scanned.
                //
                // This is what closes the hole the attribute-selector skip opened.
                // `url(data:text/plain,[)` is a legal url-token that Chromium applies
                // without complaint, but a `[` counted here left the bracket depth
                // stuck above zero for the rest of the file — and every later string
                // then skipped the URL check. A false-positive fix that became the
                // bypass. It also retires the special-casing of `;` inside a data URI,
                // which is the same problem seen from the other side.
                //
                // A quoted url("…") is left alone: the string scanner handles it, and
                // a string is where the escapes and the quote pairing live.
                var after = i + "url(".Length;
                while (after < css.Length && char.IsWhiteSpace(css[after])) after++;
                if (after < css.Length && css[after] is not ('"' or '\''))
                {
                    var end = css.IndexOf(')', after);
                    if (end < 0)
                        return new CssProblem(line, column,
                            "this `url(` is never closed — everything after it is being read as part of it");
                    Advance(css, i, end + 1, ref line, ref column);
                    i = end;
                    continue;
                }
            }
            else if (Refused(css, i) is { } refusal)
            {
                return new CssProblem(line, column, refusal);
            }
            else if (runIsAtRule && Matches(css, i, "@import"))
            {
                return new CssProblem(line, column,
                    "`@import` is not allowed in a theme — it fetches another file over the " +
                    "network, and a theme is injected into a layer where it would be ignored anyway");
            }

            Step(c, ref line, ref column);
        }

        // Reported before the brace check: an unclosed `(` is why the braces after it
        // stopped counting, so blaming a brace would be blaming the symptom.
        if (paren > 0)
            return new CssProblem(parenLine, parenColumn,
                "this `(` is never closed — everything after it is being read as part of it");

        if (depth > 0)
            return new CssProblem(line, column,
                depth == 1 ? "a `{` is never closed — the file ends inside a block"
                           : $"{depth} `{{` are never closed — the file ends inside nested blocks");

        return null;
    }

    /// <summary>
    /// The safety scan: features a theme has no business using, refused by name.
    ///
    /// This is deliberately over-broad, and that is the policy rather than an
    /// accident of implementation. A theme is a palette someone was handed; the cost
    /// of refusing a construct it does not need is a message, and the cost of
    /// allowing one it can misuse is an attack surface that four review rounds could
    /// not close by cleverness. So anything on this list goes, whether or not the
    /// particular spelling in front of us is exploitable.
    ///
    /// - `:has()` selects a parent by what is inside it, which is the same conditional
    ///   match the attribute oracle uses and the same lever on document content.
    /// - `@font-face` fetches, and a font load is its own exfiltration and
    ///   fingerprinting channel. A theme picks from installed families instead.
    /// - `expression()`, `behavior:` and `-moz-binding:` are how stylesheets used to
    ///   run script. Chromium ignores all three, so this changes nothing today — it
    ///   is here because "no script execution of any kind" should be enforced by
    ///   something more durable than a browser's disinterest.
    /// </summary>
    private static string? Refused(string css, int at) => true switch
    {
        _ when Matches(css, at, ":has(") =>
            "`:has()` is not allowed in a theme — selecting by what an element contains " +
            "is a way to read the document back out through which rules match",
        _ when Matches(css, at, "@font-face") =>
            "`@font-face` is not allowed in a theme — it fetches a font, and which fonts " +
            "load is itself a channel. Name an installed family in `font-family` instead",
        _ when Matches(css, at, "expression(") =>
            "`expression()` is not allowed in a theme — it is a way to run script from a " +
            "stylesheet",
        _ when Matches(css, at, "-moz-binding") || Matches(css, at, "behavior:") =>
            "this is not allowed in a theme — it is a way to attach script to an element " +
            "from a stylesheet",
        _ => null,
    };

    private const string MissingColonMessage =
        "this looks like a declaration with no `:` between the property and its value";

    private static bool MissingColon(bool hasContent, bool hasColon, bool isAtRule)
        => hasContent && !hasColon && !isAtRule;

    private static void ResetRun(ref bool hasColon, ref bool hasContent, ref bool isAtRule)
    {
        hasColon = false;
        hasContent = false;
        isAtRule = false;
    }

    /// <summary>
    /// A `url()` is allowed only when it embeds its own bytes.
    ///
    /// Off-origin is the beacon. Relative is rejected too, and that one is worth
    /// explaining because it looks harmless: a theme is injected into a `&lt;style&gt;`
    /// element, so relative URLs resolve against the DOCUMENT base — which the host
    /// sets to a virtual host for the open markdown file. `url(texture.png)` would
    /// point into whatever folder the user's document lives in, never the themes
    /// folder. It cannot do what its author expects, so it fails now with a reason
    /// rather than later as a missing image.
    ///
    /// `data:` is fine and is the way to ship a texture: it makes no request.
    /// </summary>
    private static CssProblem? CheckUrl(string css, int start, int line, int column)
    {
        var i = start + "url(".Length;
        while (i < css.Length && char.IsWhiteSpace(css[i])) i++;
        if (i < css.Length && css[i] is '"' or '\'') i++;
        // No second whitespace skip after the quote: SchemeOf trims, so one here was
        // dead code that read like a check.
        //
        // Decoded for the same reason as the string branch — quoted contents may
        // carry escapes, and without this `url("\68ttp://…")` was rejected, but as
        // "relative", which is a confident wrong answer about a remote URL.
        //
        // Bounded at the closing paren rather than decoding the rest of the file: a
        // span slice was free, and this is not, so doing it once per `url()` over the
        // whole remainder turns a linear pass quadratic.
        // Normalised as well as decoded, for the same reasons the string path is —
        // a tab after the colon, a backslash for a slash, a leading control
        // character. Bounded at the closing paren rather than run over the rest of
        // the file: a span slice was free and this is not, so doing it once per
        // `url()` across the remainder turns a linear pass quadratic.
        var close = css.IndexOf(')', i);
        var rest = Normalise(Decode(css.AsSpan(i, (close < 0 ? css.Length : close) - i))).AsSpan();

        // An empty url() reaches nothing; leave it to the browser to ignore.
        if (rest.IsEmpty || rest[0] is ')') return null;

        // `url(#id)` points inside the document it is already in — a filter or a
        // mask reference. It issues no request, so the rule being enforced here does
        // not reach it.
        if (rest[0] is '#') return null;

        var scheme = SchemeOf(rest);

        // No scheme means a path, and a path is the one case that is not a security
        // problem at all — it is a promise the editor cannot keep, which earns its
        // own sentence. Saying "relative" about `ftp://` or `file:///` (which the
        // first version of this did, because it only recognised http and https)
        // sends the author to fix something that is not wrong.
        if (scheme is null)
            return new CssProblem(line, column,
                "this `url()` is relative, which resolves against the open document's " +
                "location rather than the themes folder, so it cannot find the file. Embed " +
                "the image as a `data:` URL instead");

        return scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            ? null
            : new CssProblem(line, column, RemoteMessage);
    }

    private const string RemoteMessage =
        "this points at an address outside the app — a theme is not allowed to make " +
        "network requests. Embed the image as a `data:` URL instead";

    /// <summary>
    /// The scheme of an absolute reference — `https` in `https://x` — or null when
    /// there is none and the value is a path.
    ///
    /// `//host/x` counts as one: it inherits the page's scheme and is a request like
    /// any other, while looking more like a path than a URL. Every scheme except
    /// `data:` is refused, because `ftp:`, `blob:` and `file:` are absolute
    /// references too and a list that names only http and https ages badly.
    /// </summary>
    private static string? SchemeOf(ReadOnlySpan<char> value)
    {
        value = value.TrimStart();
        if (value.StartsWith("//")) return "https";   // protocol-relative

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':') return i == 0 ? null : new string(value[..i]);
            // RFC 3986: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ). A digit
            // first is not a scheme, and reading one as a scheme is what made
            // `content: "12:30"` look like a URL.
            var ok = i == 0 ? char.IsAsciiLetter(c)
                            : char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.';
            if (!ok) return null;
        }
        return null;
    }

    /// <summary>
    /// Whether a bare string is a URL, as opposed to text that merely contains a
    /// colon. Requires the `//` — `scheme://host` or the protocol-relative `//host` —
    /// because that is what every fetchable reference in a stylesheet looks like,
    /// while `Note: `, `12:30`, `Foo:Bar` and `TODO:` are all things a real theme
    /// puts in `content` and none of them can load anything.
    ///
    /// `url()` keeps the stricter rule (any scheme but `data:`), because there is no
    /// false-positive surface inside one.
    /// </summary>
    private static bool LooksLikeUrl(string value)
    {
        // Normalised the way the URL parser does before this can ask anything about
        // it, because otherwise the question is about text the browser is going to
        // rewrite. Each of these was a confirmed fetch through a version of this
        // function that skipped it:
        //
        //   - leading C0 controls and spaces are stripped, so `"https://…"`
        //     needed no CSS escape at all to walk past a check anchored at index 0;
        //   - tab, LF and CR are deleted from ANYWHERE in a URL, so nothing can be
        //     relied on to sit immediately after the colon;
        //   - for a special scheme, `\` is `/`, so `https:\\host/x` is `https://host/x`.
        // Three of these overlap, and a mutation of any one alone is caught by
        // another. Worth writing down, because "no test can tell the difference" has
        // twice in this file meant dead code and once meant belt-and-braces:
        //   - the CRLF case inside Decode is covered by deleting CR and LF here;
        //   - the `//` early return below is covered by SchemeOf reporting "https"
        //     for a protocol-relative value, which is then special;
        //   - mapping `\` to `/` is covered, for every input I could construct, by
        //     the special-scheme list. A reviewer reported a schemeless counter-example
        //     (`"/\host/x"`, protocol-relative because the BASE is special) that this
        //     mapping alone catches; I could not reproduce it, because `\h` in a CSS
        //     string is consumed as an escape before any of this sees it. Unresolved,
        //     and kept for that reason rather than despite it.
        //
        // All three are kept because each fails closed on its own, and dropping any
        // one leaves a single mechanism holding a network rule.
        var span = Normalise(value).AsSpan();

        if (span.StartsWith("//")) return true;

        if (SchemeOf(span) is not { } scheme) return false;
        if (scheme.Equals("data", StringComparison.OrdinalIgnoreCase)) return false;

        // And the `//` is OPTIONAL for a special scheme — `https:host/x` is one
        // request — which is why this cannot simply require it. Requiring it for
        // everything else is what keeps `content: "Note: "` and `"12:30"` working.
        return IsSpecialScheme(scheme) || span[(scheme.Length + 1)..].StartsWith("//");
    }

    /// <summary>The schemes the URL standard calls "special"; they are the ones that
    /// accept a missing `//` and treat a backslash as a slash.</summary>
    private static bool IsSpecialScheme(string scheme) =>
        scheme.ToLowerInvariant() is "http" or "https" or "ws" or "wss" or "ftp" or "file";

    private static string Normalise(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\t' or '\n' or '\r') continue;   // removed from anywhere in a URL
            sb.Append(c is '\\' ? '/' : c);            // a backslash is a slash to a special scheme
        }

        var i = 0;
        while (i < sb.Length && (sb[i] <= ' ')) i++;    // leading C0 controls and space
        return sb.ToString(i, sb.Length - i);
    }

    /// <summary>
    /// CSS escapes, resolved far enough to see a scheme: `\` + up to six hex digits
    /// with an optional trailing space, `\` + newline (a line continuation, which
    /// disappears), and `\` + anything else (the character itself).
    ///
    /// This is not a general unescaper and does not need to be — it exists so that
    /// `\68ttp://…`, `http\3a //…` and `\2f /…` are recognised as what CSS will
    /// resolve them to. All three fetched before it existed.
    /// </summary>
    private static string Decode(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0) return new string(value);

        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { sb.Append(value[i]); continue; }
            if (++i >= value.Length) break;                    // a trailing backslash

            // A line continuation, which disappears. All four spellings: CSS folds
            // CR, FF and CRLF into a single LF before tokenizing, so `\` + any of
            // them is the same rule. Handling only LF left `htt\<CR>p://…` and
            // `htt\<FF>p://…` fetching.
            if (value[i] is '\n' or '\r' or '\f')
            {
                if (value[i] is '\r' && i + 1 < value.Length && value[i + 1] is '\n') i++;
                continue;
            }
            if (!char.IsAsciiHexDigit(value[i])) { sb.Append(value[i]); continue; }

            var code = 0;
            var digits = 0;
            while (i < value.Length && digits < 6 && char.IsAsciiHexDigit(value[i]))
            {
                code = code * 16 + Convert.ToInt32(value[i].ToString(), 16);
                i++; digits++;
            }
            // One whitespace character after the digits terminates the escape and is
            // consumed; anything else is the next character and is not, so the loop
            // has to step back or it eats it.
            if (i < value.Length && char.IsWhiteSpace(value[i])) { /* eaten */ }
            else i--;

            if (code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF))
                sb.Append(char.ConvertFromUtf32(code));
        }
        return sb.ToString();
    }

    private static bool Matches(string css, int at, string text)
        => at + text.Length <= css.Length
           && string.Compare(css, at, text, 0, text.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static void Step(char c, ref int line, ref int column)
    {
        if (c == '\n') { line++; column = 1; }
        else column++;
    }

    private static void Advance(string css, int from, int to, ref int line, ref int column)
    {
        for (var i = from; i < to && i < css.Length; i++) Step(css[i], ref line, ref column);
    }
}
