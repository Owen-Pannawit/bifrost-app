using Bifrost.Core.Model;

namespace Bifrost.Core.Printing;

/// <summary>
/// Builds the self-check label printed by the Test Print action (FR-402).
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core, not in the Android service, because composing a <see cref="PrintDocument"/> is
/// domain logic and must stay testable without a device (NFR-601). The service supplies the
/// profile and the clock; nothing here touches Android.
/// </para>
/// <para>
/// The label's job is to prove the whole path — driver, transport, Bluetooth, printer — with no
/// web page involved, and to be photographable for a support ticket (DES-09 §5.3).
/// </para>
/// </remarks>
public static class SelfCheckDocument
{
    /// <summary>The value encoded in the test barcode. Fixed, so a scan can be checked against it.</summary>
    public const string BarcodeValue = "6205-2RS";

    /// <summary>
    /// The value encoded in the test QR. Carries more than the barcode on purpose — a QR that
    /// only holds a part number proves the symbol printed, not that a realistic payload fits.
    /// </summary>
    public const string QrValue = "PN=6205-2RS;LOT=L2408-0231;QTY=50";

    public static PrintDocument Create(PrinterProfile profile, string printerName, string timestamp)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new PrintDocument(
            profile.PrintWidthDots,
            profile.MediaType,
            [
                new PrintBlock.Text("BIFROST TEST", sizeMultiplier: 2, align: Alignment.Center, bold: true),
                new PrintBlock.Text(printerName, align: Alignment.Center),

                // Identity and capabilities on the paper: a photographed label then answers most
                // of what support would otherwise have to ask for.
                //
                // ASCII only, and deliberately so. Drivers encode with Encoding.ASCII (D-09 —
                // content is English and numeric), so a middle dot separator printed as "?" and
                // made the diagnostic label look like a fault in the driver. A self-check that
                // misleads about the thing it is checking is worse than none.
                new PrintBlock.Text(
                    $"{profile.Language} {profile.PrintWidthDots}dots {profile.Dpi}dpi",
                    align: Alignment.Center),

                // A timestamp so repeated test prints are distinguishable on the bench. Without it
                // an old label is easily mistaken for a new one, and a failure looks like a success.
                new PrintBlock.Text(timestamp, align: Alignment.Center),

                // The point of the exercise: a barcode a scanner must read first time.
                // moduleWidth 3 rather than 2 — wider bars survive a dirty printhead and a low
                // battery, and an unscannable label is the defect this project must not ship.
                PrintBlock.Barcode.Of(Symbology.Code128, BarcodeValue, heightDots: 70, moduleWidth: 3),

                // QR and image exercise the two paths a barcode does not: 2-D symbol generation
                // and raster transfer. Each fails independently, so a test print covering only
                // text and a barcode leaves half the driver unproven.
                PrintBlock.QrCode.Of(QrValue, scale: 4),

                new PrintBlock.Image(MonochromeBitmap.TestPattern(80), Alignment.Center),

                // Just enough to clear the tear bar. This button gets pressed repeatedly while
                // diagnosing, and every press costs paper.
                new PrintBlock.Feed(32),
            ]);
    }
}
