using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace MacWin.Services
{
    public record BtDevice(string Id, string Name, bool Paired, bool Connected, string Kind, bool Le);

    /// <summary>
    /// Paired and nearby Bluetooth devices through Windows' device enumeration (the same source as Settings).
    /// Connecting opens the device's services, which makes Windows bring the link (and audio profiles) up;
    /// disconnecting has no public API, so that stays with Windows Settings.
    /// </summary>
    public static class BluetoothDevices
    {
        const string ProtoClassic = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
        const string ProtoLe = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";
        const string PropConnected = "System.Devices.Aep.IsConnected";
        const string PropPaired = "System.Devices.Aep.IsPaired";
        const string PropMajor = "System.Devices.Aep.Bluetooth.Cod.Major";
        const string PropMinor = "System.Devices.Aep.Bluetooth.Cod.Minor";
        const string PropProto = "System.Devices.Aep.ProtocolId";
        static readonly string[] Props = { PropConnected, PropPaired, PropMajor, PropMinor, PropProto };

        static string Selector(bool paired) =>
            $"(System.Devices.Aep.ProtocolId:=\"{ProtoClassic}\" OR System.Devices.Aep.ProtocolId:=\"{ProtoLe}\") AND System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#{(paired ? "True" : "False")}";

        static BtDevice From(DeviceInformation d)
        {
            d.Properties.TryGetValue(PropConnected, out var c);
            d.Properties.TryGetValue(PropPaired, out var p);
            d.Properties.TryGetValue(PropMajor, out var maj);
            d.Properties.TryGetValue(PropMinor, out var min);
            d.Properties.TryGetValue(PropProto, out var proto);
            bool le = proto is Guid g && g == Guid.Parse(ProtoLe);
            return new BtDevice(d.Id, string.IsNullOrWhiteSpace(d.Name) ? "Unknown device" : d.Name, p as bool? ?? false, c as bool? ?? false, Kind(maj as uint? ?? 0, min as uint? ?? 0), le);
        }

        /// <summary>Rough device class from the Bluetooth "class of device" bits, for the icon.</summary>
        static string Kind(uint major, uint minor) => major switch
        {
            1 => "computer",
            2 => "phone",
            4 => (minor & 0x3F) is 1 or 2 or 6 or 7 ? "headphones" : "speaker",
            5 => (minor & 0x40) != 0 ? "keyboard" : (minor & 0x80) != 0 ? "mouse" : "controller",
            6 => "camera",
            7 => "watch",
            _ => "other",
        };

        public static async Task<List<BtDevice>> PairedAsync()
        {
            try
            {
                var devs = await DeviceInformation.FindAllAsync(Selector(true), Props).AsTask().ConfigureAwait(false);
                return devs.Select(From).GroupBy(x => x.Name + "|" + x.Le).Select(g => g.OrderByDescending(x => x.Connected).First())
                    .OrderByDescending(x => x.Connected).ThenBy(x => x.Name).ToList();
            }
            catch (Exception e) { App.Log(e); return new List<BtDevice>(); }
        }

        /// <summary>Watches for unpaired devices nearby while a page is open; raises on any thread.</summary>
        public sealed class Discovery : IDisposable
        {
            readonly DeviceWatcher _w;
            readonly Dictionary<string, BtDevice> _found = new();
            public event Action Changed;
            public List<BtDevice> Devices { get { lock (_found) return _found.Values.Where(d => d.Name != "Unknown device").OrderBy(d => d.Name).ToList(); } }

            public Discovery()
            {
                _w = DeviceInformation.CreateWatcher(Selector(false), Props, DeviceInformationKind.AssociationEndpoint);
                _w.Added += (_, d) => { lock (_found) _found[d.Id] = From(d); Changed?.Invoke(); };
                _w.Updated += (_, u) =>
                {
                    lock (_found)
                        if (_found.TryGetValue(u.Id, out var old))
                        {
                            u.Properties.TryGetValue(PropConnected, out var c);
                            _found[u.Id] = old with { Connected = c as bool? ?? old.Connected };
                        }
                    Changed?.Invoke();
                };
                _w.Removed += (_, u) => { lock (_found) _found.Remove(u.Id); Changed?.Invoke(); };
                try { _w.Start(); } catch (Exception e) { App.Log(e); }
            }

            public void Dispose()
            {
                try { if (_w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) _w.Stop(); } catch { }
            }
        }

        /// <summary>Asks Windows to pair; simple confirmations are accepted automatically (like Settings does).</summary>
        public static async Task<string> PairAsync(BtDevice d)
        {
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(d.Id).AsTask().ConfigureAwait(false);
                if (info.Pairing.IsPaired) return null;
                if (!info.Pairing.CanPair) return "This device can't be paired";
                var custom = info.Pairing.Custom;
                custom.PairingRequested += (_, a) =>
                {
                    if (a.PairingKind is DevicePairingKinds.ConfirmOnly or DevicePairingKinds.ConfirmPinMatch or DevicePairingKinds.DisplayPin) a.Accept();
                    else if (a.PairingKind == DevicePairingKinds.ProvidePin) a.Accept("0000");   // keyboards/old headsets: the usual default
                };
                var r = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin,
                    DevicePairingProtectionLevel.Default).AsTask().ConfigureAwait(false);
                return r.Status switch
                {
                    DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => null,
                    DevicePairingResultStatus.AuthenticationFailure or DevicePairingResultStatus.AuthenticationTimeout => "Pairing was rejected or timed out",
                    DevicePairingResultStatus.ConnectionRejected => "The device refused the connection",
                    DevicePairingResultStatus.RejectedByHandler => "Pairing needs a PIN — use Bluetooth Settings",
                    _ => "Couldn't pair (" + r.Status + ")",
                };
            }
            catch (Exception e) { App.Log(e); return "Couldn't pair"; }
        }

        public static async Task<bool> UnpairAsync(BtDevice d)
        {
            try
            {
                var info = await DeviceInformation.CreateFromIdAsync(d.Id).AsTask().ConfigureAwait(false);
                var r = await info.Pairing.UnpairAsync().AsTask().ConfigureAwait(false);
                return r.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired;
            }
            catch (Exception e) { App.Log(e); return false; }
        }

        /// <summary>
        /// Brings the link up: opening the device's services makes Windows connect, after which its audio/HID
        /// profiles attach by themselves. Returns null on success or a short reason.
        /// </summary>
        public static async Task<string> ConnectAsync(BtDevice d)
        {
            try
            {
                if (d.Le)
                {
                    using var le = await BluetoothLEDevice.FromIdAsync(d.Id).AsTask().ConfigureAwait(false);
                    if (le == null) return "Device not found";
                    var r = await le.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                    if (r.Status != GattCommunicationStatus.Success) return "Device not reachable";
                    await Task.Delay(800).ConfigureAwait(false);
                    return le.ConnectionStatus == BluetoothConnectionStatus.Connected ? null : "Device didn't answer";
                }
                using var dev = await BluetoothDevice.FromIdAsync(d.Id).AsTask().ConfigureAwait(false);
                if (dev == null) return "Device not found";
                if (dev.ConnectionStatus == BluetoothConnectionStatus.Connected) return null;
                var s = await dev.GetRfcommServicesAsync(BluetoothCacheMode.Uncached).AsTask().ConfigureAwait(false);
                if (s.Error != BluetoothError.Success && s.Error != BluetoothError.NotSupported) return "Device not reachable — is it on and in range?";
                // audio / HID profiles attach a moment after the link is up
                for (int i = 0; i < 12 && dev.ConnectionStatus != BluetoothConnectionStatus.Connected; i++) await Task.Delay(500).ConfigureAwait(false);
                return dev.ConnectionStatus == BluetoothConnectionStatus.Connected ? null : "Device didn't answer";
            }
            catch (Exception e) { App.Log(e); return "Couldn't connect"; }
        }
    }
}
