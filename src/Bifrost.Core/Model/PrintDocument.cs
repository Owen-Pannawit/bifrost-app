namespace Bifrost.Core.Model;

public enum Alignment { Left, Center, Right }

public enum MediaType { LabelGap, LabelBlackMark, Continuous, Linerless }

public enum CutMode { Full, Partial }

public enum Symbology { Code128, Code39, Ean13, Itf, UpcA }

/// <summary>
/// The intermediate representation. The only thing a driver ever sees.
/// </summary>
/// <remarks>
/// <para>
/// <b>The IR models intent, not pixels.</b> A <see cref="PrintBlock.Barcode"/> says "CODE128, this
/// value, 80 dots high" — not where the bars go. This matters because ZPL and CPCL position
/// elements absolutely on a defined label canvas, while ESC/POS streams sequentially down a
/// continuous roll. Each driver realises the same intent in its own idiom. An IR of absolute
/// coordinates would have forced the ESC/POS driver to emulate a page model it does not have.
/// </para>
/// <para>
/// Contains no Android types — this project targets net10.0, not net10.0-android, so the compiler
/// enforces it (IMP-02 §2.1).
/// </para>
/// <para>See Docs/03-design/05-print-payload-schema.md §6.</para>
/// </remarks>
public sealed record PrintDocument(
    int WidthDots,
    MediaType MediaType,
    IReadOnlyList<PrintBlock> Blocks,
    int Copies = 1,
    bool CutAfter = false);

/// <summary>One element of a document. Closed hierarchy — drivers switch exhaustively over it.</summary>
public abstract record PrintBlock(Alignment Align)
{
    public sealed record Text(
        string Value,
        int SizeMultiplier,
        bool Bold,
        bool Underline,
        bool Invert,
        string? FontId,
        Alignment Align) : PrintBlock(Align)
    {
        public Text(string value, int sizeMultiplier = 1, Alignment align = Alignment.Left, bool bold = false)
            : this(value, sizeMultiplier, bold, Underline: false, Invert: false, FontId: null, align) { }
    }

    public sealed record Barcode(
        Symbology Symbology,
        string Value,
        int HeightDots,
        int ModuleWidth,
        bool ShowText,
        Alignment Align) : PrintBlock(Align)
    {
        /// <summary>A barcode with demo-safe defaults.</summary>
        /// <remarks>
        /// ModuleWidth defaults to 3, not 2. Wider bars are more forgiving of a warehouse scanner,
        /// a dirty printhead and a low battery — and an unscannable label is the defect this
        /// project must not ship. Demo plan risk D-4.
        /// </remarks>
        public static Barcode Of(
            Symbology symbology,
            string value,
            int heightDots = 80,
            int moduleWidth = 3,
            bool showText = true,
            Alignment align = Alignment.Center)
            => new(symbology, value, heightDots, moduleWidth, showText, align);
    }

    public sealed record QrCode(
        string Value,
        int Scale,
        EccLevel ErrorCorrection,
        Alignment Align) : PrintBlock(Align)
    {
        /// <remarks>
        /// Error correction defaults to Q, not the M that most libraries use. A label lives on a
        /// bin in a warehouse: it gets scuffed, taped over and handled with gloves, and the extra
        /// redundancy costs a few millimetres.
        /// </remarks>
        public static QrCode Of(
            string value,
            int scale = 5,
            EccLevel errorCorrection = EccLevel.Q,
            Alignment align = Alignment.Center)
            => new(value, scale, errorCorrection, align);
    }

    public sealed record Image(
        MonochromeBitmap Bitmap,
        Alignment Align) : PrintBlock(Align);

    public sealed record Feed(int Dots, Alignment Align = Alignment.Left) : PrintBlock(Align);

    public sealed record Cut(CutMode Mode, Alignment Align = Alignment.Left) : PrintBlock(Align);

    // Rule is defined by DES-05 §6 but not implemented for the demo.
}

/// <summary>QR error-correction level. Higher survives more damage at the cost of size.</summary>
public enum EccLevel { L, M, Q, H }
