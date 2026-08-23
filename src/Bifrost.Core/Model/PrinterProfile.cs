namespace Bifrost.Core.Model;

public enum PrinterLanguage { EscPos, Zpl, Cpcl, Tspl }

public enum TransportType { BtClassic, Ble, Mock }

/// <summary>A configured printer: how to reach it, and what it can do.</summary>
/// <remarks>See Docs/03-design/06-printer-abstraction.md §8.</remarks>
public sealed record PrinterProfile(
    string Id,
    string BluetoothAddress,
    string DisplayName,
    TransportType TransportType,
    PrinterLanguage Language,
    int PrintWidthDots,
    int Dpi,
    MediaType MediaType,
    bool HasCutter,
    bool SupportsStatusQuery)
{
    /// <summary>
    /// Common printable widths. Printable width is always narrower than media width — getting this
    /// wrong is the most common cause of clipped labels (DES-06 §8.1).
    /// </summary>
    public static class Widths
    {
        public const int Receipt58mmAt203Dpi = 384;
        public const int Receipt80mmAt203Dpi = 576;
        public const int Label4InchAt203Dpi = 832;
    }
}

/// <summary>What a printer reported about itself. Absent fields mean "this printer cannot say".</summary>
public sealed record PrinterStatus(
    bool IsReady,
    bool? OutOfPaper = null,
    bool? CoverOpen = null,
    int? BatteryPercent = null)
{
    public static PrinterStatus Unknown => new(IsReady: true);
}

/// <summary>Live connection state of a transport.</summary>
public abstract record ConnectionState
{
    public sealed record Disconnected : ConnectionState;

    public sealed record Connecting : ConnectionState;

    public sealed record Connected(string DeviceName, int? Mtu) : ConnectionState;

    public sealed record Failed(PrinterError Error) : ConnectionState;
}
