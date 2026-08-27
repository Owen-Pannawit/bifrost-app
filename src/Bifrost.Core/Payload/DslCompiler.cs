using Bifrost.Core.Model;

namespace Bifrost.Core.Payload;

/// <summary>
/// Compiles a Tier 2 payload into the <see cref="PrintDocument"/> IR, validating as it goes.
/// </summary>
/// <remarks>
/// <para>
/// Validation happens here rather than in the driver, so a malformed payload is rejected before a
/// printer connection is even needed, and the error names the offending field (FR-308).
/// </para>
/// <para>
/// Symbology constraints are enforced at submit time on purpose: a barcode whose data is invalid
/// for its symbology prints as garbage, and the defect is discovered weeks later when the label is
/// picked. Catching it here turns a warehouse problem into a 400 response (DES-05 §4.2).
/// </para>
/// </remarks>
public sealed class DslCompiler(int defaultWidthDots, MediaType defaultMediaType = MediaType.Continuous)
{
    private const int MaxCopies = 99;

    public Result<PrintDocument> Compile(PrintRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Tier, "dsl", StringComparison.OrdinalIgnoreCase))
        {
            return new PrinterError.ValidationError(
                "tier",
                $"Only the 'dsl' tier is available in this build; got '{request.Tier}'.");
        }

        if (request.Document is null)
        {
            return new PrinterError.ValidationError("document", "document is required.");
        }

        if (request.Document.Elements.Count == 0)
        {
            return new PrinterError.ValidationError("document.elements", "At least one element is required.");
        }

        var width = request.Document.WidthDots ?? defaultWidthDots;
        if (width > defaultWidthDots)
        {
            return new PrinterError.ContentTooWide(width, defaultWidthDots);
        }

        var copies = request.Options?.Copies ?? 1;
        if (copies is < 1 or > MaxCopies)
        {
            return new PrinterError.ValidationError(
                "options.copies", $"copies must be between 1 and {MaxCopies}.");
        }

        var blocks = new List<PrintBlock>(request.Document.Elements.Count);

        for (var i = 0; i < request.Document.Elements.Count; i++)
        {
            var compiled = CompileElement(request.Document.Elements[i], $"document.elements[{i}]");
            if (compiled.IsFailure) return compiled.Error;
            blocks.Add(compiled.Value);
        }

        return new PrintDocument(
            width,
            defaultMediaType,
            blocks,
            copies,
            request.Options?.CutAfter ?? false);
    }

    private static Result<PrintBlock> CompileElement(ElementDto e, string path) =>
        e.Type?.ToLowerInvariant() switch
        {
            "text" => CompileText(e, path),
            "barcode" => CompileBarcode(e, path),
            "qr" => CompileQr(e, path),
            "feed" => CompileFeed(e, path),
            "cut" => Result<PrintBlock>.Ok(new PrintBlock.Cut(
                string.Equals(e.Mode, "PARTIAL", StringComparison.OrdinalIgnoreCase)
                    ? CutMode.Partial
                    : CutMode.Full)),
            null or "" => new PrinterError.ValidationError($"{path}.type", "type is required."),
            _ => new PrinterError.UnsupportedElement(e.Type),
        };

    private static Result<PrintBlock> CompileText(ElementDto e, string path)
    {
        if (string.IsNullOrEmpty(e.Value))
        {
            return new PrinterError.ValidationError($"{path}.value", "value is required for a text element.");
        }

        var size = e.Size ?? 1;
        if (size is < 1 or > 8)
        {
            return new PrinterError.ValidationError($"{path}.size", "size must be between 1 and 8.");
        }

        var align = ParseAlign(e.Align);
        if (align is null)
        {
            return new PrinterError.ValidationError($"{path}.align", "align must be left, center or right.");
        }

        // Drivers encode with Encoding.ASCII, so anything outside it silently becomes "?" on the
        // paper. D-09 fixes the content as English and numeric, so rejecting is honest: a caller
        // sending accented or Thai text has a real problem, and discovering it as a row of question
        // marks on a label weeks later is the worst possible time.
        if (e.Value.Any(c => c > 127))
        {
            var offending = new string(e.Value.Where(c => c > 127).Distinct().Take(5).ToArray());
            return new PrinterError.ValidationError(
                $"{path}.value",
                $"Text must be ASCII; this printer cannot render '{offending}'. " +
                "Non-Latin scripts need bitmap rendering, which this build does not support.");
        }

        return Result<PrintBlock>.Ok(new PrintBlock.Text(
            e.Value, size, e.Bold ?? false, e.Underline ?? false,
            Invert: false, FontId: null, align.Value));
    }

    private static Result<PrintBlock> CompileBarcode(ElementDto e, string path)
    {
        if (string.IsNullOrEmpty(e.Value))
        {
            return new PrinterError.ValidationError($"{path}.value", "value is required for a barcode.");
        }

        var symbology = ParseSymbology(e.Format);
        if (symbology is null)
        {
            return new PrinterError.ValidationError(
                $"{path}.format", "format must be CODE128, CODE39, EAN13, ITF or UPCA.");
        }

        var check = ValidateSymbologyData(symbology.Value, e.Value, path);
        if (check.IsFailure) return check.Error;

        var align = ParseAlign(e.Align) ?? Alignment.Center;
        var moduleWidth = e.ModuleWidth ?? 3;
        if (moduleWidth is < 1 or > 6)
        {
            return new PrinterError.ValidationError(
                $"{path}.moduleWidth", "moduleWidth must be between 1 and 6.");
        }

        return Result<PrintBlock>.Ok(new PrintBlock.Barcode(
            symbology.Value, e.Value, e.HeightDots ?? 80, moduleWidth, e.ShowText ?? true, align));
    }

    private static Result<PrintBlock> CompileQr(ElementDto e, string path)
    {
        if (string.IsNullOrEmpty(e.Value))
        {
            return new PrinterError.ValidationError($"{path}.value", "value is required for a qr element.");
        }

        // The QR spec's ceiling for byte mode. Beyond it the symbol cannot be built at all, so
        // catching it here beats a printer that silently prints nothing.
        if (System.Text.Encoding.UTF8.GetByteCount(e.Value) > 2953)
        {
            return new PrinterError.ValidationError($"{path}.value", "QR data must not exceed 2953 bytes.");
        }

        var scale = e.Scale ?? 5;
        if (scale is < 1 or > 16)
        {
            return new PrinterError.ValidationError($"{path}.scale", "scale must be between 1 and 16.");
        }

        var ecc = ParseEcc(e.ErrorCorrection);
        if (ecc is null)
        {
            return new PrinterError.ValidationError(
                $"{path}.errorCorrection", "errorCorrection must be L, M, Q or H.");
        }

        return Result<PrintBlock>.Ok(new PrintBlock.QrCode(
            e.Value, scale, ecc.Value, ParseAlign(e.Align) ?? Alignment.Center));
    }

    private static EccLevel? ParseEcc(string? value) => value?.ToUpperInvariant() switch
    {
        null or "" => EccLevel.Q,
        "L" => EccLevel.L,
        "M" => EccLevel.M,
        "Q" => EccLevel.Q,
        "H" => EccLevel.H,
        _ => null,
    };

    private static Result<PrintBlock> CompileFeed(ElementDto e, string path)
    {
        if (e.Lines is null && e.Dots is null)
        {
            return new PrinterError.ValidationError($"{path}", "feed requires either lines or dots.");
        }

        if (e.Lines is not null && e.Dots is not null)
        {
            return new PrinterError.ValidationError($"{path}", "feed takes lines or dots, not both.");
        }

        // One text line at the base font is roughly 24 dots at 203 dpi.
        var dots = e.Dots ?? (e.Lines!.Value * 24);
        return dots < 0
            ? new PrinterError.ValidationError($"{path}", "feed must not be negative.")
            : Result<PrintBlock>.Ok(new PrintBlock.Feed(dots));
    }

    /// <summary>Character-set and length rules per symbology — DES-05 §4.2.</summary>
    private static Result ValidateSymbologyData(Symbology symbology, string value, string path)
    {
        static Result Bad(string path, string why) =>
            Result.Fail(new PrinterError.ValidationError($"{path}.value", why));

        switch (symbology)
        {
            case Symbology.Code128:
                if (value.Length is < 1 or > 48) return Bad(path, "CODE128 accepts 1–48 characters.");
                if (value.Any(c => c > 127)) return Bad(path, "CODE128 accepts ASCII only.");
                break;

            case Symbology.Code39:
                const string code39 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";
                if (value.Length is < 1 or > 43) return Bad(path, "CODE39 accepts 1–43 characters.");
                if (value.Any(c => !code39.Contains(c, StringComparison.Ordinal)))
                {
                    return Bad(path, "CODE39 accepts 0-9, A-Z and - . $ / + % and space only.");
                }

                break;

            case Symbology.Ean13:
                if (value.Length is not (12 or 13) || !value.All(char.IsAsciiDigit))
                {
                    return Bad(path, "EAN13 requires 12 or 13 digits.");
                }

                break;

            case Symbology.Itf:
                if (!value.All(char.IsAsciiDigit)) return Bad(path, "ITF accepts digits only.");
                if (value.Length % 2 != 0) return Bad(path, "ITF requires an even number of digits.");
                if (value.Length is < 2 or > 30) return Bad(path, "ITF accepts 2–30 digits.");
                break;

            case Symbology.UpcA:
                if (value.Length is not (11 or 12) || !value.All(char.IsAsciiDigit))
                {
                    return Bad(path, "UPCA requires 11 or 12 digits.");
                }

                break;
        }

        return Result.Ok();
    }

    private static Alignment? ParseAlign(string? value) => value?.ToLowerInvariant() switch
    {
        null or "" or "left" => Alignment.Left,
        "center" or "centre" => Alignment.Center,
        "right" => Alignment.Right,
        _ => null,
    };

    private static Symbology? ParseSymbology(string? value) => value?.ToUpperInvariant() switch
    {
        "CODE128" => Symbology.Code128,
        "CODE39" => Symbology.Code39,
        "EAN13" => Symbology.Ean13,
        "ITF" => Symbology.Itf,
        "UPCA" => Symbology.UpcA,
        _ => null,
    };
}
