using System.Globalization;
using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Drivers.Layout;

namespace Bifrost.Drivers.Cpcl;

/// <summary>
/// CPCL — the native language of Zebra's mobile label printers (the ZQ family).
/// </summary>
/// <remarks>
/// <para>
/// Written before Spike A reports, deliberately. The available printers are unidentified, and with
/// a two-week budget the schedule cannot depend on which answer comes back: with both ESC/POS and
/// CPCL in hand, risk D-1 costs nothing either way.
/// </para>
/// <para>
/// <b>Absolute positioning.</b> Unlike ESC/POS, every element carries coordinates and the label
/// height must be known before the first byte is emitted — hence the layout pass. See DES-06 §4.3.
/// </para>
/// </remarks>
public sealed class CpclDriver : IPrinterDriver
{
    private const string Crlf = "\r\n";

    public PrinterLanguage Language => PrinterLanguage.Cpcl;

    public DriverCapabilities Capabilities { get; } = new(
        SupportedSymbologies: new HashSet<Symbology>
        {
            Symbology.Code128, Symbology.Code39, Symbology.Ean13, Symbology.Itf, Symbology.UpcA,
        },
        SupportsQr: true,
        SupportsImages: false,      // EG/CG deferred to Phase 3
        SupportsCut: false,         // ZQ-family mobile printers rarely have a cutter
        SupportsStatusQuery: true,  // Link-OS firmware
        SupportsInvert: false,
        MaxTextSizeMultiplier: 4,
        PositioningModel: PositioningModel.Absolute);

    public byte[] Serialise(PrintDocument document, PrinterProfile printer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(printer);

        var engine = new AbsoluteLayoutEngine(document.WidthDots);
        var height = engine.TotalHeight(document.Blocks);
        var placed = engine.Layout(document.Blocks);

        var sb = new StringBuilder(512);

        // ! <x-offset> <x-dpi> <y-dpi> <height> <qty>
        sb.Append(CultureInfo.InvariantCulture,
            $"! 0 {printer.Dpi} {printer.Dpi} {height} {document.Copies}").Append(Crlf);

        foreach (var p in placed)
        {
            switch (p.Block)
            {
                case PrintBlock.Text t:
                    EmitText(sb, t, p);
                    break;

                case PrintBlock.Barcode b:
                    EmitBarcode(sb, b, p);
                    break;

                case PrintBlock.Cut:
                    // No cutter on this hardware class. Silently ignored, as the schema promises.
                    break;
            }
        }

        // FORM then PRINT. Omitting FORM misfeeds the next label on gap media — a failure that
        // shows up as every subsequent label being offset, not as an error.
        sb.Append("FORM").Append(Crlf);
        sb.Append("PRINT").Append(Crlf);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static void EmitText(StringBuilder sb, PrintBlock.Text text, PositionedBlock p)
    {
        // TEXT <font> <size> <x> <y> <data>
        var font = text.SizeMultiplier >= 3 ? 4 : 7;
        sb.Append(CultureInfo.InvariantCulture,
            $"TEXT {font} 0 {p.X} {p.Y} {Sanitise(text.Value)}").Append(Crlf);
    }

    private static void EmitBarcode(StringBuilder sb, PrintBlock.Barcode barcode, PositionedBlock p)
    {
        // BARCODE <type> <width> <ratio> <height> <x> <y> <data>
        sb.Append(CultureInfo.InvariantCulture,
            $"BARCODE {TypeName(barcode.Symbology)} {barcode.ModuleWidth} 1 {barcode.HeightDots} " +
            $"{p.X} {p.Y} {Sanitise(barcode.Value)}").Append(Crlf);

        if (barcode.ShowText)
        {
            // Must precede the BARCODE it applies to in some firmware revisions; emitted after
            // here because Link-OS applies it to the most recent barcode. Verify on real hardware.
            sb.Append("BARCODE-TEXT 7 0 5").Append(Crlf);
        }
    }

    private static string TypeName(Symbology symbology) => symbology switch
    {
        Symbology.Code128 => "128",
        Symbology.Code39 => "39",
        Symbology.Ean13 => "EAN13",
        Symbology.Itf => "I2OF5",
        Symbology.UpcA => "UPCA",
        _ => throw new NotSupportedException($"Symbology {symbology} has no CPCL type name."),
    };

    /// <summary>CPCL is line-oriented: a newline inside a value would terminate the command.</summary>
    private static string Sanitise(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
             .Replace("\n", " ", StringComparison.Ordinal);

    public byte[]? StatusQuery() =>
        Encoding.ASCII.GetBytes($"! U1 getvar \"media.status\"{Crlf}");

    public PrinterStatus ParseStatus(ReadOnlySpan<byte> response)
    {
        if (response.Length == 0) return PrinterStatus.Unknown;

        var text = Encoding.ASCII.GetString(response);
        if (text.Contains("out", StringComparison.OrdinalIgnoreCase))
        {
            return new PrinterStatus(IsReady: false, OutOfPaper: true);
        }

        return new PrinterStatus(IsReady: true, OutOfPaper: false);
    }

    public bool Matches(ReadOnlySpan<byte> identificationResponse)
    {
        if (identificationResponse.Length == 0) return false;
        var text = Encoding.ASCII.GetString(identificationResponse);
        return text.Contains("cpcl", StringComparison.OrdinalIgnoreCase)
            || text.Contains("zpl", StringComparison.OrdinalIgnoreCase);
    }
}
