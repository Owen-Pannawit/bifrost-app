using System.Text.Json.Serialization;

namespace Bifrost.Core.Payload;

/// <summary>
/// The wire shape of a print request. Tier 2 (Layout DSL) only for the demo.
/// </summary>
/// <remarks>
/// Tier 1 (templates) and Tier 3 (raw) are specified in DES-05 and deferred — see the demo plan's
/// "What is deliberately not built". The discriminator is kept so adding them later is additive
/// rather than a breaking change.
/// </remarks>
public sealed class PrintRequestDto
{
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "dsl";

    [JsonPropertyName("document")]
    public DocumentDto? Document { get; set; }

    [JsonPropertyName("options")]
    public OptionsDto? Options { get; set; }
}

public sealed class DocumentDto
{
    /// <summary>Optional. The connected printer's width is used when omitted.</summary>
    [JsonPropertyName("widthDots")]
    public int? WidthDots { get; set; }

    [JsonPropertyName("elements")]
    public List<ElementDto> Elements { get; set; } = [];
}

/// <summary>
/// One element. A flat shape with a <c>type</c> discriminator rather than polymorphic
/// deserialisation: it keeps System.Text.Json source generation straightforward and produces
/// better error messages, which is what FR-308 is actually about.
/// </summary>
public sealed class ElementDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    // text
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("size")]
    public int? Size { get; set; }

    [JsonPropertyName("bold")]
    public bool? Bold { get; set; }

    [JsonPropertyName("underline")]
    public bool? Underline { get; set; }

    [JsonPropertyName("align")]
    public string? Align { get; set; }

    // barcode
    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("heightDots")]
    public int? HeightDots { get; set; }

    [JsonPropertyName("moduleWidth")]
    public int? ModuleWidth { get; set; }

    [JsonPropertyName("showText")]
    public bool? ShowText { get; set; }

    // qr
    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

    [JsonPropertyName("errorCorrection")]
    public string? ErrorCorrection { get; set; }

    // feed
    [JsonPropertyName("lines")]
    public int? Lines { get; set; }

    [JsonPropertyName("dots")]
    public int? Dots { get; set; }

    // cut
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}

public sealed class OptionsDto
{
    [JsonPropertyName("copies")]
    public int? Copies { get; set; }

    [JsonPropertyName("cutAfter")]
    public bool? CutAfter { get; set; }
}
