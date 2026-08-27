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
    private Button _testPrintButton = null!;
    private Button _switchButton = null!;
    private Spinner _languageSpinner = null!;
    private View _batteryWarning = null!;

    private BridgeService? _bridge;
    private IReadOnlyList<BluetoothDevice> _devices = [];

    /// <summary>
    /// The languages offered in the spinner. Every driver we have is listed, with the one the
    /// printer reported selected — a printer that answers wrongly should never leave the operator
    /// unable to choose the language that actually works.
    /// </summary>
    private static readonly PrinterLanguage[] Languages =
        [PrinterLanguage.Cpcl, PrinterLanguage.EscPos, PrinterLanguage.Zpl];

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _output = FindViewById<TextView>(Resource.Id.output)!;
        _serverStatus = FindViewById<TextView>(Resource.Id.serverStatus)!;
        _printerSpinner = FindViewById<Spinner>(Resource.Id.printerSpinner)!;
        _connectButton = FindViewById<Button>(Resource.Id.connectButton)!;
        _batteryWarning = FindViewById<View>(Resource.Id.batteryWarning)!;
        _testPrintButton = FindViewById<Button>(Resource.Id.testPrintButton)!;
        _switchButton = FindViewById<Button>(Resource.Id.switchButton)!;
        _languageSpinner = FindViewById<Spinner>(Resource.Id.languageSpinner)!;

        PopulateLanguages(PrinterLanguage.Cpcl);

        _connectButton.Click += async (_, _) => await ConnectSelectedAsync(null);
        _switchButton.Click += async (_, _) => await SwitchLanguageAsync();
        _testPrintButton.Click += async (_, _) => await TestPrintAsync();

        FindViewById<Button>(Resource.Id.sgdButton)!.Click += async (_, _) =>
        {
            if (_bridge is null) { Log("Bridge service not bound yet."); return; }

            Log("Asking the printer to switch to CPCL…");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await _bridge.SetCpclModeAsync(cts.Token);

            Log(result.IsSuccess
                ? $"{result.Value}\nPower-cycle the printer, reconnect, then Test print."
                : $"Failed: {result.Error.OperatorMessage} [{result.Error.Code}]");
        };

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

    // ---------------------------------------------------------------- language list

    private void PopulateLanguages(PrinterLanguage selected)
    {
        var labels = Languages
            .Select(l => l == selected ? $"{Describe(l)}   ← detected" : Describe(l))
            .ToArray();

        _languageSpinner.Adapter = new ArrayAdapter<string>(
            this, Android.Resource.Layout.SimpleSpinnerDropDownItem, labels);

        _languageSpinner.SetSelection(Array.IndexOf(Languages, selected));
    }

    private static string Describe(PrinterLanguage language) => language switch
    {
        PrinterLanguage.Cpcl => "CPCL  (Zebra ZQ/QLn mobile)",
        PrinterLanguage.EscPos => "ESC/POS  (most low-cost receipt printers)",
        PrinterLanguage.Zpl => "ZPL  (Zebra label)",
        _ => language.ToString(),
    };

    private PrinterLanguage SelectedLanguage()
    {
        var index = _languageSpinner.SelectedItemPosition;
        return index >= 0 && index < Languages.Length ? Languages[index] : PrinterLanguage.Cpcl;
    }

    // ---------------------------------------------------------------- connect

    /// <summary>
    /// Connect, then ask the printer what it speaks and offer that as the selected language.
    /// </summary>
    /// <remarks>
    /// Checking after connecting rather than before is not a detail. A mobile printer accepts one
    /// connection at a time, so a probe that opens its own link fails before reaching the printer
    /// and reports silence — which reads as a printer that cannot answer, and is how this project
    /// spent a day driving a CPCL printer with an ESC/POS driver.
    /// </remarks>
    private async Task ConnectSelectedAsync(PrinterLanguage? forceLanguage)
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

        var name = device.Name ?? "(unnamed)";
        _connectButton.Enabled = false;
        _switchButton.Enabled = false;

        try
        {
            var language = forceLanguage ?? SelectedLanguage();
            Log($"Connecting to {name} as {language}…");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var result = await _bridge.ConnectPrinterAsync(address, name, language, cts.Token);

            if (result.IsFailure)
            {
                Log($"Connect failed: {result.Error.Code} — {result.Error.OperatorMessage}");
                return;
            }

            Log($"Connected as {language}.");

            // Only worth asking when the operator has not already overridden the answer.
            if (forceLanguage is null) await CheckLanguageAsync(address, name, language, cts.Token);
        }
        finally
        {
            _connectButton.Enabled = true;
            _switchButton.Enabled = true;
        }
    }

    /// <summary>Ask the printer what it speaks and reflect it in the list.</summary>
    private async Task CheckLanguageAsync(
        string address, string name, PrinterLanguage current, CancellationToken ct)
    {
        Log("Checking printer language…");

        var report = await _bridge!.ProbeLanguageAsync(address, name, ct);
        Log(report.ToReportText());

        if (report.InferredLanguage is not { } detected)
        {
            Log("The printer did not identify itself. Pick a language below and press Switch — " +
                "CPCL for a Zebra, ESC/POS for most others.");
            return;
        }

        PopulateLanguages(detected);

        if (detected == current)
        {
            Log($"Connected with the right driver ({detected}).");
            return;
        }

        Log($"Printer reports {detected} but we connected as {current}. Press Switch to reconnect.");
    }

    /// <summary>Reconnect using the language the operator picked.</summary>
    private async Task SwitchLanguageAsync()
    {
        if (_bridge is null)
        {
            Log("Bridge service not bound yet.");
            return;
        }

        var language = SelectedLanguage();
        Log($"Switching to {language} — disconnecting…");

        // Disconnect first rather than reconnecting over the top. The printer allows one link, and
        // a half-closed socket is the difference between a clean reconnect and a connect that
        // fails for reasons nobody can see.
        await _bridge.DisconnectPrinterAsync();

        await ConnectSelectedAsync(language);
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

        // Android 14 validates a connectedDevice foreground service against granted permissions,
        // so on first run the service started without that type. Restart it now that the grant
        // exists, or it would run for the whole session without background entitlement and the
        // connection would drop the first time the operator switches to Chrome.
        if (grantResults.Any(g => g == Permission.Granted))
        {
            var intent = new Intent(this, typeof(BridgeService));
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) StartForegroundService(intent);
            else StartService(intent);
        }
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
