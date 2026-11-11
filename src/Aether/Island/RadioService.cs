using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Aether.Core;
using Windows.Devices.Radios;

namespace Aether.Island;

/// <summary>
/// WiFi / Bluetooth on-off (Windows.Devices.Radios — the same switches as Windows' quick
/// settings) and the connected WiFi name + signal (native WLAN API).
/// </summary>
public static class RadioService
{
    private static Radio? _wifi, _bt;
    private static bool _started;

    public static bool HasWifi => _wifi is not null;
    public static bool HasBluetooth => _bt is not null;
    public static bool WifiOn => _wifi?.State == RadioState.On;
    public static bool BluetoothOn => _bt?.State == RadioState.On;

    /// <summary>Raised on the UI thread.</summary>
    public static event Action? Changed;

    public static async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) Log.Info($"[radio] access {access}");
            var radios = await Radio.GetRadiosAsync();
            foreach (var r in radios)
            {
                if (r.Kind == RadioKind.WiFi && _wifi is null) _wifi = r;
                else if (r.Kind == RadioKind.Bluetooth && _bt is null) _bt = r;
            }
            foreach (var r in new[] { _wifi, _bt })
                if (r is not null) r.StateChanged += (_, _) => Raise();
            Raise();
        }
        catch (Exception ex)
        {
            Log.Error("radio-start", ex);
        }
    }

    private static void Raise() => Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public static async void ToggleWifi() => await Toggle(_wifi);
    public static async void ToggleBluetooth() => await Toggle(_bt);

    private static async Task Toggle(Radio? r)
    {
        if (r is null) return;
        try
        {
            var result = await r.SetStateAsync(r.State == RadioState.On ? RadioState.Off : RadioState.On);
            if (result != RadioAccessStatus.Allowed) Log.Info($"[radio] set {r.Kind}: {result}");
        }
        catch (Exception ex) { Log.Error("radio-toggle", ex); }
        Raise();
    }

    // ---------------- connected WiFi name (wlanapi) ----------------
    public sealed record WifiLink(string Ssid, int SignalPercent);

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opCode, IntPtr reserved, out int size, out IntPtr data, IntPtr valueType);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr p);

    private const int wlan_intf_opcode_current_connection = 7;
    private const int wlan_interface_state_connected = 1;

    /// <summary>Current WiFi connection, or null when not connected / no adapter.</summary>
    public static WifiLink? CurrentWifi()
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return null;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return null;
            int count = Marshal.ReadInt32(list);
            // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then WLAN_INTERFACE_INFO[] (Guid 16 + WCHAR[256] + int state = 532 bytes)
            for (int i = 0; i < count; i++)
            {
                IntPtr info = list + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(info);
                int state = Marshal.ReadInt32(info + 16 + 512);
                if (state != wlan_interface_state_connected) continue;
                if (WlanQueryInterface(handle, ref guid, wlan_intf_opcode_current_connection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) continue;
                try
                {
                    // WLAN_CONNECTION_ATTRIBUTES: state(4) mode(4) profileName WCHAR[256](512)
                    //   then WLAN_ASSOCIATION_ATTRIBUTES: DOT11_SSID { ULONG len; UCHAR[32] } ...
                    IntPtr assoc = data + 4 + 4 + 512;
                    int len = Math.Clamp(Marshal.ReadInt32(assoc), 0, 32);
                    var bytes = new byte[len];
                    Marshal.Copy(assoc + 4, bytes, 0, len);
                    var ssid = System.Text.Encoding.UTF8.GetString(bytes);
                    // after SSID(36): bssType(4) bssid(6) + pad(2) phyType(4) phyIndex(4) signalQuality(4)
                    int quality = Marshal.ReadInt32(assoc + 36 + 4 + 8 + 4 + 4);
                    return new WifiLink(ssid, Math.Clamp(quality, 0, 100));
                }
                finally { WlanFreeMemory(data); }
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }
}
