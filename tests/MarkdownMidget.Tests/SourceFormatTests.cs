using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MarkdownMidget;
using MarkdownMidget.Source;
using Xunit;

namespace MarkdownMidget.Tests;

/// <summary>
/// The formatting commands the toolbar and menus apply to the raw-markdown view:
/// inline wraps, block-line prefixes, code blocks. What matters is parity with the
/// TextBox the source view used to be — the caret lands where it did, a block marker
/// is replaced rather than stacked, and each command is a single undo unit so one
/// Ctrl+Z takes back one action.
/// </summary>
public class SourceFormatTests
{
    private static T On<T>(Func<SourceEditor, T> body)
    {
        var result = default(T)!;
        Exception? error = null;
        var done = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            try
            {
                var ed = new SourceEditor();
                result = body(ed);
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "SourceFormat harness timed out");
        if (error is not null) throw error;
        return result;
    }

    [Fact]
    public void BoldWithASelectionWrapsItAndKeepsItSelected()
    {
        var (text, selStart, selLen) = On(ed =>
        {
            ed.Text = "make me bold";
            ed.Select(8, 4);                 // "bold"
            SourceFormat.Apply(ed, "bold");
            return (ed.Text, ed.SelectionStart, ed.SelectionLength);
        });
        Assert.Equal("make me **bold**", text);
        Assert.Equal(8, selStart);
        Assert.Equal(8, selLen);             // "**bold**"
    }

    [Fact]
    public void BoldAtACaretLandsTheCaretBetweenTheMarkers()
    {
        var (text, caret) = On(ed =>
        {
            ed.Text = "x";
            ed.CaretIndex = 1;
            ed.Select(1, 0);
            SourceFormat.Apply(ed, "bold");
            return (ed.Text, ed.CaretIndex);
        });
        Assert.Equal("x****", text);
        Assert.Equal(3, caret);              // between the two ** pairs
    }

    [Fact]
    public void HeadingPrefixesTheCaretLine()
    {
        var text = On(ed =>
        {
            ed.Text = "title\nbody";
            ed.CaretIndex = 2;               // on "title"
            SourceFormat.Apply(ed, "h2");
            return ed.Text;
        });
        Assert.Equal("## title\nbody", text);
    }

    [Fact]
    public void ChangingAHeadingReplacesTheMarkerRatherThanStackingIt()
    {
        var text = On(ed =>
        {
            ed.Text = "# already";
            ed.CaretIndex = 3;
            SourceFormat.Apply(ed, "h3");
            return ed.Text;
        });
        Assert.Equal("### already", text);   // not "### # already"
    }

    [Fact]
    public void ParagraphStripsAnExistingBlockMarker()
    {
        var text = On(ed =>
        {
            ed.Text = "> quoted";
            ed.CaretIndex = 2;
            SourceFormat.Apply(ed, "paragraph");
            return ed.Text;
        });
        Assert.Equal("quoted", text);
    }

    [Fact]
    public void CodeBlockAtACaretLandsInsideTheFence()
    {
        var (text, caret) = On(ed =>
        {
            ed.Text = "";
            ed.CaretIndex = 0;
            SourceFormat.InsertCodeBlock(ed, "csharp");
            return (ed.Text, ed.CaretIndex);
        });
        Assert.Equal("```csharp\n\n```\n", text);
        Assert.Equal("```csharp\n".Length, caret);   // on the empty body line
    }

    [Fact]
    public void EachCommandIsASingleUndoUnit()
    {
        // One Ctrl+Z must take back the whole command, not one character of it.
        foreach (var (setup, cmd) in new (Action<SourceEditor>, string)[]
                 {
                     (ed => { ed.Text = "word"; ed.Select(0, 4); }, "bold"),
                     (ed => { ed.Text = "word"; ed.Select(0, 4); }, "italic"),
                     (ed => { ed.Text = "line"; ed.CaretIndex = 2; }, "h1"),
                     (ed => { ed.Text = "line"; ed.CaretIndex = 2; }, "quote"),
                     (ed => { ed.Text = "line"; ed.CaretIndex = 2; }, "bullet"),
                 })
        {
            var (before, after, restored) = On(ed =>
            {
                setup(ed);
                var b = ed.Text;
                SourceFormat.Apply(ed, cmd);
                var a = ed.Text;
                ed.Undo();
                return (b, a, ed.Text);
            });
            Assert.NotEqual(before, after);            // the command did something
            Assert.Equal(before, restored);            // one undo reverted all of it
        }
    }

    [Fact]
    public void CodeBlockIsASingleUndoUnit()
    {
        var (after, restored) = On(ed =>
        {
            ed.Text = "keep";
            ed.CaretIndex = 4;
            SourceFormat.InsertCodeBlock(ed, "");
            var a = ed.Text;
            ed.Undo();
            return (a, ed.Text);
        });
        Assert.NotEqual("keep", after);
        Assert.Equal("keep", restored);
    }
}
