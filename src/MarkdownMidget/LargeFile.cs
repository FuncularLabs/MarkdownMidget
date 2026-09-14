namespace MarkdownMidget;

/// <summary>When opening a file offers the Markdown source view: the formatted view takes seconds to build a large
/// document (about 1 s at 144 KB, 3–5 s at 512 KB). The offer is a line in the opening overlay; nothing switches.</summary>
internal static class LargeFile
{
    public const long HintBytes = 250 * 1024;
    public static bool ShouldHint(long bytes, bool openingInSource) => !openingInSource && bytes >= HintBytes;
}
