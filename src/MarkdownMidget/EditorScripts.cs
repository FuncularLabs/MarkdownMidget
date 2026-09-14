using System.Text.Json;

namespace MarkdownMidget;

/// <summary>
/// The scripts the window runs in the editor, built as text away from the window so
/// that what they SAY can be tested: OnWebMessage needs a WebView2 and a page, and
/// a script built inline there is only ever covered by a scan for the names in it.
///
/// That is not idle: a scan is satisfied by
/// <c>window.MDM.create("", "{\"maxPictureBytes\":67108864}")</c>, which runs
/// without error, hands the editor a STRING where it reads an object, and turns the
/// paste guard off — every test green and the feature gone (review finding F-1).
/// </summary>
internal static class EditorScripts
{
    /// <summary>A value as a JavaScript literal, safe to put in a script: JSON is a
    /// subset of JavaScript expression syntax, and its escaping is exactly the
    /// escaping a string literal needs.</summary>
    public static string JsLiteral(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// <c>MDM.create</c>: the document the editor opens with, and the options it is
    /// configured from — today the picture ceiling its paste guard applies
    /// (<see cref="PictureLimit.EditorOptionsJson"/>).
    ///
    /// The options go in as an OBJECT. The document beside them is data, so it goes
    /// in as a string literal; the options are not, and quoting them would leave
    /// <c>ceilingFrom</c> with a string, no ceiling, and every pasted picture
    /// accepted whatever its size.
    /// </summary>
    public static string Create(string markdown) =>
        $"window.MDM.create({JsLiteral(markdown)}, {PictureLimit.EditorOptionsJson()})";

    /// <summary><c>MDM.setMarkdown</c> for install <paramref name="load"/>, which the editor echoes in the <c>painted</c> it posts after it.</summary>
    public static string SetMarkdown(string markdown, int load) => $"window.MDM.setMarkdown({JsLiteral(markdown)}, true, {load})";

    /// <summary>Whether a <c>painted</c> message is install <paramref name="load"/>'s: an earlier install's, arriving after its wait ran out, is not.</summary>
    public static bool IsPaintedFor(string messageJson, int load) { using var m = JsonDocument.Parse(messageJson); return m.RootElement.TryGetProperty("load", out var l) && l.TryGetInt32(out var n) && n == load; }
}
