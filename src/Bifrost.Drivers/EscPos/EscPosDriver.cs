using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;

namespace Bifrost.Drivers.EscPos;

/// <summary>
/// ESC/POS — the de-facto receipt printer standard, used by almost every low-cost Bluetooth
/// thermal printer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sequential positioning model.</b> Blocks emit in order down a continuous roll; alignment is a
/// mode set before each block rather than a coordinate. This is why the IR models intent rather
/// than pixels — see Docs/03-design/05-print-payload-schema.md §6.1.
/// </para>
/// <para>
/// Text uses native font commands, never a rasterised bitmap: content is English and numeric
/// (D-09), so there is no reason to pay the payload cost of a raster over Bluetooth (FR-311).
/// </para>
/// </remarks>
public sealed class EscPosDriver : IPrinterDriver
{
    private static readonly Encoding Ascii = Encoding.ASCII;

    public PrinterLanguage Language => PrinterLanguage.EscPos;

    public DriverCapabilities Capabilities { get; } = new(
        SupportedSymbologies: new HashSet<Symbology>
        {
            Symbology.Code128, Symbology.Code39, Symbology.Ean13, Symbology.Itf, Symbology.UpcA,
        },
        SupportsQr: false,          // model-dependent; not claimed for the demo
        SupportsImages: false,      // deferred to Phase 3
        SupportsCut: true,
        SupportsStatusQuery: true,  // best-effort — many clones ignore DLE EOT
        SupportsInvert: true,
        MaxTextSizeMultiplier: 8,
        PositioningModel: PositioningModel.Sequential);

    public byte[] Serialise(PrintDocument document, PrinterProfile printer)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(printer);

        var output = new List<byte>(256);
        output.AddRange(EscPosCommands.Initialise);

        foreach (var block in document.Blocks)
        {
            EmitBlock(output, block);
        }

        if (document.CutAfter)
        {
            // Feed past the tear bar before cutting, or the cut lands mid-content.
            output.AddRange(EscPosCommands.FeedLines(3));
            output.AddRange(EscPosCommands.Cut(partial: false));
        }

        return [.. output];
    }

    private static void EmitBlock(List<byte> output, PrintBlock block)
    {
        switch (block)
        {
            case PrintBlock.Text text:
                EmitText(output, text);
                break;

            case PrintBlock.Barcode barcode:
                EmitBarcode(output, barcode);
                break;

            case PrintBlock.Feed feed:
                output.AddRange(EscPosCommands.FeedDots(feed.Dots));
                break;

            case PrintBlock.Cut cut:
                output.AddRange(EscPosCommands.Cut(cut.Mode == CutMode.Partial));
                break;

            default:
                throw new NotSupportedException(
                    $"{nameof(EscPosDriver)} cannot emit {block.GetType().Name}. " +
                    "Declare it unsupported in DriverCapabilities rather than reaching here.");
        }
    }

    private static void EmitText(List<byte> output, PrintBlock.Text text)
    {
        output.AddRange(EscPosCommands.Align(AlignByte(text.Align)));
        output.AddRange(EscPosCommands.TextSize(text.SizeMultiplier));

        if (text.Bold) output.AddRange(EscPosCommands.Bold(true));
        if (text.Underline) output.AddRange(EscPosCommands.Underline(true));
        if (text.Invert) output.AddRange(EscPosCommands.Invert(true));

        output.AddRange(Ascii.GetBytes(text.Value));
        output.Add(EscPosCommands.Lf);

        // Reset only what was set. Leaving a mode on silently restyles every later block.
        if (text.Invert) output.AddRange(EscPosCommands.Invert(false));
        if (text.Underline) output.AddRange(EscPosCommands.Underline(false));
        if (text.Bold) output.AddRange(EscPosCommands.Bold(false));
        output.AddRange(EscPosCommands.TextSize(1));
    }

    private static void EmitBarcode(List<byte> output, PrintBlock.Barcode barcode)
    {
        output.AddRange(EscPosCommands.Align(AlignByte(barcode.Align)));
        output.AddRange(EscPosCommands.BarcodeHeight(barcode.HeightDots));
        output.AddRange(EscPosCommands.BarcodeModuleWidth(barcode.ModuleWidth));
        output.AddRange(EscPosCommands.BarcodeTextPosition(barcode.ShowText));
        output.AddRange(EscPosCommands.BarcodeTextFont());

        var data = Ascii.GetBytes(barcode.Value);
        output.AddRange(EscPosCommands.Barcode(TypeByte(barcode.Symbology), data));
        output.Add(EscPosCommands.Lf);
    }

    private static byte AlignByte(Alignment align) => align switch
    {
        Alignment.Left => 0,
        Alignment.Center => 1,
        Alignment.Right => 2,
        _ => 0,
    };

    private static byte TypeByte(Symbology symbology) => symbology switch
    {
        Symbology.Code128 => EscPosCommands.BarcodeType.Code128,
        Symbology.Code39 => EscPosCommands.BarcodeType.Code39,
        Symbology.Ean13 => EscPosCommands.BarcodeType.Ean13,
        Symbology.Itf => EscPosCommands.BarcodeType.Itf,
        Symbology.UpcA => EscPosCommands.BarcodeType.UpcA,
        _ => throw new NotSupportedException($"Symbology {symbology} is not an ESC/POS barcode type."),
    };

    public byte[]? StatusQuery() => [.. EscPosCommands.StatusQuery];

    public PrinterStatus ParseStatus(ReadOnlySpan<byte> response)
    {
        // DLE EOT 1 returns one status byte. Bit 3 set = offline.
        // Cheap clones return nothing at all, which surfaces as an empty span.
        if (response.Length == 0) return PrinterStatus.Unknown;

        var b = response[0];
        var offline = (b & 0b0000_1000) != 0;
        return new PrinterStatus(IsReady: !offline);
    }

    public bool Matches(ReadOnlySpan<byte> identificationResponse)
        // ESC/POS has no reliable identity query. Detection is by elimination, or manual override
        // in settings (FR-607, DES-06 §9).
        => false;
}
