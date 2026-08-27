using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Drivers.Cpcl;
using Bifrost.Drivers.EscPos;

namespace Bifrost.Drivers.Tests;

/// <summary>Golden-output tests for CPCL — see EscPosDriverTests for why these are byte-exact.</summary>
public sealed class CpclDriverTests
{
    private static readonly PrinterProfile Zq521 = new(
        Id: "zq521",
        BluetoothAddress: "AC:3F:A4:00:00:17",
        DisplayName: "ZQ521-A17",
        TransportType: TransportType.Mock,
        Language: PrinterLanguage.Cpcl,
        PrintWidthDots: PrinterProfile.Widths.Label4InchAt203Dpi,
        Dpi: 203,
        MediaType: MediaType.LabelGap,
        HasCutter: false,
        SupportsStatusQuery: true);

    private static PrintDocument Doc(params PrintBlock[] blocks) =>
        new(PrinterProfile.Widths.Label4InchAt203Dpi, MediaType.LabelGap, blocks);

    private static string Render(PrintDocument doc) =>
        Encoding.ASCII.GetString(new CpclDriver().Serialise(doc, Zq521));

    [Fact]
    public void FR_605_header_declares_dpi_computed_height_and_copy_count()
    {
        var text = Render(Doc(new PrintBlock.Text("6205-2RS")));

        var first = text.Split("\r\n")[0];
        Assert.StartsWith("! 0 203 203 ", first, StringComparison.Ordinal);
        Assert.EndsWith(" 1", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_height_grows_with_content()
    {
        static int Height(string rendered) =>
            int.Parse(rendered.Split("\r\n")[0].Split(' ')[4], System.Globalization.CultureInfo.InvariantCulture);

        var shortDoc = Height(Render(Doc(new PrintBlock.Text("A"))));
        var tallDoc = Height(Render(Doc(
            new PrintBlock.Text("A"),
            PrintBlock.Barcode.Of(Symbology.Code128, "6205-2RS", heightDots: 120),
            new PrintBlock.Text("B"))));

        Assert.True(tallDoc > shortDoc,
            $"Taller content must yield a taller label: {tallDoc} should exceed {shortDoc}");
    }

    [Fact]
    public void Every_label_terminates_with_FORM_then_PRINT()
    {
        var lines = Render(Doc(new PrintBlock.Text("X")))
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Omitting FORM misfeeds the next label on gap media — every subsequent label offsets.
        Assert.Equal("FORM", lines[^2]);
        Assert.Equal("PRINT", lines[^1]);
    }

    [Fact]
    public void FR_309_barcode_carries_type_module_width_height_and_coordinates()
    {
        var text = Render(Doc(
            PrintBlock.Barcode.Of(Symbology.Code128, "6205-2RS", heightDots: 80, moduleWidth: 3)));

        var line = text.Split("\r\n").Single(l => l.StartsWith("BARCODE ", StringComparison.Ordinal));
        var parts = line.Split(' ');

        Assert.Equal("BARCODE", parts[0]);
        Assert.Equal("128", parts[1]);
        Assert.Equal("3", parts[2]);      // module width
        Assert.Equal("80", parts[4]);     // height
        Assert.Equal("6205-2RS", parts[^1]);
    }

    [Theory]
    [InlineData(Symbology.Code128, "128")]
    [InlineData(Symbology.Code39, "39")]
    [InlineData(Symbology.Ean13, "EAN13")]
    [InlineData(Symbology.Itf, "I2OF5")]
    [InlineData(Symbology.UpcA, "UPCA")]
    public void FR_309_each_symbology_maps_to_its_CPCL_type_name(Symbology symbology, string expected)
    {
        var text = Render(Doc(PrintBlock.Barcode.Of(symbology, "12345678")));

        Assert.Contains($"BARCODE {expected} ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Centred_content_is_offset_from_the_left_edge()
    {
        var centred = Render(Doc(new PrintBlock.Text("MID", align: Alignment.Center)));
        var left = Render(Doc(new PrintBlock.Text("MID", align: Alignment.Left)));

        static int X(string rendered) =>
            int.Parse(rendered.Split("\r\n").First(l => l.StartsWith("TEXT", StringComparison.Ordinal))
                              .Split(' ')[3], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, X(left));
        Assert.True(X(centred) > 0, "Centred text must have a non-zero X coordinate");
    }

    [Fact]
    public void Blocks_stack_downwards_without_overlapping()
    {
        var text = Render(Doc(new PrintBlock.Text("FIRST"), new PrintBlock.Text("SECOND")));

        var ys = text.Split("\r\n")
            .Where(l => l.StartsWith("TEXT", StringComparison.Ordinal))
            .Select(l => int.Parse(l.Split(' ')[4], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(2, ys.Length);
        Assert.True(ys[1] > ys[0], "The second block must sit below the first");
    }

    [Fact]
    public void A_newline_in_a_value_cannot_terminate_the_command()
    {
        // CPCL is line-oriented; an unsanitised newline would truncate the label silently.
        var text = Render(Doc(new PrintBlock.Text("6205\r\n2RS")));

        var textLines = text.Split("\r\n").Where(l => l.StartsWith("TEXT", StringComparison.Ordinal));
        Assert.Single(textLines);
    }

    [Fact]
    public void Cut_is_ignored_because_this_hardware_class_has_no_cutter()
    {
        var text = Render(Doc(new PrintBlock.Text("X"), new PrintBlock.Cut(CutMode.Full)));

        Assert.DoesNotContain("CUT", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(new CpclDriver().Capabilities.SupportsCut);
    }

    [Fact]
    public void A_QR_is_emitted_as_a_positioned_block_terminated_by_ENDQR()
    {
        var text = Render(Doc(PrintBlock.QrCode.Of("PN=6205-2RS;LOT=L2408", scale: 4)));
        var lines = text.Split("\r\n");

        var start = Array.FindIndex(lines, l => l.StartsWith("B QR ", StringComparison.Ordinal));
        Assert.True(start >= 0, "Expected a 'B QR' block");

        // B QR <x> <y> M 2 U <scale>
        var parts = lines[start].Split(' ');
        Assert.Equal("M", parts[4]);
        Assert.Equal("2", parts[5]);       // model 2
        Assert.Equal("4", parts[7]);       // scale

        // The data line carries the ECC level and the automatic data-mode marker.
        Assert.StartsWith("QA,", lines[start + 1], StringComparison.Ordinal);
        Assert.Contains("PN=6205-2RS", lines[start + 1], StringComparison.Ordinal);

        // Without ENDQR the printer keeps consuming following commands as QR data.
        Assert.Equal("ENDQR", lines[start + 2]);
    }

    [Theory]
    [InlineData(EccLevel.L, "LA,")]
    [InlineData(EccLevel.M, "MA,")]
    [InlineData(EccLevel.Q, "QA,")]
    [InlineData(EccLevel.H, "HA,")]
    public void Each_error_correction_level_reaches_the_data_line(EccLevel level, string expected)
    {
        var text = Render(Doc(PrintBlock.QrCode.Of("X", errorCorrection: level)));

        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_image_is_emitted_as_EG_with_byte_width_and_uppercase_hex()
    {
        var bitmap = new MonochromeBitmap(16, 4);
        bitmap.Set(0, 0);       // 0x80 in the first byte
        bitmap.Set(8, 0);       // 0x80 in the second

        var text = Render(Doc(new PrintBlock.Image(bitmap, Alignment.Left)));
        var line = text.Split("\r\n").Single(l => l.StartsWith("EG ", StringComparison.Ordinal));
        var parts = line.Split(' ');

        Assert.Equal("2", parts[1]);    // bytes per row, not dots — the usual mistake
        Assert.Equal("4", parts[2]);    // height in dots
        Assert.StartsWith("8080", parts[5], StringComparison.Ordinal);
    }

    [Fact]
    public void FR_607_identifies_itself_from_a_Zebra_identity_response()
    {
        var driver = new CpclDriver();

        Assert.True(driver.Matches("cpcl zpl"u8));
        Assert.False(driver.Matches([]));
        Assert.False(driver.Matches("something else"u8));
    }

    [Fact]
    public void FR_610_both_drivers_satisfy_the_same_interface_for_the_same_document()
    {
        // The point of ADR-007: a document is language-agnostic, and adding a driver costs one
        // implementation with no change to anything upstream.
        var doc = Doc(
            new PrintBlock.Text("6205-2RS", sizeMultiplier: 3, align: Alignment.Center),
            PrintBlock.Barcode.Of(Symbology.Code128, "6205-2RS"));

        IPrinterDriver[] drivers = [new EscPosDriver(), new CpclDriver()];

        foreach (var driver in drivers)
        {
            var bytes = driver.Serialise(doc, Zq521 with { Language = driver.Language });

            Assert.NotEmpty(bytes);
            Assert.Contains("6205-2RS", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
            Assert.Contains(Symbology.Code128, driver.Capabilities.SupportedSymbologies);
        }
    }
}
