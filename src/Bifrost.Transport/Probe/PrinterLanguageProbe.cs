using System.Text;
using Bifrost.Core.Model;
using Bifrost.Core.Printing;

namespace Bifrost.Transport.Probe;

/// <summary>
/// Day 1 Spike A — works out what command language a printer speaks.
/// </summary>
/// <remarks>
/// <para>
/// The printers available are of unknown make and model, and with two weeks there is no room to
/// write the wrong driver. This runs first and decides.
/// </para>
/// <para>
/// The probes are safe to send in any order: each query is inert in the languages it does not
/// belong to (DES-06 §9). ESC/POS is probed <b>last</b> because it is the only one that consumes
/// paper — the Zebra queries are silent on an ESC/POS printer, but an ESC/POS feed on a Zebra
/// would waste a label before the quieter tests had run.
/// </para>
/// </remarks>
public sealed class PrinterLanguageProbe(IPrinterTransport transport)
{
    private static readonly TimeSpan ResponseWindow = TimeSpan.FromSeconds(2);

    /// <summary>Probe a printer already connected on <paramref name="address"/>.</summary>
    public async Task<ProbeReport> ProbeAsync(string address, string displayName, CancellationToken ct)
    {
        var connect = await transport.ConnectAsync(address, ct).ConfigureAwait(false);
        if (connect.IsFailure)
        {
            return ProbeReport.Unreachable(address, displayName, connect.Error);
        }

        var attempts = new List<ProbeAttempt>
        {
            await TryAsync("CPCL", "! U1 getvar \"device.languages\"\r\n", ct).ConfigureAwait(false),
            await TryAsync("ZPL", "~HI\r\n", ct).ConfigureAwait(false),
        };

        // Last, and the only one that moves paper.
        attempts.Add(await TryEscPosAsync(ct).ConfigureAwait(false));

        return new ProbeReport(address, displayName, attempts, Infer(attempts), Error: null);
    }

    private async Task<ProbeAttempt> TryAsync(string label, string command, CancellationToken ct)
    {
        var write = await transport
            .WriteAsync(Encoding.ASCII.GetBytes(command), ct)
            .ConfigureAwait(false);

        if (write.IsFailure) return new ProbeAttempt(label, Responded: false, Response: write.Error.Code);

        var read = await transport.ReadAsync(ResponseWindow, ct).ConfigureAwait(false);
        if (read.IsFailure) return new ProbeAttempt(label, Responded: false, Response: read.Error.Code);

        var responded = read.Value.Length > 0;
        var text = responded ? Encoding.ASCII.GetString(read.Value).Trim() : "(silence)";
        return new ProbeAttempt(label, responded, text);
    }

    private async Task<ProbeAttempt> TryEscPosAsync(CancellationToken ct)
    {
        // ESC @ (initialise), a visible marker, then three line feeds.
        // Verified by eye, not by response: most low-cost ESC/POS printers answer nothing at all.
        byte[] command =
        [
            0x1B, (byte)'@',
            .. "BIFROST ESC/POS PROBE"u8,
            0x0A, 0x0A, 0x0A,
        ];

        var write = await transport.WriteAsync(command, ct).ConfigureAwait(false);
        return write.IsFailure
            ? new ProbeAttempt("ESC/POS", Responded: false, Response: write.Error.Code)
            : new ProbeAttempt("ESC/POS", Responded: false, Response: "sent — CHECK IF PAPER MOVED");
    }

    private static PrinterLanguage? Infer(IReadOnlyList<ProbeAttempt> attempts)
    {
        // Only the Zebra queries can be inferred automatically. ESC/POS is confirmed by the
        // operator seeing paper move, which is why the result is nullable rather than guessed.
        if (attempts.Any(a => a is { Label: "CPCL", Responded: true })) return PrinterLanguage.Cpcl;
        if (attempts.Any(a => a is { Label: "ZPL", Responded: true })) return PrinterLanguage.Zpl;
        return null;
    }
}

public sealed record ProbeAttempt(string Label, bool Responded, string Response);

public sealed record ProbeReport(
    string Address,
    string DisplayName,
    IReadOnlyList<ProbeAttempt> Attempts,
    PrinterLanguage? InferredLanguage,
    PrinterError? Error)
{
    public static ProbeReport Unreachable(string address, string name, PrinterError error) =>
        new(address, name, [], null, error);

    public string ToReportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── {DisplayName}  [{Address}]");

        if (Error is not null)
        {
            sb.AppendLine($"   UNREACHABLE: {Error.Code} — {Error.OperatorMessage}");
            return sb.ToString();
        }

        foreach (var a in Attempts)
        {
            sb.AppendLine($"   {a.Label,-8} {(a.Responded ? "REPLIED" : "silent ")}  {a.Response}");
        }

        sb.AppendLine(InferredLanguage is { } lang
            ? $"   → {lang}"
            : "   → no reply to Zebra queries. If paper moved, it is ESC/POS.");

        return sb.ToString();
    }
}
