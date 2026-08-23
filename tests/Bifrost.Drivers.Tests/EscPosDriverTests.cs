using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Drivers.EscPos;

namespace Bifrost.Drivers.Tests;

/// <summary>
/// Golden-output tests: PrintDocument in, exact bytes out.
/// </summary>
/// <remarks>
/// These are the highest-value tests in the suite. A command-language regression is invisible in
/// code review, invisible in a green functional test, and produces a label that is subtly wrong —
/// a barcode a scanner rejects six weeks later. Byte-exact assertions are the only way to catch it.
/// See Docs/05-testing/01-test-strategy.md §3.1.
/// </remarks>
public sealed class EscPosDriverTests
{
    private static readonly PrinterProfile Profile = new(
        Id: "test",
        BluetoothAddress: "00:11:22:33:44:55",
        DisplayName: "Test 80mm",
        TransportType: TransportType.Mock,
        Language: PrinterLanguage.EscPos,
        PrintWidthDots: PrinterProfile.Widths.Receipt80mmAt203Dpi,
        Dpi: 203,
        MediaType: MediaType.Continuous,
        HasCutter: true,
        SupportsStatusQuery: true);

    private static PrintDocument Doc(params PrintBlock[] blocks) =>
        new(PrinterProfile.Widths.Receipt80mmAt203Dpi, MediaType.Continuous, blocks);

    [Fact]
    public void FR_605_output_always_begins_with_initialise()
    {
        var bytes = new EscPosDriver().Serialise(Doc(new PrintBlock.Text("X")), Profile);

        Assert.Equal(0x1B, bytes[0]);
        Assert.Equal((byte)'@', bytes[1]);
    }

    [Fact]
    public void FR_311_text_is_emitted_as_native_font_commands_not_a_raster()
    {
        var bytes = new EscPosDriver().Serialise(
            Doc(new PrintBlock.Text("6205-2RS", sizeMultiplier: 3, align: Alignment.Center, bold: true)),
            Profile);

        // The literal characters must appear — a rasterised implementation would not contain them.
        Assert.Contains("6205-2RS", Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);

        // GS ! 0x22 — width and height multiplier both 3 (encoded as 2 in each nibble).
        AssertContainsSequence(bytes, [0x1D, (byte)'!', 0x22]);

        // ESC a 1 — centre.
        AssertContainsSequence(bytes, [0x1B, (byte)'a', 0x01]);

        // ESC E 1 … ESC E 0 — bold on, then off again.
        AssertContainsSequence(bytes, [0x1B, (byte)'E', 0x01]);
        AssertContainsSequence(bytes, [0x1B, (byte)'E', 0x00]);
    }

    [Fact]
    public void FR_309_code128_uses_the_native_barcode_command_with_a_length_byte()
    {
        var bytes = new EscPosDriver().Serialise(
            Doc(PrintBlock.Barcode.Of(Symbology.Code128, "6205-2RS", heightDots: 80, moduleWidth: 3)),
            Profile);

        // GS h 80 — height.
        AssertContainsSequence(bytes, [0x1D, (byte)'h', 80]);

        // GS w 3 — module width. Wider bars are the demo's scannability margin (risk D-4).
        AssertContainsSequence(bytes, [0x1D, (byte)'w', 3]);

        // GS k 73 <len> "6205-2RS" — function B, CODE128, explicit length.
        byte[] expected = [0x1D, (byte)'k', 73, 8, .. "6205-2RS"u8];
        AssertContainsSequence(bytes, expected);
    }

    [Theory]
    [InlineData(Symbology.Code128, 73)]
    [InlineData(Symbology.Code39, 69)]
    [InlineData(Symbology.Ean13, 67)]
    [InlineData(Symbology.Itf, 70)]
    [InlineData(Symbology.UpcA, 65)]
    public void FR_309_each_symbology_maps_to_its_ESC_POS_type_byte(Symbology symbology, byte expected)
    {
        var bytes = new EscPosDriver().Serialise(
            Doc(PrintBlock.Barcode.Of(symbology, "12345678")), Profile);

        AssertContainsSequence(bytes, [0x1D, (byte)'k', expected]);
    }

    [Fact]
    public void Text_modes_are_reset_so_they_do_not_leak_into_later_blocks()
    {
        var bytes = new EscPosDriver().Serialise(
            Doc(new PrintBlock.Text("BIG", sizeMultiplier: 4, bold: true),
                new PrintBlock.Text("small")),
            Profile);

        // Size must return to 1× (GS ! 0x00) before the second block is written.
        var text = Encoding.ASCII.GetString(bytes);
        var bigIndex = text.IndexOf("BIG", StringComparison.Ordinal);
        var smallIndex = text.IndexOf("small", StringComparison.Ordinal);

        Assert.True(bigIndex >= 0 && smallIndex > bigIndex);
        AssertContainsSequenceWithin(bytes, [0x1D, (byte)'!', 0x00], bigIndex, smallIndex);
    }

    [Fact]
    public void CutAfter_feeds_past_the_tear_bar_before_cutting()
    {
        var doc = new PrintDocument(
            PrinterProfile.Widths.Receipt80mmAt203Dpi,
            MediaType.Continuous,
            [new PrintBlock.Text("X")],
            CutAfter: true);

        var bytes = new EscPosDriver().Serialise(doc, Profile);

        // ESC d 3 then GS V 0 — feed, then full cut. Cutting without feeding severs content.
        AssertContainsSequence(bytes, [0x1B, (byte)'d', 3, 0x1D, (byte)'V', 0x00]);
    }

    [Fact]
    public void FR_608_status_query_is_offered_and_an_empty_response_is_not_a_false_ready()
    {
        var driver = new EscPosDriver();

        Assert.NotNull(driver.StatusQuery());

        // A printer that answers nothing must not be reported as a confirmed state.
        var unknown = driver.ParseStatus([]);
        Assert.Equal(PrinterStatus.Unknown, unknown);

        // Bit 3 set means offline.
        Assert.False(driver.ParseStatus([0b0000_1000]).IsReady);
        Assert.True(driver.ParseStatus([0b0000_0000]).IsReady);
    }

    [Fact]
    public void Capabilities_do_not_over_claim()
    {
        var caps = new EscPosDriver().Capabilities;

        // Over-declaring produces broken output; under-declaring degrades gracefully (ADR-007).
        Assert.False(caps.SupportsQr);
        Assert.False(caps.SupportsImages);
        Assert.Equal(PositioningModel.Sequential, caps.PositioningModel);
        Assert.Contains(Symbology.Code128, caps.SupportedSymbologies);
    }

    private static void AssertContainsSequence(byte[] haystack, byte[] needle) =>
        Assert.True(
            IndexOf(haystack, needle, 0) >= 0,
            $"Expected byte sequence [{string.Join(' ', needle.Select(b => b.ToString("X2")))}] " +
            $"in [{string.Join(' ', haystack.Select(b => b.ToString("X2")))}]");

    private static void AssertContainsSequenceWithin(byte[] haystack, byte[] needle, int from, int to)
    {
        var index = IndexOf(haystack, needle, from);
        Assert.True(index >= 0 && index < to,
            $"Expected byte sequence between offsets {from} and {to}.");
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (var i = start; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j]) continue;
                match = false;
                break;
            }

            if (match) return i;
        }

        return -1;
    }
}
