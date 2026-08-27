using System.Net;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;
using Bifrost.Core.Testing;
using Bifrost.Drivers.Cpcl;
using Bifrost.Drivers.EscPos;
using Bifrost.Server;
using Bifrost.Server.EmbedIO;
using Bifrost.Transport.Classic;

namespace Bifrost.App.Services;

/// <summary>
/// Owns the bridge: the HTTP server, the printer connection and the print service.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists, and why it is a demo blocker without it.</b> The demo flow is: open the app,
/// connect the printer, <i>switch to Chrome</i>, print. Switching backgrounds the activity, and
/// Android will tear down the Bluetooth connection of a backgrounded process within minutes — or
/// immediately, under an aggressive OEM power manager. A foreground service of type
/// <c>connectedDevice</c> is the only supported way to keep it alive (FR-407).
/// </para>
/// <para>
/// The server lives here rather than in the activity for the same reason ADR-004 gave: it is
/// started when the service starts and stopped with it, so it never runs without the notification
/// that tells the operator the bridge is live.
/// </para>
/// </remarks>
[Service(
    Name = "com.bearing.bifrost.BridgeService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public sealed class BridgeService : Service
{
    private const int BridgePort = 8437;

    /// <summary>
    /// Remote origins permitted to print. Loopback is allowed automatically on any port, so a page
    /// served from the device itself needs no entry here — see <see cref="CorsInterceptor"/>.
    /// </summary>
    /// <remarks>
    /// No wildcards: a compromised subdomain must not be able to put labels on physical stock
    /// (DES-08 §5). Replace this with the real intranet origin before the demo — including scheme
    /// and port, because origins compare exactly.
    /// </remarks>
    private static readonly string[] AllowedOrigins =
    [
        "http://intranet.company.local",
    ];

    private readonly BridgeBinder _binder;

    private IBridgeServer? _server;
    private IPrinterTransport? _transport;
    private PrintService? _printService;

    public BridgeService() => _binder = new BridgeBinder(this);

    /// <summary>Raised whenever the state the UI displays has changed.</summary>
    public event Action? StateChanged;

    public bool IsServerListening { get; private set; }

    public string? ConnectedPrinterName { get; private set; }

    public PrinterLanguage? ConnectedLanguage { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>
    /// The active print service. Named <c>Printing</c> rather than <c>PrintService</c> because
    /// <see cref="Context.PrintService"/> already exists on the base class — Android's own
    /// print-service constant, which is a fair reminder of what this project exists to avoid.
    /// </summary>
    public PrintService? Printing => _printService;

    public override IBinder OnBind(Intent? intent) => _binder;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        BridgeNotification.EnsureChannel(this);
        StartInForeground(BridgeUiState.Starting, "Bifrǫst — starting", "Bringing the print bridge up…");

        // Fire and forget: OnStartCommand must return promptly or Android kills the service.
        _ = StartBridgeAsync();

        // Sticky: if the process is killed while a printer is configured, come back.
        return StartCommandResult.Sticky;
    }

    private void StartInForeground(BridgeUiState state, string title, string detail)
    {
        var notification = BridgeNotification.Build(this, state, title, detail);

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                // From Android 14 the type is mandatory; from 29 the overload exists. Declaring
                // connectedDevice is what entitles the process to hold a Bluetooth link in the
                // background (DES-08 §6.1).
                StartForeground(BridgeNotification.Id, notification, ForegroundService.TypeConnectedDevice);
            }
            else
            {
                StartForeground(BridgeNotification.Id, notification);
            }
        }
        catch (Java.Lang.Exception ex)
        {
            // Android 14 validates a connectedDevice service against the app's *granted* runtime
            // permissions, not its manifest. On the very first launch after install,
            // BLUETOOTH_CONNECT has not been granted yet — the activity is still asking — and
            // startForeground throws, killing the process before the operator can answer the
            // prompt.
            //
            // Starting without the type keeps the bridge alive so the UI can come up and request
            // permission; the activity restarts the service once granted, and the second attempt
            // gets the type it needs. Degrading here is right: a bridge running without background
            // entitlement is worth far more than a process that dies at the moment of first use.
            Android.Util.Log.Warn("Bifrost",
                $"connectedDevice foreground service refused ({ex.Message}). " +
                "Falling back until Bluetooth permission is granted.");

            try
            {
                StartForeground(BridgeNotification.Id, notification);
            }
            catch (Java.Lang.Exception fallback)
            {
                Android.Util.Log.Error("Bifrost",
                    $"Foreground service could not start at all: {fallback.Message}");
            }
        }
    }

    // ---------------------------------------------------------------- bridge lifecycle

    /// <summary>
    /// Bring the server up against the mock, so the API is reachable before a printer is chosen.
    /// </summary>
    private async Task StartBridgeAsync()
    {
        try
        {
            _transport = new MockTransport();
            _printService = new PrintService(
                _transport, new EscPosDriver(), DemoProfile(PrinterLanguage.EscPos));

            await RestartServerAsync();

            IsServerListening = true;
            LastError = null;
            Publish(BridgeUiState.Attention, "Bifrǫst — no printer", "Open the app and choose a printer.");
        }
        catch (Exception ex)
        {
            // Catch-all: if EmbedIO cannot load on this runtime, that is R-16 and the operator
            // needs to see something other than a silent dead app.
            IsServerListening = false;
            LastError = $"{ex.GetType().Name}: {ex.Message}";
            Publish(BridgeUiState.Error, "Bifrǫst — bridge failed to start", "Call IT.");
        }
    }

    private async Task RestartServerAsync()
    {
        if (_server is not null) await _server.DisposeAsync();

        var server = new EmbedIoBridgeServer();
        server.UseInterceptor(new CorsInterceptor(AllowedOrigins));
        new BridgeApi(_printService!, "0.1.0").MapRoutes(server);

        // Loopback only — never IPAddress.Any (FR-504).
        await server.StartAsync(IPAddress.Loopback, BridgePort, CancellationToken.None);
        _server = server;
    }

    /// <summary>Connect a printer and rebuild the API against it.</summary>
    public async Task<Result> ConnectPrinterAsync(
        string address, string displayName, PrinterLanguage language, CancellationToken ct)
    {
        Publish(BridgeUiState.Starting, "Bifrǫst — connecting", $"Connecting to {displayName}…");

        var transport = new SppTransport();
        var connected = await transport.ConnectAsync(address, ct);

        if (connected.IsFailure)
        {
            await transport.DisposeAsync();
            LastError = connected.Error.Code;
            Publish(BridgeUiState.Error, "Bifrǫst — printer not connected", connected.Error.OperatorMessage);
            return connected;
        }

        IPrinterDriver driver = language switch
        {
            PrinterLanguage.Cpcl => new CpclDriver(),
            _ => new EscPosDriver(),
        };

        if (_transport is not null) await _transport.DisposeAsync();
        _transport = transport;
        _printService = new PrintService(transport, driver, DemoProfile(language));

        await RestartServerAsync();

        ConnectedPrinterName = displayName;
        ConnectedLanguage = language;
        LastError = null;

        Publish(BridgeUiState.Ready, "Bifrǫst — printer ready", $"{displayName} · {language}");
        return Result.Ok();
    }

    /// <summary>
    /// Print a self-check label: printer identity, capabilities and a scannable barcode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// FR-402. Its value is that it proves the whole path — driver, transport, Bluetooth link,
    /// printer — <b>without involving a web page at all</b>. That separates "Bluetooth printing
    /// does not work" from "the web integration does not work", which are different problems with
    /// different fixes, and the first must be answered before the second is worth asking.
    /// </para>
    /// <para>
    /// It also gives support something concrete: one sheet that can be photographed and sent in
    /// (DES-09 §5.3).
    /// </para>
    /// </remarks>
    public async Task<Result<PrintJob>> TestPrintAsync(CancellationToken ct)
    {
        if (_printService is null) return new PrinterError.NotConnected();

        var document = SelfCheckDocument.Create(
            _printService.Profile,
            ConnectedPrinterName ?? _printService.Profile.DisplayName,
            DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));

        return await _printService.PrintAsync(document, ct).ConfigureAwait(false);
    }

    private static PrinterProfile DemoProfile(PrinterLanguage language) => new(
        Id: "demo",
        BluetoothAddress: string.Empty,
        DisplayName: "Printer",
        TransportType: TransportType.BtClassic,
        Language: language,
        PrintWidthDots: language == PrinterLanguage.Cpcl
            ? PrinterProfile.Widths.Label4InchAt203Dpi
            : PrinterProfile.Widths.Receipt80mmAt203Dpi,
        Dpi: 203,
        MediaType: language == PrinterLanguage.Cpcl ? MediaType.LabelGap : MediaType.Continuous,
        HasCutter: language != PrinterLanguage.Cpcl,
        SupportsStatusQuery: true);

    private void Publish(BridgeUiState state, string title, string detail)
    {
        BridgeNotification.Update(this, state, title, detail);
        StateChanged?.Invoke();
    }

    public override void OnDestroy()
    {
        _ = _server?.DisposeAsync();
        _ = _transport?.DisposeAsync();
        IsServerListening = false;
        base.OnDestroy();
    }

    /// <summary>Hands the activity a direct reference — same process, so no IPC is involved.</summary>
    public sealed class BridgeBinder(BridgeService service) : Binder
    {
        public BridgeService Service { get; } = service;
    }
}
