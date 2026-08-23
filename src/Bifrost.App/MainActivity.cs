using System.Net;
using System.Text;
using Android;
using Android.Content.PM;
using Android.Widget;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Core.Testing;
using Bifrost.Drivers.Cpcl;
using Bifrost.Drivers.EscPos;
using Bifrost.Server;
using Bifrost.Server.EmbedIO;
using Bifrost.Transport;
using Bifrost.Transport.Classic;
using Bifrost.Transport.Probe;

namespace Bifrost.App;

/// <summary>
/// Day 1 spike screen, now also serving the real API.
/// </summary>
/// <remarks>
/// <para>
/// <b>Spike A</b> — which command language does each available printer speak? Both ESC/POS and
/// CPCL drivers exist, so whichever answer comes back, Day 2 is unblocked.
/// </para>
/// <para>
/// <b>Spike B</b> — does EmbedIO load and serve on net10.0-android? ASP.NET Core does not run on
/// Android at all, so the server is a third-party dependency on the critical path (R-16).
/// </para>
/// <para>
/// This activity is scaffolding: composition root, spike runner and status display in one. The
/// real screens and a proper foreground service arrive on Day 9.
/// </para>
/// </remarks>
[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    private const int BridgePort = 8437;
    private const int PermissionRequestCode = 1001;

    /// <summary>
    /// Demo allowlist. Loopback origins cover a page served from the device itself; the company
    /// intranet origin is the one that will actually be used. No wildcards, ever — DES-08 §5.
    /// </summary>
    private static readonly string[] AllowedOrigins =
    [
        "http://localhost",
        "http://127.0.0.1",
        "http://intranet.company.local",
    ];

    private TextView _output = null!;
    private TextView _serverStatus = null!;
    private Button _probeButton = null!;
    private Button _connectButton = null!;

    private IBridgeServer? _server;
    private IPrinterTransport? _transport;
    private PrintService? _printService;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _output = FindViewById<TextView>(Resource.Id.output)!;
        _serverStatus = FindViewById<TextView>(Resource.Id.serverStatus)!;
        _probeButton = FindViewById<Button>(Resource.Id.probeButton)!;
        _connectButton = FindViewById<Button>(Resource.Id.connectButton)!;

        _probeButton.Click += async (_, _) => await RunPrinterProbeAsync();
        _connectButton.Click += async (_, _) => await ConnectFirstPrinterAsync();

        RequestBluetoothPermissions();
        await StartBridgeAsync();
    }

    // ---------------------------------------------------------------- bridge

    /// <summary>
    /// Spike B plus the real API. Starts against <see cref="MockTransport"/> so the endpoints are
    /// reachable before any printer is connected — pressing Connect swaps in the real transport.
    /// </summary>
    private async Task StartBridgeAsync()
    {
        try
        {
            _transport = new MockTransport();
            _printService = new PrintService(_transport, new EscPosDriver(), DemoProfile(PrinterLanguage.EscPos));

            var server = new EmbedIoBridgeServer();
            server.UseInterceptor(new CorsInterceptor(AllowedOrigins));
            new BridgeApi(_printService, "0.1.0").MapRoutes(server);

            // IPAddress.Loopback, never IPAddress.Any — FR-504.
            await server.StartAsync(IPAddress.Loopback, BridgePort, CancellationToken.None);
            _server = server;

            _serverStatus.Text = $"✅ Spike B: EmbedIO listening on 127.0.0.1:{BridgePort}";
            Log("Spike B PASSED — EmbedIO loaded and bound on net10.0-android.");
            Log($"Verify in Chrome on this device: http://127.0.0.1:{BridgePort}/v1/status");
            Log("Printing works against the mock until you press Connect.");
        }
        catch (Exception ex)
        {
            // Catch-all here and nowhere else: the point of a spike is to learn how it fails.
            _serverStatus.Text = "❌ Spike B: EmbedIO failed to start";
            Log($"Spike B FAILED — {ex.GetType().Name}: {ex.Message}");
            Log("If this is a load failure, ADR-009's fallback is GenHTTP. Escalate R-16.");
        }
    }

    // ---------------------------------------------------------------- Spike A

    private async Task RunPrinterProbeAsync()
    {
        var devices = BluetoothAccess.BondedDevices;
        if (devices.Count == 0)
        {
            Log("No paired Bluetooth devices. Pair the printer in Android Bluetooth settings first.");
            return;
        }

        _probeButton.Enabled = false;
        try
        {
            Log($"Probing {devices.Count} paired device(s)…");

            foreach (var device in devices)
            {
                if (device.Address is not { } address) continue;

                await using var transport = new SppTransport();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                var report = await new PrinterLanguageProbe(transport)
                    .ProbeAsync(address, device.Name ?? "(unnamed)", cts.Token);

                Log(report.ToReportText());
            }

            Log("── Spike A complete. The result decides which driver Day 2 uses.");
        }
        finally
        {
            _probeButton.Enabled = true;
        }
    }

    // ---------------------------------------------------------------- real printer

    /// <summary>Swap the mock for the first paired device and serve the API against it.</summary>
    private async Task ConnectFirstPrinterAsync()
    {
        var device = BluetoothAccess.BondedDevices.FirstOrDefault();
        if (device?.Address is not { } address)
        {
            Log("No paired Bluetooth device to connect to.");
            return;
        }

        _connectButton.Enabled = false;
        try
        {
            Log($"Connecting to {device.Name} ({address})…");

            var transport = new SppTransport();
            var result = await transport.ConnectAsync(address, CancellationToken.None);

            if (result.IsFailure)
            {
                Log($"Connect failed: {result.Error.Code} — {result.Error.OperatorMessage}");
                await transport.DisposeAsync();
                return;
            }

            // Language is provisional until Spike A reports. Both drivers are implemented, so
            // switching is a one-line change here.
            var language = PrinterLanguage.EscPos;
            IPrinterDriver driver = language == PrinterLanguage.Cpcl
                ? new CpclDriver()
                : new EscPosDriver();

            if (_transport is not null) await _transport.DisposeAsync();
            _transport = transport;
            _printService = new PrintService(transport, driver, DemoProfile(language));

            // Rebuild the API against the new service.
            if (_server is not null) await _server.DisposeAsync();
            var server = new EmbedIoBridgeServer();
            server.UseInterceptor(new CorsInterceptor(AllowedOrigins));
            new BridgeApi(_printService, "0.1.0").MapRoutes(server);
            await server.StartAsync(IPAddress.Loopback, BridgePort, CancellationToken.None);
            _server = server;

            _serverStatus.Text = $"✅ Connected: {device.Name} · {language}";
            Log($"Connected. The demo page will now print to {device.Name}.");
        }
        catch (Exception ex)
        {
            Log($"Connect failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _connectButton.Enabled = true;
        }
    }

    private static PrinterProfile DemoProfile(PrinterLanguage language) => new(
        Id: "demo",
        BluetoothAddress: string.Empty,
        DisplayName: "Demo printer",
        TransportType: TransportType.BtClassic,
        Language: language,
        PrintWidthDots: language == PrinterLanguage.Cpcl
            ? PrinterProfile.Widths.Label4InchAt203Dpi
            : PrinterProfile.Widths.Receipt80mmAt203Dpi,
        Dpi: 203,
        MediaType: language == PrinterLanguage.Cpcl ? MediaType.LabelGap : MediaType.Continuous,
        HasCutter: language != PrinterLanguage.Cpcl,
        SupportsStatusQuery: true);

    // ---------------------------------------------------------------- permissions

    private void RequestBluetoothPermissions()
    {
        // Two permission models across the supported range (NFR-402): runtime BLUETOOTH_CONNECT
        // from API 31, install-time BLUETOOTH below it.
        var needed = new List<string>();

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            needed.Add(Manifest.Permission.BluetoothConnect);
            needed.Add(Manifest.Permission.BluetoothScan);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            needed.Add(Manifest.Permission.PostNotifications);
        }

        // Platform APIs rather than AndroidX compat: minSdk is 29, so these are always present.
        var missing = needed
            .Where(p => CheckSelfPermission(p) != Permission.Granted)
            .ToArray();

        if (missing.Length > 0) RequestPermissions(missing, PermissionRequestCode);
    }

    // ---------------------------------------------------------------- plumbing

    private void Log(string message)
    {
        RunOnUiThread(() =>
        {
            var sb = new StringBuilder(message).Append('\n').Append(_output.Text);
            _output.Text = sb.ToString();
        });

        Android.Util.Log.Info("Bifrost", message);
    }

    protected override void OnDestroy()
    {
        _ = _server?.DisposeAsync();
        _ = _transport?.DisposeAsync();
        base.OnDestroy();
    }
}
