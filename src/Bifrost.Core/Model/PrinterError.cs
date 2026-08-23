namespace Bifrost.Core.Model;

/// <summary>
/// Every failure in the system. Each error declares whether retrying could help.
/// </summary>
/// <remarks>
/// Putting <see cref="Transient"/> on the error type means RetryPolicy is a single switch over a
/// closed hierarchy, and adding an error without deciding its retry disposition is caught at
/// compile time (FR-107).
///
/// Putting <see cref="OperatorMessage"/> here means the API, the app UI and the SDK all surface
/// the same words for the same fault — one vocabulary, not three (NFR-501).
///
/// See Docs/04-implementation/03-coding-standards.md §2.2.
/// </remarks>
public abstract record PrinterError(string Code, string OperatorMessage, bool Transient)
{
    // ---- Printer conditions the operator can fix (transient) ----

    public sealed record OutOfPaper() : PrinterError(
        "PRINTER_OUT_OF_PAPER",
        "Printer is out of paper. Load media and printing will resume automatically.",
        Transient: true);

    public sealed record CoverOpen() : PrinterError(
        "PRINTER_COVER_OPEN",
        "Printer cover is open. Close the cover.",
        Transient: true);

    public sealed record BatteryLow() : PrinterError(
        "PRINTER_BATTERY_LOW",
        "Printer battery is low. Charge or swap the battery.",
        Transient: true);

    public sealed record Overheated() : PrinterError(
        "PRINTER_OVERHEATED",
        "Printer is too hot. Wait about a minute for it to cool.",
        Transient: true);

    public sealed record PaperJam() : PrinterError(
        "PRINTER_PAPER_JAM",
        "Paper jam. Open the cover and clear the jam.",
        Transient: true);

    // ---- Connection and transport (transient) ----

    public sealed record Disconnected() : PrinterError(
        "PRINTER_DISCONNECTED",
        "Printer not connected. Switch it on and keep it within a few metres.",
        Transient: true);

    public sealed record NotConnected() : PrinterError(
        "PRINTER_NOT_CONNECTED",
        "No printer selected. Choose your printer in Printer setup.",
        Transient: true);

    public sealed record ConnectionFailed(string Detail) : PrinterError(
        "PRINTER_CONNECTION_FAILED",
        "Could not connect to the printer. Check it is switched on and in range.",
        Transient: true);

    public sealed record TransmitTimeout() : PrinterError(
        "TRANSMIT_TIMEOUT",
        "Printer stopped responding. Retrying automatically.",
        Transient: true);

    public sealed record InternalError(string Detail) : PrinterError(
        "INTERNAL_ERROR",
        "Something went wrong. Export diagnostics from Settings and send it to IT.",
        Transient: true);

    // ---- Payload and capability faults (permanent — retrying cannot help) ----

    public sealed record ValidationError(string Field, string Detail) : PrinterError(
        "VALIDATION_ERROR",
        "This print request is not valid.",
        Transient: false);

    public sealed record ContentTooWide(int RequiredDots, int MaxDots) : PrinterError(
        "CONTENT_TOO_WIDE",
        "This label is too wide for the printer. Call IT — the label design needs changing.",
        Transient: false);

    public sealed record UnsupportedElement(string ElementType) : PrinterError(
        "UNSUPPORTED_ELEMENT",
        "This printer does not support part of the label.",
        Transient: false);

    public sealed record UnsupportedCommand() : PrinterError(
        "PRINTER_UNSUPPORTED_COMMAND",
        "This printer does not support a feature the label needs.",
        Transient: false);
}
