using System.Net;
using System.Text;
using Android;
using Android.Content.PM;
using Android.Widget;
using Bifrost.Server;
using Bifrost.Server.EmbedIO;
using Bifrost.Transport;
using Bifrost.Transport.Classic;
using Bifrost.Transport.Probe;

namespace Bifrost.App;

/// <summary>
/// Day 1 spike screen — answers the two questions the plan pulled forward to day one.
/// </summary>
/// <remarks>
/// <b>Spike A</b> — which command language does each available printer speak? Several printers are
/// on hand and none is identified; with two weeks there is no room to write the wrong driver.
///
/// <b>Spike B</b> — does EmbedIO actually load and serve on net10.0-android? ASP.NET Core does not
/// run on Android at all, so the server is a third-party dependency on the critical path (R-16).
/// Ten minutes of work here either retires that risk or exposes it while there is still time to
/// switch to GenHTTP.
///
/// This activity is scaffolding. The real UI arrives on Day 9.
/// </remarks>
[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity
{
    private const int BridgePort = 8437;
    private const int PermissionRequestCode = 1001;

    private TextView _output = null!;
    private TextView _serverStatus = null!;
    private IBridgeServer? _server;

    protected override async void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _output = FindViewById<TextView>(Resource.Id.output)!;
        _serverStatus = FindViewById<TextView>(Resource.Id.serverStatus)!;
        var probeButton = FindViewById<Button>(Resource.Id.probeButton)!;

        probeButton.Click += async (_, _) => await RunPrinterProbeAsync();

        RequestBluetoothPermissions();
        await StartSpikeServerAsync();
    }

    // ---------------------------------------------------------------- Spike B

    /// <summary>Prove EmbedIO binds loopback and serves a request on this runtime.</summary>
    private async Task StartSpikeServerAsync()
    {
        try
        {
            var server = new EmbedIoBridgeServer();

            server.MapGet("/v1/status", (_, _) => Task.FromResult(
                BridgeResponse.Ok("""{"bridge":{"version":"0.1.0","apiVersion":"v1"},"spike":"B"}""")));

            // IPAddress.Loopback, never IPAddress.Any — FR-504.
            await server.StartAsync(IPAddress.Loopback, BridgePort, CancellationToken.None);
            _server = server;

            _serverStatus.Text =
                $"✅ Spike B: EmbedIO listening on http://127.0.0.1:{BridgePort}/v1/status";
            Log($"Spike B PASSED — EmbedIO loaded and bound on net10.0-android.");
            Log($"Verify: open http://127.0.0.1:{BridgePort}/v1/status in Chrome on this device.");
        }
        catch (Exception ex)
        {
            // Catch-all is deliberate here and nowhere else: the whole point of the spike is to
            // learn how it fails if it fails.
            _serverStatus.Text = "❌ Spike B: EmbedIO failed to start";
            Log($"Spike B FAILED — {ex.GetType().Name}: {ex.Message}");
            Log("If this is a load/JIT failure, ADR-009's fallback is GenHTTP. Escalate R-16.");
        }
    }

    // ---------------------------------------------------------------- Spike A

    /// <summary>Probe every paired device and report what language it answers in.</summary>
    private async Task RunPrinterProbeAsync()
    {
        var devices = BluetoothAccess.BondedDevices;

        if (devices.Count == 0)
        {
            Log("No paired Bluetooth devices. Pair the printer in Android Bluetooth settings first.");
            return;
        }

        Log($"Probing {devices.Count} paired device(s)…");
        var summary = new StringBuilder();

        foreach (var device in devices)
        {
            var address = device.Address;
            var name = device.Name ?? "(unnamed)";

            if (address is null)
            {
                Log($"── {name}: no address, skipped");
                continue;
            }

            await using var transport = new SppTransport();
            var probe = new PrinterLanguageProbe(transport);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var report = await probe.ProbeAsync(address, name, cts.Token);

            var text = report.ToReportText();
            Log(text);
            summary.Append(text);
        }

        Log("── Spike A complete.");
        Log("Record the result: it decides which driver Day 2 builds.");
    }

    // ---------------------------------------------------------------- Permissions

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

        // Platform APIs rather than AndroidX compat: minSdk is 29, so CheckSelfPermission and
        // RequestPermissions are always present. No dependency needed for this.
        var missing = needed
            .Where(p => CheckSelfPermission(p) != Permission.Granted)
            .ToArray();

        if (missing.Length > 0)
        {
            RequestPermissions(missing, PermissionRequestCode);
        }
    }

    // ---------------------------------------------------------------- Plumbing

    private void Log(string message)
    {
        RunOnUiThread(() => _output.Text = $"{_output.Text}\n{message}");
        Android.Util.Log.Info("Bifrost.Spike", message);
    }

    protected override void OnDestroy()
    {
        _ = _server?.DisposeAsync();
        base.OnDestroy();
    }
}
