using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Drivers.Cpcl;
using Bifrost.Drivers.EscPos;

namespace Bifrost.Core.Tests;

/// <summary>
/// The self-check label is what answers the go/no-go question on real hardware, so what it
/// contains is not a detail — a test print without a scannable barcode proves nothing.
/// </summary>
public sealed class SelfCheckDocumentTests
{
    private static readonly PrinterProfile EscPos = new(
        Id: "demo", BluetoothAddress: "00:11:22:33:44:55", DisplayName: "Demo 80mm",
        TransportType: TransportType.BtClassic, Language: PrinterLanguage.EscPos,
        PrintWidthDots: PrinterProfile.Widths.Receipt80mmAt203Dpi, Dpi: 203,
        MediaType: MediaType.Continuous, HasCutter: true, SupportsStatusQuery: true);

    private static readonly PrinterProfile Cpcl = EscPos with
    {
        Language = PrinterLanguage.Cpcl,
        PrintWidthDots = PrinterProfile.Widths.Label4InchAt203Dpi,
        MediaType = MediaType.LabelGap,
        HasCutter = false,
    };

    [Fact]
    public void FR_402_carries_a_scannable_barcode()
    {
        // Without this the test print cannot answer the question it exists to answer.
        var doc = SelfCheckDocument.Create(EscPos, "ZQ521-A17", "14:32:07");

        var barcode = Assert.Single(doc.Blocks.OfType<PrintBlock.Barcode>());
        Assert.Equal(Symbology.Code128, barcode.Symbology);
        Assert.Equal(SelfCheckDocument.BarcodeValue, barcode.Value);

        // moduleWidth 3, not 2: wider bars survive a dirty printhead and a low battery (risk D-4).
        Assert.True(barcode.ModuleWidth >= 3,
            "Narrow bars are the first thing to fail on a worn thermal head.");
    }

    [Fact]
    public void FR_402_states_printer_identity_and_capabilities()
    {
        // A photographed label should answer most of what support would otherwise have to ask.
        var doc = SelfCheckDocument.Create(EscPos, "ZQ521-A17", "14:32:07");
        var text = string.Join('\n', doc.Blocks.OfType<PrintBlock.Text>().Select(t => t.Value));

        Assert.Contains("ZQ521-A17", text, StringComparison.Ordinal);
        Assert.Contains("EscPos", text, StringComparison.Ordinal);
        Assert.Contains("576", text, StringComparison.Ordinal);   // print width
        Assert.Contains("203", text, StringComparison.Ordinal);   // dpi
    }

    [Fact]
    public void The_timestamp_appears_so_an_old_label_is_not_mistaken_for_a_new_one()
    {
        var doc = SelfCheckDocument.Create(EscPos, "printer", "14:32:07");

        Assert.Contains(doc.Blocks.OfType<PrintBlock.Text>(),
            t => t.Value.Contains("14:32:07", StringComparison.Ordinal));
    }

    [Fact]
    public void It_adopts_the_connected_printer_width_not_a_hardcoded_one()
    {
        Assert.Equal(576, SelfCheckDocument.Create(EscPos, "p", "t").WidthDots);
        Assert.Equal(832, SelfCheckDocument.Create(Cpcl, "p", "t").WidthDots);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void It_serialises_on_both_drivers(bool cpcl)
    {
        // The test print must work whichever answer Spike A gives, or it cannot be the first
        // thing run on unfamiliar hardware.
        var profile = cpcl ? Cpcl : EscPos;
        IPrinterDriver driver = cpcl ? new CpclDriver() : new EscPosDriver();

        var bytes = driver.Serialise(SelfCheckDocument.Create(profile, "printer", "14:32:07"), profile);

        Assert.NotEmpty(bytes);
        Assert.Contains(SelfCheckDocument.BarcodeValue, Encoding.ASCII.GetString(bytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public void It_feeds_enough_to_clear_the_tear_bar()
    {
        // Content stopping under the head means the operator cannot read or scan it without
        // pulling media through by hand.
        var doc = SelfCheckDocument.Create(EscPos, "printer", "14:32:07");

        Assert.True(doc.Blocks.OfType<PrintBlock.Feed>().Sum(f => f.Dots) >= 72);
    }
}
