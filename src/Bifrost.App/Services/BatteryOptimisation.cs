using Android.Content;
using Android.OS;
using Android.Provider;

namespace Bifrost.App.Services;

/// <summary>
/// Detects whether Android is allowed to put this app to sleep, and offers a way to stop it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single most likely cause of intermittent field failures.</b> Battery optimisation kills
/// the foreground service and drops the Bluetooth connection silently — the app looks healthy and
/// prints simply stop. Risk R-03, and the reason OPS-01 §5 calls this the most important
/// deployment step.
/// </para>
/// <para>
/// <b>No new permission.</b> <c>REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c> would allow a one-tap
/// dialog, but it is not in the permission set DES-08 §6.1 authorises, and least privilege is a
/// requirement here (NFR-305). Opening the settings list costs the operator one extra tap and no
/// permission at all. For the fleet this is moot: OPS-01 §5 sets the exemption centrally by MDM,
/// and this screen exists for the devices MDM has not reached.
/// </para>
/// </remarks>
internal static class BatteryOptimisation
{
    /// <summary>True when Android will leave the app alone.</summary>
    public static bool IsExempt(Context context)
    {
        var power = (PowerManager?)context.GetSystemService(Context.PowerService);
        var package = context.PackageName;

        // Absent PowerManager or package name should not be possible; treating the unknown as
        // "exempt" would hide the warning precisely when we cannot verify it, so assume the worst.
        if (power is null || package is null) return false;

        return power.IsIgnoringBatteryOptimizations(package);
    }

    /// <summary>
    /// Opens the system screen listing apps under battery optimisation.
    /// </summary>
    /// <returns><c>false</c> if the device has no such screen, which some OEM builds do not.</returns>
    public static bool OpenSettings(Context context)
    {
        try
        {
            var intent = new Intent(Settings.ActionIgnoreBatteryOptimizationSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        }
        catch (ActivityNotFoundException)
        {
            // Aggressive OEM builds — Xiaomi, Huawei, Oppo — sometimes bury or remove this screen
            // and need a per-vendor autostart setting instead (OPS-01 §5). Nothing generic to do.
            return false;
        }
    }
}
