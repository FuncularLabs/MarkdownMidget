using System;
using System.IO;
using System.Reflection;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace MarkdownMidget.Source;

/// <summary>
/// Loads the markdown highlighting definition and recolours it from the document
/// theme.
///
/// The definition (Markdown.xshd) names its colours but sets no brushes; this maps
/// each named colour to a page-level theme colour and applies the legibility floor
/// (<see cref="SourcePalette.Legible"/>). Font weight and style live in the .xshd and
/// are never touched here.
///
/// One process is one window (File ▸ New spawns a new process), so mutating the
/// shared definition's named colours cannot bleed into a window that chose a
/// different theme.
/// </summary>
public sealed class SourceHighlighting
{
    private readonly IHighlightingDefinition _definition;

    public SourceHighlighting() : this(Assembly.GetExecutingAssembly()) { }

    public SourceHighlighting(Assembly resources)
    {
        _definition = Load(resources);
    }

    /// <summary>The loaded definition, so a test can inspect its named colours.</summary>
    public IHighlightingDefinition Definition => _definition;

    private static IHighlightingDefinition Load(Assembly resources)
    {
        using var stream = resources.GetManifestResourceStream("Markdown.xshd")
            ?? throw new InvalidOperationException("Markdown.xshd resource is missing.");
        using var reader = XmlReader.Create(stream);
        // A private HighlightingManager, not the shared Instance: the definition is
        // ours to mutate per theme, and it references nothing else, so it does not
        // need to be registered process-wide.
        return HighlightingLoader.Load(reader, new HighlightingManager());
    }

    /// <summary>Attach the definition to the editor so it colours markdown.</summary>
    public void Attach(SourceEditor editor) => editor.SyntaxHighlighting = _definition;

    /// <summary>
    /// Recolour every named colour from the theme, then re-highlight the editor.
    ///
    /// The role→colour mapping is where "capture the vibe of the WYSIWYG theme"
    /// lives: headings and structural markers take the heading colour, links the link
    /// colour, quotes the quote colour, code the accent, and emphasis/strong stay the
    /// body text colour (their weight and slant carry them). Each accent passes
    /// through the legibility floor so nothing lands under 4.5:1 on the page.
    /// </summary>
    public void SetPalette(SourceEditor editor, SourcePalette palette)
    {
        var heading = palette.Legible(palette.Heading);
        var link = palette.Legible(palette.Link);
        var accent = palette.Legible(palette.Accent);
        var quote = palette.Legible(palette.Quote);

        Set("Heading", heading);
        Set("ListMarker", heading);
        Set("Rule", heading);
        Set("Link", link);
        Set("BlockQuote", quote);
        Set("InlineCode", accent);   // inline code and fenced-code lines
        Set("Strong", palette.Text);
        Set("Emphasis", palette.Text);

        // Force a re-highlight: the DocumentHighlighter caches brushes per line, so a
        // colour change is not seen until the visible lines are redrawn.
        editor.TextArea.TextView.Redraw();
        return;

        void Set(string name, System.Windows.Media.Color color)
        {
            var c = _definition.GetNamedColor(name);
            if (c is not null) c.Foreground = new SimpleHighlightingBrush(color);
        }
    }
}
