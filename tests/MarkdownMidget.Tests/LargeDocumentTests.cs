using Xunit;

namespace MarkdownMidget.Tests;

/// <summary><see cref="LargeDocument"/>. That the ticks, margins, squiggles and note follow it is a manual check.</summary>
public class LargeDocumentTests
{
    private static readonly string OverInUtf8 = new('é', LargeDocument.Bytes / 2 + 1);   // under the threshold in chars, over it in UTF-8 bytes
    private static LargeDocument Loaded(string text) { var large = new LargeDocument(); large.Loaded(text, sameDocument: false); return large; }

    [Fact]
    public void Over_512_KB_of_UTF8_both_start_off_in_both_views_and_the_note_naming_the_menus_shows_once_per_document()
    {
        var large = new LargeDocument();
        Assert.True(large.Loaded(OverInUtf8, sameDocument: false));
        Assert.False(large.SpellCheck(saved: true) || large.LineNumbers(saved: true, sourceView: false) || large.LineNumbers(saved: true, sourceView: true));
        Assert.False(large.Took(TimeSpan.FromSeconds(9)) || large.Loaded(OverInUtf8, sameDocument: true));   // a slow switch, then a reload
        Assert.True(large.Loaded(OverInUtf8, sameDocument: false));   // the next large document
        Assert.Equal(512 * 1024, LargeDocument.Bytes);
        Assert.Contains("View ▸ Line Numbers ▸ Show Line Numbers and View ▸ Spell Check", LargeDocument.Note);
    }

    [Fact]
    public void Exactly_512_KB_follows_the_saved_settings() =>
        Assert.True(Loaded(new string('a', LargeDocument.Bytes)) is var large && large.SpellCheck(saved: true) && large.LineNumbers(saved: true, sourceView: true));

    [Fact]
    public void Turning_one_on_is_the_documents_own_choice_and_sticks_and_a_small_document_follows_the_saved_settings_again()
    {
        var large = Loaded(OverInUtf8);
        Assert.True(large.ChooseSpell(true) && large.ChooseLines(true, document: false, source: true));   // true: the saved settings stay as they are
        large.Took(TimeSpan.FromSeconds(9));
        large.Loaded(OverInUtf8, sameDocument: true);
        Assert.True(large.SpellCheck(saved: false) && large.LineNumbers(saved: false, sourceView: true));
        Assert.False(large.LineNumbers(saved: true, sourceView: false));
        large.Loaded("small", sameDocument: false);
        Assert.False(large.ChooseSpell(true) || large.SpellCheck(saved: false));   // not large: the click is the saved setting's
        Assert.True(large.LineNumbers(saved: true, sourceView: false));
    }

    [Fact]
    public void Closing_forgets_the_document_so_the_no_document_screen_follows_the_saved_settings()
    {
        var large = Loaded(OverInUtf8);
        large.ChooseSpell(true); large.Closed();
        Assert.True(large.SpellCheck(saved: true) && large.LineNumbers(saved: true, sourceView: true));
        Assert.False(large.ChooseSpell(false) || large.SpellCheck(saved: false));   // a click there is the saved setting's
        Assert.True(large.Loaded(OverInUtf8, sameDocument: true));   // the next large one gets its note
    }

    [Fact]
    public void Keeping_the_document_under_a_new_name_keeps_its_choices()   // Save As over a file changed on disk: sameDocument
    {
        var large = Loaded(OverInUtf8); large.ChooseLines(true, document: true, source: true);
        Assert.False(large.Loaded(OverInUtf8, sameDocument: true));
        Assert.True(large.LineNumbers(saved: false, sourceView: false) && large.LineNumbers(saved: false, sourceView: true));
    }

    [Theory, InlineData(5000, false), InlineData(5001, true)]
    public void A_load_or_switch_over_5_seconds_turns_both_off_with_the_note(int ms, bool off)
    {
        var large = Loaded("small");
        Assert.Equal(off, large.Took(TimeSpan.FromMilliseconds(ms)));
        Assert.Equal(!off, large.SpellCheck(saved: true) && large.LineNumbers(saved: true, sourceView: false));
    }
}
