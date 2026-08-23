using Android.Bluetooth;
using Android.Content;

namespace Bifrost.Transport;

/// <summary>
/// Obtains the Bluetooth adapter in a way that is correct across API 29–35.
/// </summary>
/// <remarks>
/// <c>BluetoothAdapter.DefaultAdapter</c> is deprecated from API 31; the supported route is
/// <see cref="BluetoothManager"/> from the system service. The fleet spans Android 10 to 15
/// (NFR-401), so the modern call is used throughout — it is available well below our minimum.
///
/// The adapter is a process-wide singleton and must <b>not</b> be disposed by callers.
/// </remarks>
public static class BluetoothAccess
{
    public static BluetoothAdapter? Adapter
    {
        get
        {
            var context = Application.Context;
            var manager = context.GetSystemService(Context.BluetoothService) as BluetoothManager;
            return manager?.Adapter;
        }
    }

    /// <summary>Devices already paired in Android Bluetooth settings.</summary>
    /// <remarks>
    /// Only bonded devices are offered. Pairing itself is delegated to Android settings — reusing a
    /// flow operators may already know, and avoiding a second discovery UI that could disagree with
    /// the system one (FR-401, DES-09 §5.2).
    /// </remarks>
    public static IReadOnlyList<BluetoothDevice> BondedDevices =>
        Adapter?.BondedDevices?.ToList() ?? [];
}
