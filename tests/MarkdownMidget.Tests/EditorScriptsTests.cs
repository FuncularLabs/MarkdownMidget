using System;
using System.Text.Json;
using MarkdownMidget;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The scripts the window runs in the editor (<see cref="EditorScripts"/>), and the
/// two arms of OnWebMessage that use them.
///
/// F-1: the create call was built inline in the window and covered only by a scan
/// for the names in it — which spelling the options as a JS STRING satisfies.
/// <c>window.MDM.create("", "{\"maxPictureBytes\":67108864}")</c> kept every C# and
/// JS test green while the editor read a string, took no ceiling from it and
/// refused nothing: the guard silently off. What the script SAYS is pinned here,
/// character for character, and the window is pinned to call it.
///
/// F-2: deleting the window's answer to a refused paste (the status-bar notice) was
/// noticed by nothing — the paste was cancelled and the user told nothing. The arm
/// is scanned between its label and its break, so the call has to be in THAT arm.
/// </summary>
public class EditorScriptsTests
{
    [Fact]
    public void TheCreateScriptPassesTheCeilingAsAnObjectNotAString()
    {
        // The call the window actually makes, in full.
        Assert.Equal("window.MDM.create(\"\", {\"maxPictureBytes\":67108864})", EditorScripts.Create(string.Empty));
        // And the same call said the way the code has to build it, so moving the
        // ceiling moves the script rather than leaving a stale number behind.
        Assert.Equal($"window.MDM.create(\"\", {PictureLimit.EditorOptionsJson()})", EditorScripts.Create(string.Empty));

        // What the options must not be: quoted. MDM.create reads
        // options.maxPictureBytes; a string has no such property, so ceilingFrom
        // answers null and the paste guard applies no ceiling at all.
        const string prefix = "window.MDM.create(\"\", ";
        var options = EditorScripts.Create(string.Empty)[prefix.Length..^1];
        Assert.StartsWith("{", options, StringComparison.Ordinal);
        using var parsed = JsonDocument.Parse(options);
        Assert.Equal(PictureLimit.MaxBytes, parsed.RootElement.GetProperty("maxPictureBytes").GetInt64());
    }

    [Fact]
    public void TheDocumentGoesInAsAJavascriptStringLiteral()
    {
        // The other half of the call is the document, which is data: a quote, a
        // backslash or a newline in it must not end the literal or change the script.
        // Proved by reading the literal back rather than by writing the escaping out
        // a second time — the escaping is the serializer's business, the round trip
        // is the promise.
        const string markdown = "a \"quoted\" \\ line\nand another";
        var script = EditorScripts.Create(markdown);
        var literal = script["window.MDM.create(".Length..script.LastIndexOf(", {", StringComparison.Ordinal)];

        Assert.Equal(markdown, JsonSerializer.Deserialize<string>(literal));
        Assert.EndsWith($", {PictureLimit.EditorOptionsJson()})", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWindowHandsTheEditorThatScriptAndSaysSoWhenAPasteIsRefused()
    {
        // Neither arm can be run by a test: OnWebMessage needs a WebView2 and a page.
        // Each is scanned inside its own arm — between the label and the break — so a
        // call that wandered elsewhere in five thousand lines would not satisfy it.
        var window = RepoSources.Read("src", "MarkdownMidget", "MainWindow.xaml.cs");

        var loaded = RepoSources.CaseBody(window, "case \"loaded\":");
        Assert.Contains("EditorScripts.Create(string.Empty)", loaded, StringComparison.Ordinal);
        // Built in one place, not spelled out here as well.
        Assert.DoesNotContain("window.MDM.create(", loaded, StringComparison.Ordinal);

        var refused = RepoSources.CaseBody(window, "case PictureLimit.RefusedMessageType:");
        Assert.Contains("FlashStatus(PictureLimit.Notice(null))", refused, StringComparison.Ordinal);
    }
}
