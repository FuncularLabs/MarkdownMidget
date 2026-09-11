using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using WpfDataObject = System.Windows.IDataObject;

namespace MarkdownMidget.Tests;

/// <summary>
/// Clipboard data in the shapes other programs really leave it (#7), built in
/// memory: nothing here reads or writes the clipboard itself.
/// </summary>
internal static class ClipboardFixtures
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    private const int BiRgb = 0;
    private const uint DibRgbColors = 0;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>
    /// A 32-bit BI_RGB bitmap (the kind a Windows screenshot tool puts on the
    /// clipboard) holding <paramref name="bgra"/>: top-down rows, four bytes a
    /// pixel, the fourth being the DIB's padding byte. It is read back through
    /// <see cref="Imaging.CreateBitmapSourceFromHBitmap"/>, and what comes back has
    /// the type and format a read-only probe of a real Snipping Tool screenshot
    /// found WPF handing over: an InteropBitmap in Bgra32, the padding byte taken as
    /// alpha. PastedScreenshotKeepsItsColoursAndIsOpaque asserts both.
    /// </summary>
    public static BitmapSource Dib(int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4)
            throw new ArgumentException("four bytes a pixel", nameof(bgra));
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,          // negative: rows run top-down, as the bytes do
            Planes = 1,
            BitCount = 32,
            Compression = BiRgb,
        };
        var hbitmap = CreateDIBSection(IntPtr.Zero, ref header, DibRgbColors, out var bits, IntPtr.Zero, 0);
        if (hbitmap == IntPtr.Zero) throw new InvalidOperationException("CreateDIBSection failed");
        try
        {
            Marshal.Copy(bgra, 0, bits, bgra.Length);
            return Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally { DeleteObject(hbitmap); }
    }

    /// <summary>
    /// Only the COM face of <paramref name="inner"/>. Wrapped in a WPF
    /// <see cref="DataObject"/>, it is read the way WPF reads another program's
    /// clipboard data: through its OLE converter, each format fetched into an
    /// HGLOBAL and handed back from there. No clipboard is involved.
    /// </summary>
    public sealed class ComOnly(ComDataObject inner) : ComDataObject
    {
        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) =>
            inner.DAdvise(ref pFormatetc, advf, adviseSink, out connection);
        public void DUnadvise(int connection) => inner.DUnadvise(connection);
        public int EnumDAdvise(out IEnumSTATDATA? enumAdvise) => inner.EnumDAdvise(out enumAdvise);
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);
        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) =>
            inner.GetCanonicalFormatEtc(ref formatIn, out formatOut);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => inner.GetData(ref format, out medium);
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => inner.GetDataHere(ref format, ref medium);
        public int QueryGetData(ref FORMATETC format) => inner.QueryGetData(ref format);
        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
            inner.SetData(ref formatIn, ref medium, release);
    }

    /// <summary>
    /// A data object whose "PNG" read throws what a failed clipboard read throws (a
    /// COMException, here carrying CLIPBRD_E_BAD_DATA) while every other read is
    /// answered by <paramref name="inner"/>.
    /// </summary>
    public sealed class PngReadFails(DataObject inner) : WpfDataObject
    {
        private const int ClipbrdEBadData = unchecked((int)0x800401D3);

        public object? GetData(string format) => GetData(format, autoConvert: true);
        public object? GetData(Type format) => inner.GetData(format);
        public object? GetData(string format, bool autoConvert) =>
            format == "PNG" ? throw Marshal.GetExceptionForHR(ClipbrdEBadData)! : inner.GetData(format, autoConvert);
        public bool GetDataPresent(string format) => inner.GetDataPresent(format);
        public bool GetDataPresent(Type format) => inner.GetDataPresent(format);
        public bool GetDataPresent(string format, bool autoConvert) => inner.GetDataPresent(format, autoConvert);
        public string[] GetFormats() => inner.GetFormats();
        public string[] GetFormats(bool autoConvert) => inner.GetFormats(autoConvert);
        public void SetData(object data) => inner.SetData(data);
        public void SetData(string format, object data) => inner.SetData(format, data);
        public void SetData(Type format, object data) => inner.SetData(format, data);
        public void SetData(string format, object data, bool autoConvert) => inner.SetData(format, data, autoConvert);
    }
}
