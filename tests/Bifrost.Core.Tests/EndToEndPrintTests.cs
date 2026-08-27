using System.Text;
using System.Text.Json;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Core.Testing;
using Bifrost.Drivers.EscPos;
using Bifrost.Server;

namespace Bifrost.Core.Tests;

/// <summary>
/// The whole path: HTTP request → CORS → route → DSL compile → IR → driver → transport bytes.
/// </summary>
/// <remarks>
/// No socket, no Android, no printer. When a real printer is attached, only the last component
/// changes — <see cref="MockTransport"/> becomes <c>SppTransport</c> and nothing above it moves.
/// That is the whole argument for the abstractions in ADR-007 and ADR-009.
/// </remarks>
public sealed class EndToEndPrintTests
{
    private const string AllowedOrigin = "http://intranet.company.local";

    private static readonly PrinterProfile Profile = new(
        Id: "demo",
        BluetoothAddress: "00:11:22:33:44:55",
        DisplayName: "Demo 80mm",
        TransportType: TransportType.Mock,
        Language: PrinterLanguage.EscPos,
        PrintWidthDots: PrinterProfile.Widths.Receipt80mmAt203Dpi,
        Dpi: 203,
        MediaType: MediaType.Continuous,
        HasCutter: true,
        SupportsStatusQuery: true);

    private static async Task<(TestBridgeServer Server, MockTransport Transport)> BuildAsync(
        MockScenario? scenario = null, bool connect = true)
    {
        var transport = new MockTransport(scenario);
        var service = new PrintService(transport, new EscPosDriver(), Profile);

        if (connect) await service.ConnectAsync(Profile.BluetoothAddress, CancellationToken.None);

        var server = new TestBridgeServer();
        server.UseInterceptor(new CorsInterceptor([AllowedOrigin]));
        new BridgeApi(service, "0.1.0").MapRoutes(server);

        return (server, transport);
    }

    private static readonly IReadOnlyDictionary<string, string> FromAllowedOrigin =
        new Dictionary<string, string> { ["Origin"] = AllowedOrigin };

    private const string PartLabel = """
    {
      "tier": "dsl",
      "document": {
        "elements": [
          { "type": "text",    "value": "6205-2RS", "size": 3, "bold": true, "align": "center" },
          { "type": "text",    "value": "Lot L2408-0231", "size": 1, "align": "center" },
          { "type": "barcode", "format": "CODE128", "value": "6205-2RS", "heightDots": 80, "moduleWidth": 3 },
          { "type": "feed",    "lines": 3 }
        ]
      }
    }
    """;

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task A_web_request_produces_printer_bytes()
    {
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);

        Assert.Equal(202, response.StatusCode);

        var bytes = transport.AllBytes;
        Assert.NotEmpty(bytes);

        var text = Encoding.ASCII.GetString(bytes);
        Assert.Contains("6205-2RS", text, StringComparison.Ordinal);
        Assert.Contains("Lot L2408-0231", text, StringComparison.Ordinal);

        // ESC @ initialise, and GS k 73 — the native CODE128 command, not a raster (FR-311).
        Assert.Equal(0x1B, bytes[0]);
        Assert.Contains("k", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_job_response_carries_an_id_and_a_terminal_state()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);
        var job = JsonDocument.Parse(response.Body).RootElement;

        Assert.StartsWith("job_", job.GetProperty("jobId").GetString(), StringComparison.Ordinal);
        Assert.Equal("PRINTED", job.GetProperty("state").GetString());
        Assert.True(job.GetProperty("byteCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Status_reports_the_connected_printer()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("GET", "/v1/status", headers: FromAllowedOrigin);
        var root = JsonDocument.Parse(response.Body).RootElement;

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("READY", root.GetProperty("printer").GetProperty("state").GetString());
        Assert.Equal("EscPos", root.GetProperty("printer").GetProperty("language").GetString());
        Assert.Equal(576, root.GetProperty("printer").GetProperty("printWidthDots").GetInt32());
    }

    [Fact]
    public async Task Status_works_before_a_printer_is_connected()
    {
        // FR-204: a page must be able to detect the bridge without a printer being ready.
        var (server, _) = await BuildAsync(connect: false);

        var response = await server.SendAsync("GET", "/v1/status", headers: FromAllowedOrigin);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("DISCONNECTED",
            JsonDocument.Parse(response.Body).RootElement
                .GetProperty("printer").GetProperty("state").GetString());
    }

    // ------------------------------------------------------------------ printer conditions

    [Fact]
    public async Task Out_of_paper_is_reported_with_its_own_code_and_an_actionable_message()
    {
        var (server, _) = await BuildAsync(new MockScenario.OutOfPaper());

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);
        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("PRINTER_OUT_OF_PAPER", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("transient").GetBoolean());

        // NFR-501: the message tells the operator what to do, without an error code in it.
        Assert.Contains("Load media", error.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Printing_with_no_printer_connected_fails_without_reaching_the_transport()
    {
        var (server, transport) = await BuildAsync(connect: false);

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);

        Assert.Equal(409, response.StatusCode);
        Assert.Empty(transport.Written);
    }

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("""{"tier":"dsl","document":{"elements":[{"type":"barcode","format":"CODE39","value":"lowercase"}]}}""",
        "CODE39")]
    [InlineData("""{"tier":"dsl","document":{"elements":[{"type":"barcode","format":"ITF","value":"123"}]}}""",
        "even number")]
    [InlineData("""{"tier":"dsl","document":{"elements":[{"type":"barcode","format":"EAN13","value":"12345"}]}}""",
        "12 or 13")]
    public async Task Unscannable_barcode_data_is_rejected_at_submit_time(string body, string expectedHint)
    {
        // Catching this here turns a warehouse problem — a label that prints and will not scan —
        // into a 400 the developer sees immediately (DES-05 §4.2).
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", body, FromAllowedOrigin);
        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.Contains(expectedHint, error.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Written);
    }

    [Fact]
    public async Task A_validation_error_names_the_offending_field()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print",
            """{"tier":"dsl","document":{"elements":[{"type":"text","value":"x","size":99}]}}""",
            FromAllowedOrigin);

        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");

        // FR-308: a field path, not just "invalid request".
        Assert.Equal("document.elements[0].size", error.GetProperty("field").GetString());
    }

    [Fact]
    public async Task An_unknown_element_type_is_permanent_not_transient()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print",
            """{"tier":"dsl","document":{"elements":[{"type":"hologram","value":"x"}]}}""",
            FromAllowedOrigin);

        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("UNSUPPORTED_ELEMENT", error.GetProperty("code").GetString());

        // FR-107: retrying an unsupported element can never succeed.
        Assert.False(error.GetProperty("transient").GetBoolean());
    }

    [Fact]
    public async Task Content_wider_than_the_printer_is_refused()
    {
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print",
            """{"tier":"dsl","document":{"widthDots":9999,"elements":[{"type":"text","value":"x"}]}}""",
            FromAllowedOrigin);

        Assert.Equal(422, response.StatusCode);
        Assert.Equal("CONTENT_TOO_WIDE",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(transport.Written);
    }

    [Fact]
    public async Task Malformed_json_does_not_crash_the_bridge()
    {
        // NFR-205: a hostile payload must not leave the app unusable.
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", "{ this is not json", FromAllowedOrigin);
        Assert.Equal(400, response.StatusCode);

        // Still serving afterwards.
        var after = await server.SendAsync("GET", "/v1/status", headers: FromAllowedOrigin);
        Assert.Equal(200, after.StatusCode);
    }

    // ------------------------------------------------------------------ origin control

    [Fact]
    public async Task A_page_from_an_unlisted_origin_cannot_print()
    {
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel,
            new Dictionary<string, string> { ["Origin"] = "http://evil.local" });

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString());

        // The asset protected is the printer: nothing reached it.
        Assert.Empty(transport.Written);
    }

    [Fact]
    public async Task Preflight_from_an_unlisted_origin_gets_no_permissive_headers()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/print", headers:
            new Dictionary<string, string> { ["Origin"] = "http://evil.local" });

        Assert.Equal(403, response.StatusCode);
        Assert.True(response.Headers is null
            || !response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Preflight_from_an_allowlisted_origin_varies_on_origin()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/print", headers: FromAllowedOrigin);

        Assert.Equal(204, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers!["Access-Control-Allow-Origin"]);

        // Without Vary, a cache could serve this to a different origin.
        Assert.Equal("Origin", response.Headers["Vary"]);
    }

    [Fact]
    public async Task A_successful_print_carries_the_header_the_browser_needs_to_read_it()
    {
        // REGRESSION. Decorate was never wired up, so 202 came back with no
        // Access-Control-Allow-Origin. The server printed and the browser discarded the reply —
        // the page reported failure for a label that had already come out. The operator then
        // prints again and gets a duplicate, which is the outcome NFR-202 exists to prevent,
        // arriving through a door nobody was watching.
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);

        Assert.Equal(202, response.StatusCode);
        Assert.NotEmpty(transport.Written);
        Assert.Equal(AllowedOrigin, response.Headers?["Access-Control-Allow-Origin"]);
        Assert.Equal("Origin", response.Headers?["Vary"]);
    }

    [Fact]
    public async Task Status_responses_are_decorated_too()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("GET", "/v1/status", headers: FromAllowedOrigin);

        Assert.Equal(AllowedOrigin, response.Headers?["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task A_refused_origin_gets_no_headers_even_on_the_error_response()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel,
            new Dictionary<string, string> { ["Origin"] = "http://evil.local" });

        Assert.Equal(403, response.StatusCode);
        Assert.True(response.Headers is null
            || !response.Headers.ContainsKey("Access-Control-Allow-Origin"));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8437")]   // a page served by the bridge itself
    [InlineData("http://localhost:3000")]   // a dev server reached over adb reverse
    [InlineData("http://localhost")]
    [InlineData("http://[::1]:5500")]
    public async Task Any_loopback_origin_can_print_whatever_its_port(string origin)
    {
        // REGRESSION. Origins compare exactly, including the port, and the allowlist held only
        // bare hosts — so every practical way of serving a test page was refused, including a
        // page served by the bridge's own port. Web printing could not be tested at all.
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel,
            new Dictionary<string, string> { ["Origin"] = origin });

        Assert.Equal(202, response.StatusCode);
        Assert.NotEmpty(transport.Written);
        Assert.Equal(origin, response.Headers?["Access-Control-Allow-Origin"]);
    }

    [Theory]
    [InlineData("http://evil.local")]
    [InlineData("http://192.168.1.50:3000")]     // LAN is not loopback
    [InlineData("https://localhost.evil.com")]   // suffix trickery
    [InlineData("http://notlocalhost")]
    public async Task Non_loopback_origins_are_still_refused(string origin)
    {
        // The loopback exemption must not become a general opening. A remote page is the threat
        // the allowlist exists for (T-1).
        var (server, transport) = await BuildAsync();

        var response = await server.SendAsync("POST", "/v1/print", PartLabel,
            new Dictionary<string, string> { ["Origin"] = origin });

        Assert.Equal(403, response.StatusCode);
        Assert.Empty(transport.Written);
    }

    [Fact]
    public async Task A_preflight_is_answered_even_though_OPTIONS_matches_no_route()
    {
        // REGRESSION, and the one that actually broke printing from a browser.
        //
        // The EmbedIO adapter registered a module per route, so OPTIONS — a verb no route
        // declares — was 404'd by the framework before any interceptor ran. Chrome read the
        // failed preflight and never sent the real request.
        //
        // Private Network Access makes that fatal rather than cosmetic: Chrome preflights even a
        // simple GET when the target is loopback, so every endpoint was unreachable from a page,
        // not just the ones taking a body.
        //
        // TestBridgeServer always routed after intercepting, which is precisely why it passed
        // while the real server failed. Both must intercept first; this asserts the contract.
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/print", headers: FromAllowedOrigin);

        Assert.Equal(204, response.StatusCode);
        Assert.Equal(AllowedOrigin, response.Headers?["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task A_preflight_for_an_unknown_path_is_still_intercepted_not_routed()
    {
        // Interception happens before routing, so an unknown path must not shortcut the CORS
        // decision. Anything else and the framework's 404 wins the race again.
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/anything", headers: FromAllowedOrigin);

        Assert.Equal(204, response.StatusCode);
    }

    [Fact]
    public async Task A_private_network_preflight_gets_explicit_consent()
    {
        // Chrome asks before reaching a loopback address from a page and discards the response
        // unless the server consents by name.
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/print", headers: new Dictionary<string, string>
        {
            ["Origin"] = AllowedOrigin,
            ["Access-Control-Request-Private-Network"] = "true",
        });

        Assert.Equal(204, response.StatusCode);
        Assert.Equal("true", response.Headers?["Access-Control-Allow-Private-Network"]);
    }

    [Fact]
    public async Task Consent_to_private_network_access_is_given_only_when_asked_for()
    {
        // It is consent to a specific request, not a standing advertisement.
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("OPTIONS", "/v1/print", headers: FromAllowedOrigin);

        Assert.True(response.Headers is null
            || !response.Headers.ContainsKey("Access-Control-Allow-Private-Network"));
    }

    [Fact]
    public async Task An_unknown_path_still_returns_a_structured_error()
    {
        var (server, _) = await BuildAsync();

        var response = await server.SendAsync("GET", "/v1/nope", headers: FromAllowedOrigin);

        Assert.Equal(404, response.StatusCode);
        Assert.Equal("NOT_FOUND",
            JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_interceptor_covers_every_registered_route()
    {
        // A new endpoint must not be able to slip past the origin check by existing.
        var (server, _) = await BuildAsync();

        foreach (var (method, route) in server.Routes)
        {
            var response = await server.SendAsync(method, route, method == "POST" ? PartLabel : null,
                new Dictionary<string, string> { ["Origin"] = "http://evil.local" });

            Assert.Equal(403, response.StatusCode);
        }
    }

    // ------------------------------------------------------------------ transport failure modes

    [Fact]
    public async Task A_silently_truncated_write_still_reports_success_today()
    {
        // Documents the gap the demo knowingly ships with. TruncateAt reproduces the BLE
        // flow-control failure: the printer accepts part of the payload and nothing looks wrong
        // until the label is read (DES-06 §7.3).
        //
        // Detecting it needs the acknowledgement discipline in the BLE transport, which is Day 5
        // of the plan. This test exists so the gap is visible rather than assumed away, and it is
        // the test that must flip when BleTransport lands.
        var (server, transport) = await BuildAsync(new MockScenario.TruncateAt(20));

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);

        Assert.Equal(202, response.StatusCode);
        Assert.Equal(20, transport.AllBytes.Length);
    }

    [Fact]
    public async Task A_dropped_connection_mid_write_is_reported_as_transient()
    {
        var (server, _) = await BuildAsync(new MockScenario.DisconnectAfter(10));

        var response = await server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin);
        var error = JsonDocument.Parse(response.Body).RootElement.GetProperty("error");

        Assert.Equal(409, response.StatusCode);
        Assert.True(error.GetProperty("transient").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_requests_do_not_interleave_on_the_wire()
    {
        // One consumer at a time. Two half-labels printed on top of each other cannot be undone by
        // any retry (ADR-005).
        var (server, transport) = await BuildAsync(new MockScenario.SlowWrite(100_000));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            server.SendAsync("POST", "/v1/print", PartLabel, FromAllowedOrigin)));

        Assert.Equal(8, transport.Written.Count);

        // Every write is a whole document: same length, each starting with ESC @.
        Assert.Single(transport.Written.Select(w => w.Length).Distinct());
        Assert.All(transport.Written, w => Assert.Equal(0x1B, w[0]));
    }
}
