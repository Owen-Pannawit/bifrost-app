using System.Text.Json;
using System.Text.Json.Serialization;
using Bifrost.Core.Model;
using Bifrost.Core.Payload;
using Bifrost.Core.Printing;

namespace Bifrost.Server;

/// <summary>
/// Registers the API routes onto any <see cref="IBridgeServer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Contains no EmbedIO type — that is the point of ADR-009. Swapping the server library means one
/// new adapter, not touching this file.
/// </para>
/// <para>
/// <b>Demo scope.</b> Two endpoints of the ten in DES-03. No pairing, no jobs list, no WebSocket.
/// </para>
/// </remarks>
public sealed class BridgeApi(PrintService printService, string appVersion)
{
    // One serialiser for the whole wire surface. Two configurations is how half an API ends up
    // PascalCase — see BridgeResponse.Error.
    private static readonly JsonSerializerOptions Json = BridgeResponse.WireJson;

    public void MapRoutes(IBridgeServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        server.MapGet("/v1/status", (_, _) => Task.FromResult(Status()));
        server.MapPost("/v1/print", PrintAsync);
    }

    /// <summary>
    /// Bridge, printer and queue state. Unauthenticated by design so a page can detect the bridge
    /// before pairing (FR-204).
    /// </summary>
    private BridgeResponse Status()
    {
        var state = printService.ConnectionState.Current;

        var printer = state switch
        {
            Core.Model.ConnectionState.Connected c => new PrinterStatusDto(
                "READY", c.DeviceName, printService.Profile.TransportType.ToString(),
                printService.Driver.Language.ToString(), printService.Profile.PrintWidthDots),

            Core.Model.ConnectionState.Connecting => new PrinterStatusDto("CONNECTING"),
            Core.Model.ConnectionState.Failed f => new PrinterStatusDto("ERROR", LastError: f.Error.Code),
            _ => new PrinterStatusDto("DISCONNECTED"),
        };

        return BridgeResponse.Ok(JsonSerializer.Serialize(
            new StatusDto(new BridgeDto(appVersion, "v1", Paired: true), printer), Json));
    }

    private async Task<BridgeResponse> PrintAsync(BridgeRequest request, CancellationToken ct)
    {
        PrintRequestDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PrintRequestDto>(request.Body, Json);
        }
        catch (JsonException ex)
        {
            return BridgeResponse.Error(400, "MALFORMED_JSON", ex.Message, transient: false);
        }

        if (dto is null)
        {
            return BridgeResponse.Error(400, "MALFORMED_JSON", "Request body was empty.", transient: false);
        }

        var result = await printService.PrintAsync(dto, ct).ConfigureAwait(false);

        if (result.IsFailure) return FromError(result.Error);

        var job = result.Value;
        return BridgeResponse.Accepted(JsonSerializer.Serialize(
            new JobDto(job.JobId, job.State.ToString().ToUpperInvariant(), job.ByteCount), Json));
    }

    /// <summary>
    /// Maps a domain error to an HTTP status. One place, so a new error cannot slip out with a
    /// misleading status (DES-03 §4).
    /// </summary>
    private static BridgeResponse FromError(PrinterError error)
    {
        var status = error switch
        {
            PrinterError.ValidationError => 400,
            PrinterError.ContentTooWide => 422,
            PrinterError.UnsupportedElement => 422,
            PrinterError.NotConnected => 409,
            PrinterError.Disconnected => 409,
            PrinterError.OutOfPaper => 409,
            PrinterError.CoverOpen => 409,
            PrinterError.PaperJam => 409,
            PrinterError.BatteryLow => 409,
            PrinterError.Overheated => 409,
            PrinterError.TransmitTimeout => 504,
            _ => 500,
        };

        if (error is not PrinterError.ValidationError v)
        {
            return BridgeResponse.Error(status, error.Code, error.OperatorMessage, error.Transient);
        }

        // A validation error is a defect in the calling page, never something an operator can act
        // on — DES-09's operator message catalogue does not even list it. So the specific reason
        // is the message, and the generic operator wording is dropped. Returning "This print
        // request is not valid" with no detail would satisfy the letter of FR-308 and none of its
        // purpose.
        return BridgeResponse.Json(status, JsonSerializer.Serialize(
            new { error = new { code = v.Code, message = v.Detail, transient = v.Transient, field = v.Field } },
            Json));
    }

    private sealed record StatusDto(BridgeDto Bridge, PrinterStatusDto Printer);

    private sealed record BridgeDto(string Version, string ApiVersion, bool Paired);

    private sealed record PrinterStatusDto(
        string State,
        string? Name = null,
        string? Transport = null,
        string? Language = null,
        int? PrintWidthDots = null,
        string? LastError = null);

    private sealed record JobDto(string JobId, string State, int ByteCount);
}
