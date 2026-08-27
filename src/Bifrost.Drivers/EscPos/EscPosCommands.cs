namespace Bifrost.Drivers.EscPos;

/// <summary>
/// Raw ESC/POS control sequences. Named so the driver reads as intent rather than as magic bytes.
/// </summary>
/// <remarks>
/// Reference: Epson ESC/POS command specification. See Docs/03-design/06-printer-abstraction.md §4.1.
/// </remarks>
internal static class EscPosCommands
{
    public const byte Esc = 0x1B;
    public const byte Gs = 0x1D;
    public const byte Lf = 0x0A;

    /// <summary>ESC @ — initialise. Clears buffer and resets all modes.</summary>
    public static ReadOnlySpan<byte> Initialise => [Esc, (byte)'@'];

    /// <summary>ESC a n — justification. 0 left, 1 centre, 2 right.</summary>
    public static byte[] Align(byte n) => [Esc, (byte)'a', n];

    /// <summary>
    /// GS ! n — character size. High nibble is width multiplier, low nibble is height, each 0–7
    /// (meaning 1×–8×).
    /// </summary>
    public static byte[] TextSize(int multiplier)
    {
        var n = (byte)Math.Clamp(multiplier - 1, 0, 7);
        return [Gs, (byte)'!', (byte)((n << 4) | n)];
    }

    /// <summary>ESC E n — emphasised (bold) on/off.</summary>
    public static byte[] Bold(bool on) => [Esc, (byte)'E', on ? (byte)1 : (byte)0];

    /// <summary>ESC - n — underline. 0 off, 1 one-dot, 2 two-dot.</summary>
    public static byte[] Underline(bool on) => [Esc, (byte)'-', on ? (byte)1 : (byte)0];

    /// <summary>GS B n — white/black reverse printing.</summary>
    public static byte[] Invert(bool on) => [Gs, (byte)'B', on ? (byte)1 : (byte)0];

    /// <summary>GS h n — barcode height in dots.</summary>
    public static byte[] BarcodeHeight(int dots) => [Gs, (byte)'h', (byte)Math.Clamp(dots, 1, 255)];

    /// <summary>GS w n — barcode module (narrow bar) width, 2–6.</summary>
    public static byte[] BarcodeModuleWidth(int width) => [Gs, (byte)'w', (byte)Math.Clamp(width, 2, 6)];

    /// <summary>GS H n — human-readable text position. 0 none, 2 below.</summary>
    public static byte[] BarcodeTextPosition(bool show) => [Gs, (byte)'H', show ? (byte)2 : (byte)0];

    /// <summary>GS f n — font for the human-readable text. 0 = Font A.</summary>
    public static byte[] BarcodeTextFont(byte font = 0) => [Gs, (byte)'f', font];

    /// <summary>
    /// GS k m n d1…dn — print barcode, "function B" form with an explicit length byte.
    /// </summary>
    /// <remarks>
    /// Function B (<c>m</c> ≥ 65) is used rather than the NUL-terminated function A, because the
    /// length byte makes the command unambiguous for data that could contain a zero byte.
    /// </remarks>
    public static byte[] Barcode(byte type, ReadOnlySpan<byte> data)
    {
        var result = new byte[4 + data.Length];
        result[0] = Gs;
        result[1] = (byte)'k';
        result[2] = type;
        result[3] = (byte)data.Length;
        data.CopyTo(result.AsSpan(4));
        return result;
    }

    /// <summary>Barcode type selectors for the GS k function-B form.</summary>
    public static class BarcodeType
    {
        public const byte UpcA = 65;
        public const byte Ean13 = 67;
        public const byte Code39 = 69;
        public const byte Itf = 70;
        public const byte Code128 = 73;
    }

    // ---------------------------------------------------------------- QR (GS ( k)

    /// <summary>GS ( k — store the QR data. pL/pH cover the 3 header bytes plus the payload.</summary>
    public static byte[] QrStore(ReadOnlySpan<byte> data)
    {
        var length = data.Length + 3;
        var result = new byte[8 + data.Length];
        result[0] = Gs;
        result[1] = (byte)'(';
        result[2] = (byte)'k';
        result[3] = (byte)(length & 0xFF);
        result[4] = (byte)(length >> 8);
        result[5] = 49;     // cn — QR function group
        result[6] = 80;     // fn 80 — store data
        result[7] = 48;     // m
        data.CopyTo(result.AsSpan(8));
        return result;
    }

    /// <summary>GS ( k fn 67 — module size in dots, 1–16.</summary>
    public static byte[] QrModuleSize(int scale) =>
        [Gs, (byte)'(', (byte)'k', 3, 0, 49, 67, (byte)Math.Clamp(scale, 1, 16)];

    /// <summary>GS ( k fn 69 — error correction: 48=L, 49=M, 50=Q, 51=H.</summary>
    public static byte[] QrErrorCorrection(byte level) =>
        [Gs, (byte)'(', (byte)'k', 3, 0, 49, 69, level];

    /// <summary>GS ( k fn 81 — print the stored symbol.</summary>
    public static byte[] QrPrint() => [Gs, (byte)'(', (byte)'k', 3, 0, 49, 81, 48];

    // ---------------------------------------------------------------- raster (GS v 0)

    /// <summary>GS v 0 m xL xH yL yH — raster bit image, normal size.</summary>
    public static byte[] RasterHeader(int bytesPerRow, int heightDots) =>
    [
        Gs, (byte)'v', (byte)'0', 0,
        (byte)(bytesPerRow & 0xFF), (byte)(bytesPerRow >> 8),
        (byte)(heightDots & 0xFF), (byte)(heightDots >> 8),
    ];

    /// <summary>ESC d n — feed n lines.</summary>
    public static byte[] FeedLines(int lines) => [Esc, (byte)'d', (byte)Math.Clamp(lines, 0, 255)];

    /// <summary>ESC J n — feed n dots.</summary>
    public static byte[] FeedDots(int dots) => [Esc, (byte)'J', (byte)Math.Clamp(dots, 0, 255)];

    /// <summary>GS V m — cut. 0 full, 1 partial.</summary>
    public static byte[] Cut(bool partial) => [Gs, (byte)'V', partial ? (byte)1 : (byte)0];

    /// <summary>DLE EOT 1 — real-time status transmission.</summary>
    /// <remarks>Not universal on low-cost clones — hence IPrinterDriver.StatusQuery() is nullable.</remarks>
    public static ReadOnlySpan<byte> StatusQuery => [0x10, 0x04, 0x01];
}
