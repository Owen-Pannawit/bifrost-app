using System.Text;
using Android;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using Bifrost.App.Services;
using Bifrost.Core.Model;
using Bifrost.Transport;
using Bifrost.Transport.Classic;
using Bifrost.Transport.Probe;

namespace Bifrost.App;

/// <summary>
/// The demo screen: choose a printer, identify its language, connect.
/// </summary>
/// <remarks>
/// <para>
/// The activity is a <b>view over the service</b>, not the owner of anything. The bridge, the
/// printer connection and the HTTP server all live in <see cref="BridgeService"/> so that they
/// survive the operator switching to Chrome — which is a required step of the demo, not an edge
/// case.
/// </para>
/// <para>
/// The full screen set in DES-09 is Day 9 of the roadmap proper. This covers what the demo needs.
/// </para>
/// </remarks>
[Activity(Label = "@string/app_name", MainLauncher = true)]
public class MainActivity : Activity, IServiceConnection
{
    private const int PermissionRequestCode = 1001;

    private TextView _output = null!;
    private TextView _serverStatus = null!;
    private Spinner _printerSpinner = null!;
    private Button _connectButton = null!;
    private Button _probeButton = null!;
    private Button _testPrintButton = null!;
    private View _batteryWarning = null!;

    private BridgeService? _bridge;
    private IReadOnlyList<BluetoothDevice> _devices = [];

    /// <summary>Language learned from the probe, per Bluetooth address.</summary>
    private readonly Dictionary<string, PrinterLanguage> _detectedLanguage = [];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _output = FindViewById<TextView>(Resource.Id.output)!;
        _serverStatus = FindViewById<TextView>(Resource.Id.serverStatus)!;
        _printerSpinner = FindViewById<Spinner>(Resource.Id.printerSpinner)!;
        _connectButton = FindViewById<Button>(Resource.Id.connectButton)!;
        _probeButton = FindViewById<Button>(Resource.Id.probeButton)!;
        _batteryWarning = FindViewById<View>(Resource.Id.batteryWarning)!;

        _testPrintButton = FindViewById<Button>(Resource.Id.testPrintButton)!;

        _connectButton.Click += async (_, _) => await ConnectSelectedAsync();
        _probeButton.Click += async (_, _) => await ProbeSelectedAsync();
        _testPrintButton.Click += async (_, _) => await TestPrintAsync();

        FindViewById<Button>(Resource.Id.batteryButton)!.Click += (_, _) =>
        {
            if (!BatteryOptimisation.OpenSettings(this))
            {
                Log("This device has no battery-optimisation screen. On Xiaomi, Huawei or Oppo, " +
                    "enable Autostart for Bifrǫst instead.");
            }
        };

        RequestBluetoothPermissions();
        StartAndBindService();
    }

    protected override void OnResume()
    {
        base.OnResume();

        // Re-checked on every resume: the operator may have just come back from the settings
        // screen, and the banner going away is the confirmation that it worked.
        RefreshBatteryWarning();
    }

    /// <summary>
    /// Show the warning only while Android is allowed to sleep the app.
    /// </summary>
    /// <remarks>
    /// Battery optimisation kills the foreground service and drops the Bluetooth link silently —
    /// the app looks healthy and printing simply stops (R-03). Making it visible here is cheaper
    /// than diagnosing it by phone later.
    /// </remarks>
    private void RefreshBatteryWarning()
    {
        var exempt = BatteryOptimisation.IsExempt(this);
        _batteryWarning.Visibility = exempt ? ViewStates.Gone : ViewStates.Visible;

        if (!exempt && !_batteryWarningLogged)
        {
            _batteryWarningLogged = true;
            Log("Battery optimisation is ON. The printer connection may be dropped in the " +
                "background — set Bifrǫst to Unrestricted before demonstrating.");
        }
    }

    private bool _batteryWarningLogged;

    // ---------------------------------------------------------------- service binding

    private void StartAndBindService()
    {
        var intent = new Intent(this, typeof(BridgeService));

        // StartForegroundService, not StartService: from API 26 a background start must promote
        // itself to the foreground promptly or the system kills it.
        if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
        else StartService(intent);

        BindService(intent, this, Bind.AutoCreate);
    }

    public void OnServiceConnected(ComponentName? name, IBinder? service)
    {
        if (service is not BridgeService.BridgeBinder binder) return;

        _bridge = binder.Service;
        _bridge.StateChanged += OnBridgeStateChanged;

        LoadPairedDevices();
        RefreshStatus();

        Log(_bridge.IsServerListening
            ? "Bridge listening on 127.0.0.1:8437 — open http://127.0.0.1:8437/v1/status in Chrome."
            : $"Bridge failed to start: {_bridge.LastError}");
    }

    public void OnServiceDisconnected(ComponentName? name)
    {
        if (_bridge is not null) _bridge.StateChanged -= OnBridgeStateChanged;
        _bridge = null;
    }

    private void OnBridgeStateChanged() => RunOnUiThread(RefreshStatus);

    private void RefreshStatus()
    {
        if (_bridge is null) return;

        _serverStatus.Text = _bridge switch
        {
            { IsServerListening: false } => $"❌ Bridge not running — {_bridge.LastError}",
            { ConnectedPrinterName: { } printer } => $"✅ {printer} · {_bridge.ConnectedLanguage}",
            _ => "⚠ Bridge running, no printer connected",
        };
    }

    // ---------------------------------------------------------------- printer selection

    private void LoadPairedDevices()
    {
        _devices = BluetoothAccess.BondedDevices;

        if (_devices.Count == 0)
        {
            Log("No paired Bluetooth devices. Pair the printer in Android Bluetooth settings first.");
        }

        var labels = _devices
            .Select(d => $"{d.Name ?? "(unnamed)"}  ·  {d.Address}")
            .DefaultIfEmpty("(no paired devices)")
            .ToArray();

        _printerSpinner.Adapter = new ArrayAdapter<string>(
            this, Android.Resource.Layout.SimpleSpinnerDropDownItem, labels);
    }

    private BluetoothDevice? Selected()
    {
        var index = _printerSpinner.SelectedItemPosition;
        return index >= 0 && index < _devices.Count ? _devices[index] : null;
    }

    // ---------------------------------------------------------------- identify

    /// <summary>
    /// Probe the selected printer and remember what it answered, so Connect picks the right driver
    /// without anyone editing code.
    /// </summary>
    private async Task ProbeSelectedAsync()
    {
        if (Selected() is not { Address: { } address } device)
        {
            Log("Select a printer first.");
            return;
        }

        _probeButton.Enabled = false;
        try
        {
            Log($"Probing {device.Name}…");

            await using var transport = new SppTransport();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var report = await new PrinterLanguageProbe(transport)
                .ProbeAsync(address, device.Name ?? "(unnamed)", cts.Token);

            Log(report.ToReportText());

            if (report.InferredLanguage is { } language)
            {
                _detectedLanguage[address] = language;
                Log($"Detected {language}. Connect will use it automatically.");
            }
            else
            {
                // Only the Zebra queries can be inferred. ESC/POS is confirmed by the operator
                // seeing paper move, so it is offered rather than assumed.
                _detectedLanguage[address] = PrinterLanguage.EscPos;
                Log("No reply to the Zebra queries. If paper moved, it is ESC/POS — " +
                    "Connect will assume that. If nothing moved, this printer is not supported yet.");
            }
        }
        finally
        {
            _probeButton.Enabled = true;
        }
    }

    // ---------------------------------------------------------------- connect

    private async Task ConnectSelectedAsync()
    {
        if (_bridge is null)
        {
            Log("Bridge service not bound yet.");
            return;
        }

        if (Selected() is not { Address: { } address } device)
        {
            Log("Select a printer first.");
            return;
        }

        // Probe result if we have one, ESC/POS otherwise — the commonest case, and Identify is
        // there to correct it.
        var language = _detectedLanguage.TryGetValue(address, out var detected)
            ? detected
            : PrinterLanguage.EscPos;

        _connectButton.Enabled = false;
        try
        {
            Log($"Connecting to {device.Name} as {language}…");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _bridge.ConnectPrinterAsync(
                address, device.Name ?? "(unnamed)", language, cts.Token);

            Log(result.IsSuccess
                ? $"Connected. The demo page will now print to {device.Name}."
                : $"Connect failed: {result.Error.Code} — {result.Error.OperatorMessage}");
        }
        finally
        {
            _connectButton.Enabled = true;
        }
    }

    // ---------------------------------------------------------------- test print

    /// <summary>
    /// Print a self-check label. No web page, no HTTP, no CORS — just driver, transport, printer.
    /// </summary>
    /// <remarks>
    /// This is the button that answers the go/no-go question. Everything upstream of the driver
    /// is already covered by 64 automated tests; what none of them can prove is that bytes reach
    /// paper on hardware nobody has run this against.
    /// </remarks>
    private async Task TestPrintAsync()
    {
        if (_bridge is null)
        {
            Log("Bridge service not bound yet.");
            return;
        }

        _testPrintButton.Enabled = false;
        try
        {
            Log("Test print…");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var result = await _bridge.TestPrintAsync(cts.Token);

            if (result.IsSuccess)
            {
                Log($"Sent {result.Value.ByteCount} bytes. " +
                    "If a label came out, scan the barcode — that is the gate.");
            }
            else
            {
                // OperatorMessage is written to be actionable; showing the code as well gives
                // support something to search for (DES-09 §6).
                Log($"Test print failed: {result.Error.OperatorMessage} [{result.Error.Code}]");
            }
        }
        finally
        {
            _testPrintButton.Enabled = true;
        }
    }

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

    public override void OnRequestPermissionsResult(
        int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode != PermissionRequestCode) return;

        // Bonded devices are unreadable without BLUETOOTH_CONNECT, so the list must be rebuilt
        // once permission arrives or the spinner stays empty for no visible reason.
        LoadPairedDevices();
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
        if (_bridge is not null) _bridge.StateChanged -= OnBridgeStateChanged;

        // Unbind only. The service keeps running so the printer connection survives the operator
        // switching to Chrome — the entire reason it is a foreground service.
        try
        {
            UnbindService(this);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
            // Not bound. Nothing to do.
        }

        base.OnDestroy();
    }
}
