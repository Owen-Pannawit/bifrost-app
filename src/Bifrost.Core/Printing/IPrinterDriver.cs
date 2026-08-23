using Bifrost.Core.Model;

namespace Bifrost.Core.Printing;

/// <summary>
/// Serialises a rendered document into one printer command language.
/// </summary>
/// <remarks>
/// <b>Drivers never touch Bluetooth.</b> Transports never interpret command bytes. Any code that
/// violates that separation is a defect, because it is what would reintroduce vendor coupling
/// (ADR-007).
///
/// Adding a language costs one implementation of this interface, with no change to the API, queue
/// or rendering layers (FR-610).
/// </remarks>
public interface IPrinterDriver
{
    PrinterLanguage Language { get; }

    DriverCapabilities Capabilities { get; }

    /// <summary>Serialise a document into this language's command bytes.</summary>
    byte[] Serialise(PrintDocument document, PrinterProfile printer);

    /// <summary>
    /// Bytes that ask the printer for status, or <c>null</c> if this printer cannot be asked.
    /// </summary>
    /// <remarks>
    /// Nullable deliberately: with Nullable enabled and warnings as errors, a caller that forgets
    /// a printer might not answer gets a build failure rather than a false "ready" (FR-608).
    /// Cheap ESC/POS clones frequently return null here.
    /// </remarks>
    byte[]? StatusQuery();

    /// <summary>Interpret a status response. Never called when <see cref="StatusQuery"/> is null.</summary>
    PrinterStatus ParseStatus(ReadOnlySpan<byte> response);

    /// <summary>Identify this language from a printer's identification response, if possible.</summary>
    bool Matches(ReadOnlySpan<byte> identificationResponse);
}

/// <summary>
/// What a command language can express. Capability differences are data, not conditionals in
/// calling code (ADR-007 invariant 4).
/// </summary>
public sealed record DriverCapabilities(
    IReadOnlySet<Symbology> SupportedSymbologies,
    bool SupportsQr,
    bool SupportsImages,
    bool SupportsCut,
    bool SupportsStatusQuery,
    bool SupportsInvert,
    int MaxTextSizeMultiplier,
    PositioningModel PositioningModel);

/// <summary>
/// The one structural difference between the language families, and the reason the IR models
/// intent rather than coordinates (DES-05 §6.1).
/// </summary>
public enum PositioningModel
{
    /// <summary>ESC/POS — blocks stream in order down a continuous roll.</summary>
    Sequential,

    /// <summary>ZPL, CPCL, TSPL — blocks are placed at computed coordinates on a label canvas.</summary>
    Absolute,
}
