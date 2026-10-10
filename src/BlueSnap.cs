// BlueSnap - keeps a paired Bluetooth (A2DP) speaker connected to this PC.
//
// Mechanism (all documented Windows APIs, no third-party code):
//   * Connect:  KSPROPERTY_ONESHOT_RECONNECT (KSPROPSETID_BtAudio, ksmedia.h) sent to the speaker's
//               Bluetooth audio KS filter. This is the same request the "Connect" button in Settings issues.
//               The filter is located via IMMDevice -> IDeviceTopology -> IConnector::GetDeviceIdConnectedTo.
//   * Verify:   the speaker's A2DP render endpoint state must become DEVICE_STATE_ACTIVE (MMDevice API).
//               PnP "Status = OK" is NOT used - it only means the paired device node exists.
//   * Events:   IMMNotificationClient reports connect/disconnect of the endpoint immediately.
//   * Presence: classic Bluetooth gives no event when a paired speaker becomes connectable, so while
//               disconnected the agent pages the speaker periodically (with back-off).
//   * Default:  optional, via the undocumented-but-stable IPolicyConfig interface (used by many audio tools).
//
// Interface layouts were taken from Windows SDK 10.0.19041 headers (mmdeviceapi.h, devicetopology.h,
// ks.h, ksmedia.h, bluetoothapis.h). Builds with the C# 5 compiler that ships with .NET Framework 4.x.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace BlueSnap
{
    // ---------------------------------------------------------------- configuration

    sealed class Config
    {
        public ulong Address;
        public bool SetAsDefault = true;
        public bool SetAsDefaultCommunications = false;
        public int HoldOffAfterDisconnectSec = 0;
        public int HandOffMinutes = 2;
        public int AttemptTimeoutSec = 6;
        public int FastGapSec = 2;
        public int FastPhaseMinutes = 60;
        public bool SlowWhileOtherBtAudioActive = true;
        public int SlowGapSec = 20;
        public bool ShowTrayIcon = true;
        public bool VerboseLog = false;

        public string Mac12 { get { return Address.ToString("x12"); } }
        public string AddressText { get { return FormatAddress(Address); } }

        public static Config Load(string path)
        {
            var c = new Config();
            if (!File.Exists(path)) return c;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "deviceaddress": c.Address = v.Length == 0 ? 0 : ParseAddress(v); break;
                    case "setasdefault": c.SetAsDefault = ParseBool(v); break;
                    case "setasdefaultcommunications": c.SetAsDefaultCommunications = ParseBool(v); break;
                    case "holdoffafterdisconnectsec": c.HoldOffAfterDisconnectSec = ParseInt(v, 0, 3600); break;
                    case "handoffminutes": c.HandOffMinutes = ParseInt(v, 0, 1440); break;
                    case "slowwhileotherbtaudioactive": c.SlowWhileOtherBtAudioActive = ParseBool(v); break;
                    case "attempttimeoutsec": c.AttemptTimeoutSec = ParseInt(v, 3, 60); break;
                    case "fastgapsec": c.FastGapSec = ParseInt(v, 0, 3600); break;
                    case "fastphaseminutes": c.FastPhaseMinutes = ParseInt(v, 0, 100000); break;
                    case "slowgapsec": c.SlowGapSec = ParseInt(v, 0, 3600); break;
                    case "showtrayicon": c.ShowTrayIcon = ParseBool(v); break;
                    case "verboselog": c.VerboseLog = ParseBool(v); break;
                }
            }
            return c;
        }

        public static ulong ParseAddress(string s)
        {
            string hex = s.Replace(":", "").Replace("-", "").Trim();
            if (!Regex.IsMatch(hex, "^[0-9A-Fa-f]{12}$"))
                throw new FormatException("Bluetooth address must be 12 hex digits, e.g. 12:34:56:78:9A:BC (got '" + s + "')");
            return Convert.ToUInt64(hex, 16);
        }

        public static string FormatAddress(ulong a)
        {
            string h = a.ToString("X12");
            var sb = new StringBuilder();
            for (int i = 0; i < 12; i += 2) { if (i > 0) sb.Append(':'); sb.Append(h, i, 2); }
            return sb.ToString();
        }

        static bool ParseBool(string v)
        {
            v = v.ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        static int ParseInt(string v, int min, int max)
        {
            int n = int.Parse(v);
            return Math.Max(min, Math.Min(max, n));
        }
    }

    // ---------------------------------------------------------------- logging

    static class Log
    {
        static readonly object Gate = new object();
        public static string FilePath;
        public static bool Echo;
        public static bool Verbose;
        const long MaxBytes = 512 * 1024;

        public static void Info(string m) { Write("INFO ", m); }
        public static void Warn(string m) { Write("WARN ", m); }
        public static void Debug(string m) { if (Verbose) Write("DEBUG", m); }

        static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg;
            lock (Gate)
            {
                if (Echo) Console.WriteLine(line);
                if (FilePath == null) return;
                try
                {
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    // ---------------------------------------------------------------- COM interop: MMDevice / DeviceTopology / KS

    enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    static class DeviceState
    {
        public const int Active = 0x1, Disabled = 0x2, NotPresent = 0x4, Unplugged = 0x8, All = 0xF;
        public static string Name(int s)
        {
            switch (s)
            {
                case Active: return "ACTIVE (connected)";
                case Disabled: return "DISABLED (in Sound settings)";
                case NotPresent: return "NOT_PRESENT (stale)";
                case Unplugged: return "UNPLUGGED (not connected)";
                default: return "0x" + s.ToString("X");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PROPVARIANT { public ushort vt; ushort r1, r2, r3; public IntPtr p; IntPtr p2; }

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PROPERTYKEY key);
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IConnector
    {
        [PreserveSig] int GetType_(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected(out int connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength, IntPtr data, uint dataLength, out uint bytesReturned);
    }

    // Undocumented but long-stable (Windows 7 - 11); used by SoundSwitch, EarTrumpet, AudioDeviceCmdlets.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigCo { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();          // placeholders keep the vtable order; never called
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, ERole role);
    }

    sealed class SpeakerEndpoint
    {
        public string EndpointId;   // MMDevice endpoint id, e.g. {0.0.0.00000000}.{ab2a006d-...}
        public string FilterId;     // KS filter of the Bluetooth A2DP driver (contains the BT address)
        public string Name;
        public int State;

        static readonly Regex MacInFilter = new Regex(@"&([0-9a-f]{12})_c", RegexOptions.IgnoreCase);
        public string AddressText
        {
            get
            {
                Match m = MacInFilter.Match(FilterId ?? "");
                return m.Success ? Config.FormatAddress(Convert.ToUInt64(m.Groups[1].Value, 16)) : "?";
            }
        }
    }

    sealed class BtAudio : IDisposable
    {
        const int CLSCTX_ALL = 0x17;
        const int S_OK = 0;
        // Bluetooth "AudioSink" service class (A2DP sink on the speaker).
        const string A2dpSinkFilterTag = "bthenum#{0000110b-0000-1000-8000-00805f9b34fb}";
        static readonly Guid KSPROPSETID_BtAudio = new Guid("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
        const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
        const uint KSPROPERTY_ONESHOT_DISCONNECT = 1;
        const uint KSPROPERTY_TYPE_GET = 0x1;
        const uint IOCTL_KS_PROPERTY = 0x2F0003; // CTL_CODE(FILE_DEVICE_KS, 0, METHOD_NEITHER, FILE_ANY_ACCESS)
        static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY { fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), pid = 14 };

        readonly IMMDeviceEnumerator _enum;
        IMMNotificationClient _registered;

        public BtAudio() { _enum = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo(); }

        public void Register(IMMNotificationClient client)
        {
            Check(_enum.RegisterEndpointNotificationCallback(client), "RegisterEndpointNotificationCallback");
            _registered = client;
        }

        public void Dispose()
        {
            try { if (_registered != null) _enum.UnregisterEndpointNotificationCallback(_registered); } catch { }
            _registered = null;
            try { Marshal.ReleaseComObject(_enum); } catch { }
        }

        /// <summary>Render endpoints backed by a Bluetooth A2DP sink; mac12 = null returns all of them.</summary>
        public List<SpeakerEndpoint> FindEndpoints(string mac12, int stateMask)
        {
            var list = new List<SpeakerEndpoint>();
            IMMDeviceCollection col;
            Check(_enum.EnumAudioEndpoints(EDataFlow.eRender, stateMask, out col), "EnumAudioEndpoints");
            try
            {
                uint n;
                Check(col.GetCount(out n), "IMMDeviceCollection.GetCount");
                for (uint i = 0; i < n; i++)
                {
                    IMMDevice dev;
                    if (col.Item(i, out dev) != S_OK) continue;
                    try
                    {
                        string filter = GetFilterId(dev);
                        if (filter == null) continue;
                        string f = filter.ToLowerInvariant();
                        if (!f.Contains(A2dpSinkFilterTag)) continue;
                        if (mac12 != null && !f.Contains("&" + mac12 + "_c")) continue;
                        var ep = new SpeakerEndpoint { FilterId = filter, Name = GetFriendlyName(dev) };
                        dev.GetId(out ep.EndpointId);
                        dev.GetState(out ep.State);
                        list.Add(ep);
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
            finally { Marshal.ReleaseComObject(col); }
            return list;
        }

        public int GetEndpointState(string endpointId)
        {
            IMMDevice d;
            if (_enum.GetDevice(endpointId, out d) != S_OK) return 0;
            try { int s; return d.GetState(out s) == S_OK ? s : 0; }
            finally { Marshal.ReleaseComObject(d); }
        }

        public string GetDefaultRenderId(ERole role)
        {
            IMMDevice d;
            if (_enum.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out d) != S_OK) return null;
            try { string id; return d.GetId(out id) == S_OK ? id : null; }
            finally { Marshal.ReleaseComObject(d); }
        }

        public static void SetDefault(string endpointId, bool includeCommunications)
        {
            var pc = (IPolicyConfig)new PolicyConfigCo();
            try
            {
                Check(pc.SetDefaultEndpoint(endpointId, ERole.eConsole), "SetDefaultEndpoint(console)");
                Check(pc.SetDefaultEndpoint(endpointId, ERole.eMultimedia), "SetDefaultEndpoint(multimedia)");
                if (includeCommunications)
                    Check(pc.SetDefaultEndpoint(endpointId, ERole.eCommunications), "SetDefaultEndpoint(communications)");
            }
            finally { Marshal.ReleaseComObject(pc); }
        }

        /// <summary>
        /// Asks the Bluetooth audio driver to page the speaker and bring up A2DP.
        /// Returns null if the driver accepted the request (the connection itself is asynchronous and must be
        /// verified separately), otherwise a description of the failure.
        /// </summary>
        public string RequestReconnect(SpeakerEndpoint ep) { return SendBtAudioProperty(ep, KSPROPERTY_ONESHOT_RECONNECT, "RECONNECT"); }

        /// <summary>Asks the driver to drop the speaker's audio connection (same as "Disconnect" in Settings).</summary>
        public string RequestDisconnect(SpeakerEndpoint ep) { return SendBtAudioProperty(ep, KSPROPERTY_ONESHOT_DISCONNECT, "DISCONNECT"); }

        string SendBtAudioProperty(SpeakerEndpoint ep, uint propertyId, string name)
        {
            int hr = SendViaKsControl(ep.FilterId, propertyId);
            if (hr == S_OK) { Log.Debug("KSPROPERTY_ONESHOT_" + name + " sent via IKsControl."); return null; }
            int err = SendViaIoctl(ep.FilterId, propertyId);
            if (err == 0) { Log.Debug("IKsControl path failed (0x" + hr.ToString("X8") + "), IOCTL fallback succeeded"); return null; }
            return "IKsControl hr=0x" + hr.ToString("X8") + ", IOCTL_KS_PROPERTY win32 error=" + err;
        }

        int SendViaKsControl(string filterId, uint propertyId)
        {
            IMMDevice filter;
            int hr = _enum.GetDevice(filterId, out filter);
            if (hr != S_OK) return hr;
            try
            {
                Guid iid = typeof(IKsControl).GUID;
                object o;
                hr = filter.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o);
                if (hr != S_OK) return hr;
                var ks = (IKsControl)o;
                try
                {
                    var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = propertyId, Flags = KSPROPERTY_TYPE_GET };
                    uint returned;
                    return ks.KsProperty(ref prop, (uint)Marshal.SizeOf(typeof(KSPROPERTY)), IntPtr.Zero, 0, out returned);
                }
                finally { Marshal.ReleaseComObject(ks); }
            }
            finally { Marshal.ReleaseComObject(filter); }
        }

        // Fallback: open the KS filter interface directly and send IOCTL_KS_PROPERTY.
        static int SendViaIoctl(string filterId, uint propertyId)
        {
            int dot = filterId.IndexOf("}.", StringComparison.Ordinal);
            string path = dot >= 0 ? filterId.Substring(dot + 2) : filterId;
            using (var h = Native.CreateFile(path, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                                             IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return Marshal.GetLastWin32Error();
                var prop = new KSPROPERTY { Set = KSPROPSETID_BtAudio, Id = propertyId, Flags = KSPROPERTY_TYPE_GET };
                uint returned;
                bool ok = Native.DeviceIoControl(h, IOCTL_KS_PROPERTY, ref prop, (uint)Marshal.SizeOf(typeof(KSPROPERTY)), IntPtr.Zero, 0, out returned, IntPtr.Zero);
                return ok ? 0 : Marshal.GetLastWin32Error();
            }
        }

        static string GetFilterId(IMMDevice dev)
        {
            Guid iid = typeof(IDeviceTopology).GUID;
            object o;
            if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o) != S_OK || o == null) return null;
            var topo = (IDeviceTopology)o;
            try
            {
                uint count;
                if (topo.GetConnectorCount(out count) != S_OK || count == 0) return null;
                IConnector con;
                if (topo.GetConnector(0, out con) != S_OK) return null;
                try { string id; return con.GetDeviceIdConnectedTo(out id) == S_OK ? id : null; }
                finally { Marshal.ReleaseComObject(con); }
            }
            finally { Marshal.ReleaseComObject(topo); }
        }

        static string GetFriendlyName(IMMDevice dev)
        {
            IPropertyStore store;
            if (dev.OpenPropertyStore(0 /* STGM_READ */, out store) != S_OK) return "?";
            try
            {
                PROPERTYKEY key = PKEY_Device_FriendlyName;
                PROPVARIANT pv;
                if (store.GetValue(ref key, out pv) != S_OK) return "?";
                try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.p) : "?"; }
                finally { Native.PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }

        static void Check(int hr, string what)
        {
            if (hr != S_OK) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }
    }

    // ---------------------------------------------------------------- Win32: Bluetooth radio / device info

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        public override string ToString()
        {
            if (Year == 0) return "never";
            try { return new DateTime(Year, Month, Day, Hour, Minute, Second, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return "?"; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    static class Native
    {
        public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        [StructLayout(LayoutKind.Sequential)]
        public struct BLUETOOTH_FIND_RADIO_PARAMS { public uint dwSize; }

        [DllImport("bthprops.cpl", SetLastError = true)]
        public static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS p, out IntPtr hRadio);
        [DllImport("bthprops.cpl")]
        public static extern bool BluetoothFindRadioClose(IntPtr hFind);
        [DllImport("bthprops.cpl")]
        public static extern int BluetoothGetDeviceInfo(IntPtr hRadio, ref BLUETOOTH_DEVICE_INFO info);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, ref KSPROPERTY inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();
        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PROPVARIANT pv);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        public static bool RadioPresent()
        {
            try
            {
                var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_FIND_RADIO_PARAMS)) };
                IntPtr radio;
                IntPtr find = BluetoothFindFirstRadio(ref p, out radio);
                if (find == IntPtr.Zero) return false;
                CloseHandle(radio);
                BluetoothFindRadioClose(find);
                return true;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
        }

        /// <summary>Returns false if Windows has no record of the device (not paired / removed).</summary>
        public static bool TryGetDeviceInfo(ulong address, out BLUETOOTH_DEVICE_INFO info)
        {
            info = new BLUETOOTH_DEVICE_INFO { dwSize = (uint)Marshal.SizeOf(typeof(BLUETOOTH_DEVICE_INFO)), Address = address };
            try { return BluetoothGetDeviceInfo(IntPtr.Zero, ref info) == 0; }
            catch (DllNotFoundException) { return false; }
        }
    }

    // ---------------------------------------------------------------- endpoint notifications

    [ComVisible(true)]
    sealed class EndpointWatcher : IMMNotificationClient
    {
        readonly AutoResetEvent _wake;
        volatile string[] _targets = new string[0];

        public EndpointWatcher(AutoResetEvent wake) { _wake = wake; }

        public void SetTargets(List<SpeakerEndpoint> eps)
        {
            var ids = new string[eps.Count];
            for (int i = 0; i < ids.Length; i++) ids[i] = eps[i].EndpointId;
            _targets = ids;
        }

        bool IsTarget(string id)
        {
            foreach (string t in _targets)
                if (string.Equals(t, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Callbacks arrive on MMDevice worker threads: only signal, never block or call back into COM here.
        public int OnDeviceStateChanged(string id, int newState)
        {
            if (IsTarget(id)) { Log.Debug("Endpoint event: state -> " + DeviceState.Name(newState)); _wake.Set(); }
            return 0;
        }
        public int OnDeviceAdded(string id) { _wake.Set(); return 0; }
        public int OnDeviceRemoved(string id) { if (IsTarget(id)) _wake.Set(); return 0; }
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string id) { return 0; }
        public int OnPropertyValueChanged(string id, PROPERTYKEY key) { return 0; }
    }

    // ---------------------------------------------------------------- the monitoring agent

    sealed class Agent
    {
        public const string StopEventName = @"Local\BlueSnap.Stop";
        public const string AttemptMutexName = @"Local\BlueSnap.Attempt";

        readonly Config _cfg;
        readonly EventWaitHandle _stop;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);

        public volatile string Status = "Starting";
        public volatile bool Connected;
        volatile bool _paused;
        volatile bool _connectNow;
        volatile bool _handOff;

        bool _wasConnected;
        DateTime _unavailableSince = DateTime.MinValue;
        DateTime _holdUntil = DateTime.MinValue;
        DateTime _lastSummary = DateTime.MinValue;
        bool _slowPhaseLogged;
        int _attempts;
        string _lastProblem;

        public Agent(Config cfg, EventWaitHandle stop) { _cfg = cfg; _stop = stop; }

        public bool Paused
        {
            get { return _paused; }
            set { _paused = value; Log.Info(value ? "Auto-connect paused by user." : "Auto-connect resumed by user."); _holdUntil = DateTime.MinValue; _wake.Set(); }
        }
        public void ConnectNow() { _handOff = false; _connectNow = true; _wake.Set(); }
        /// <summary>Disconnects the speaker and suspends auto-connect for HandOffMinutes so a phone can take it.</summary>
        public void HandOff() { _connectNow = false; _handOff = true; _wake.Set(); }
        public void Stop() { _stop.Set(); }
        bool Stopping { get { return _stop.WaitOne(0); } }

        /// <summary>Waits until timeout, an endpoint event, or stop. Returns true if stopping.</summary>
        bool WaitWake(int ms)
        {
            return WaitHandle.WaitAny(new WaitHandle[] { _stop, _wake }, Math.Max(0, ms)) == 0;
        }

        public void Run()
        {
            Log.Info("Agent started. Target speaker " + _cfg.AddressText +
                     "; hold-off " + _cfg.HoldOffAfterDisconnectSec + " s, attempt timeout " + _cfg.AttemptTimeoutSec +
                     " s, gaps " + _cfg.FastGapSec + " s (first " + _cfg.FastPhaseMinutes + " min) / " + _cfg.SlowGapSec + " s.");
            BtAudio audio = null;
            EndpointWatcher watcher = null;
            while (!Stopping)
            {
                try
                {
                    if (audio == null)
                    {
                        audio = new BtAudio();
                        watcher = new EndpointWatcher(_wake);
                        audio.Register(watcher);
                    }
                    Iterate(audio, watcher);
                }
                catch (Exception ex)
                {
                    Log.Warn("Unexpected error, re-initialising in 10 s: " + ex.GetType().Name + ": " + ex.Message);
                    if (audio != null) { audio.Dispose(); audio = null; }
                    if (WaitWake(10000)) break;
                }
            }
            if (audio != null) audio.Dispose();
            Log.Info("Agent stopped.");
        }

        void Iterate(BtAudio audio, EndpointWatcher watcher)
        {
            if (_paused) { SetStatus("Paused"); WaitWake(5000); return; }

            if (!Native.RadioPresent())
            {
                Problem("Bluetooth adapter not found or disabled; waiting for it to come back.");
                _wasConnected = false; Connected = false;
                SetStatus("Bluetooth adapter unavailable");
                WaitWake(10000);
                return;
            }

            List<SpeakerEndpoint> eps = audio.FindEndpoints(_cfg.Mac12, DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled);
            watcher.SetTargets(eps);

            if (eps.Count == 0)
            {
                BLUETOOTH_DEVICE_INFO bi;
                Problem(Native.TryGetDeviceInfo(_cfg.Address, out bi) && bi.fRemembered != 0
                    ? "Speaker " + _cfg.AddressText + " is paired but has no stereo (A2DP) audio endpoint; re-pair it or check Sound settings."
                    : "Speaker " + _cfg.AddressText + " is not paired with this PC. Pair it once in Settings > Bluetooth & other devices.");
                SetStatus("Speaker not paired / no audio endpoint");
                WaitWake(60000);
                return;
            }

            SpeakerEndpoint active = eps.Find(e => e.State == DeviceState.Active);
            if (_handOff)
            {
                _handOff = false;
                _holdUntil = DateTime.Now.AddMinutes(_cfg.HandOffMinutes);
                Log.Info("Hand-off requested: auto-connect suspended until " + _holdUntil.ToString("HH:mm:ss") + ".");
                if (active != null)
                {
                    string err = audio.RequestDisconnect(active);
                    if (err != null) Log.Warn("Disconnect request failed: " + err);
                }
            }
            if (active != null)
            {
                OnConnected(audio, active);
                WaitWake(60000); // endpoint events wake us on disconnect; timeout is only a safety net
                return;
            }

            SpeakerEndpoint ep = eps.Find(e => e.State == DeviceState.Unplugged);
            if (ep == null)
            {
                Problem("The speaker's audio endpoint is disabled in Sound settings; enable it to allow auto-connect.");
                SetStatus("Speaker endpoint disabled");
                WaitWake(60000);
                return;
            }
            _lastProblem = null;

            DateTime now = DateTime.Now;
            if (_wasConnected)
            {
                _wasConnected = false; Connected = false;
                _unavailableSince = now;
                DateTime hold = now.AddSeconds(_cfg.HoldOffAfterDisconnectSec);
                if (hold > _holdUntil) _holdUntil = hold;
                Log.Info("Disconnected from the PC." + (_holdUntil > now
                    ? " Not reconnecting until " + _holdUntil.ToString("HH:mm:ss") + " so another device can take the speaker." : ""));
            }
            if (_unavailableSince == DateTime.MinValue) _unavailableSince = now;

            if (!_connectNow && now < _holdUntil)
            {
                SetStatus("Released until " + _holdUntil.ToString("HH:mm"));
                WaitWake((int)(_holdUntil - now).TotalMilliseconds);
                return;
            }
            if (_connectNow) Log.Info("Manual connect requested.");
            _connectNow = false;
            _holdUntil = DateTime.MinValue;

            Attempt(audio, ep);
        }

        void OnConnected(BtAudio audio, SpeakerEndpoint ep)
        {
            if (!_wasConnected)
            {
                BLUETOOTH_DEVICE_INFO bi;
                bool acl = Native.TryGetDeviceInfo(_cfg.Address, out bi) && bi.fConnected != 0;
                Log.Info("Connected: '" + ep.Name + "' A2DP endpoint is ACTIVE" +
                         (_attempts > 0 ? " after " + _attempts + " attempt(s)" : "") +
                         " (Bluetooth link " + (acl ? "up" : "state unknown") + ").");
                _wasConnected = true; Connected = true;
                _attempts = 0;
                _unavailableSince = DateTime.MinValue;
                _lastSummary = DateTime.MinValue;
                _slowPhaseLogged = false;
                _connectNow = false;
                if (_cfg.SetAsDefault) EnsureDefault(audio, ep);
            }
            SetStatus("Connected");
        }

        void EnsureDefault(BtAudio audio, SpeakerEndpoint ep)
        {
            // Give Windows a moment: it often switches to the new device by itself.
            if (_stop.WaitOne(1500)) return;
            try
            {
                if (audio.GetEndpointState(ep.EndpointId) != DeviceState.Active) return;
                string current = audio.GetDefaultRenderId(ERole.eMultimedia);
                if (string.Equals(current, ep.EndpointId, StringComparison.OrdinalIgnoreCase)) { Log.Debug("Speaker is already the default output."); return; }
                BtAudio.SetDefault(ep.EndpointId, _cfg.SetAsDefaultCommunications);
                Log.Info("Set '" + ep.Name + "' as the default audio output.");
            }
            catch (Exception ex) { Log.Warn("Could not set default audio output: " + ex.Message); }
        }

        void Attempt(BtAudio audio, SpeakerEndpoint ep)
        {
            // Serialise with any other attempt (e.g. a concurrent "bluesnap-cli connect" from the CLI).
            using (var m = new Mutex(false, AttemptMutexName))
            {
                bool owned;
                try { owned = m.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (!owned) { WaitWake(2000); return; }
                try
                {
                    _attempts++;
                    SetStatus("Connecting...");
                    var sw = Stopwatch.StartNew();
                    string err = audio.RequestReconnect(ep);
                    if (err != null) Problem("Bluetooth audio driver rejected the connect request: " + err);
                    else Log.Debug("Connect request #" + _attempts + " accepted by driver.");

                    if (err == null && WaitForActive(audio, ep.EndpointId, _cfg.AttemptTimeoutSec * 1000))
                    {
                        Log.Debug("Endpoint became ACTIVE " + sw.ElapsedMilliseconds + " ms after request.");
                        return; // next iteration logs "Connected"
                    }
                }
                finally { m.ReleaseMutex(); }
            }
            if (Stopping) return;

            DateTime now = DateTime.Now;
            bool otherBusy = _cfg.SlowWhileOtherBtAudioActive && OtherBtAudioActive(audio);
            bool fast = (now - _unavailableSince).TotalMinutes < _cfg.FastPhaseMinutes && !otherBusy;
            if (otherBusy != _otherBusyLogged)
            {
                Log.Info(otherBusy ? "Another Bluetooth audio device is playing; retrying gently (every ~" + (_cfg.AttemptTimeoutSec + _cfg.SlowGapSec) + " s) to avoid audio glitches on it."
                                   : "No other Bluetooth audio device active any more; back to normal retry pace.");
                _otherBusyLogged = otherBusy;
            }
            int gap = fast ? _cfg.FastGapSec : _cfg.SlowGapSec;
            if (_lastSummary == DateTime.MinValue)
            {
                Log.Info("Speaker not reachable (off, out of range, or busy with another device). Retrying about every " +
                         (_cfg.AttemptTimeoutSec + gap) + " s.");
                _lastSummary = now;
            }
            else if (!fast && !otherBusy && !_slowPhaseLogged)
            {
                Log.Info("Speaker unavailable for " + _cfg.FastPhaseMinutes + " min; slowing retries to about every " + (_cfg.AttemptTimeoutSec + gap) + " s.");
                _slowPhaseLogged = true;
                _lastSummary = now;
            }
            else if ((now - _lastSummary).TotalMinutes >= 30)
            {
                Log.Info("Still waiting for the speaker (" + _attempts + " attempts since " + _unavailableSince.ToString("HH:mm") + ").");
                _lastSummary = now;
            }
            SetStatus("Waiting for speaker");
            WaitWake(gap * 1000);
        }

        bool _otherBusyLogged;

        // Paging the speaker competes for radio time with other Bluetooth audio streams (e.g. a headset).
        bool OtherBtAudioActive(BtAudio audio)
        {
            try { return audio.FindEndpoints(null, DeviceState.Active).Exists(e => e.FilterId.IndexOf("&" + _cfg.Mac12 + "_c", StringComparison.OrdinalIgnoreCase) < 0); }
            catch { return false; }
        }

        bool WaitForActive(BtAudio audio, string endpointId, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                if (audio.GetEndpointState(endpointId) == DeviceState.Active) return true;
                long left = timeoutMs - sw.ElapsedMilliseconds;
                if (left <= 0) return false;
                // Event-driven; the 1 s cap is only a safety net in case a notification is missed.
                if (WaitWake((int)Math.Min(left, 1000))) return false;
            }
        }

        void Problem(string msg)
        {
            if (msg == _lastProblem) return;
            Log.Warn(msg);
            _lastProblem = msg;
        }

        void SetStatus(string s) { Status = s; }
    }

    // ---------------------------------------------------------------- tray icon

    sealed class TrayContext : ApplicationContext
    {
        readonly NotifyIcon _icon;
        readonly System.Windows.Forms.Timer _timer;
        readonly Icon _onIcon, _offIcon;
        readonly ToolStripMenuItem _statusItem, _pauseItem;

        public TrayContext(Agent agent, Thread worker, string logPath, string addressText)
        {
            _onIcon = MakeIcon(Color.FromArgb(0, 120, 215));
            _offIcon = MakeIcon(Color.Gray);

            var menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("Starting...") { Enabled = false };
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Connect now", null, (s, e) => agent.ConnectNow());
            menu.Items.Add("Hand off to another device", null, (s, e) => agent.HandOff());
            _pauseItem = new ToolStripMenuItem("Pause auto-connect", null, (s, e) => { agent.Paused = !agent.Paused; _pauseItem.Checked = agent.Paused; });
            menu.Items.Add(_pauseItem);
            menu.Items.Add("Open log", null, (s, e) => { try { Process.Start("notepad.exe", "\"" + logPath + "\""); } catch { } });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => agent.Stop());

            _icon = new NotifyIcon { Icon = _offIcon, Text = "BlueSnap", ContextMenuStrip = menu, Visible = true };
            _icon.DoubleClick += (s, e) => agent.ConnectNow();

            _timer = new System.Windows.Forms.Timer { Interval = 1000 };
            _timer.Tick += (s, e) =>
            {
                if (!worker.IsAlive) { ExitThread(); return; }
                string text = "Speaker " + addressText + ": " + agent.Status;
                _statusItem.Text = text;
                _icon.Text = text.Length > 63 ? text.Substring(0, 63) : text;
                _icon.Icon = agent.Connected ? _onIcon : _offIcon;
            };
            _timer.Start();
        }

        static Icon MakeIcon(Color c)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var b = new SolidBrush(c))
                using (var p = new Pen(Color.White, 1.6f))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.FillEllipse(b, 1, 1, 14, 14);
                    // simple Bluetooth rune
                    g.DrawLines(p, new[] { new PointF(5, 5), new PointF(11, 10.5f), new PointF(8, 13), new PointF(8, 3), new PointF(11, 5.5f), new PointF(5, 11) });
                }
                IntPtr h = bmp.GetHicon();
                Icon tmp = Icon.FromHandle(h);
                Icon copy = (Icon)tmp.Clone();
                tmp.Dispose();
                Native.DestroyIcon(h);
                return copy;
            }
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _icon.Visible = false;
            _icon.Dispose();
            base.ExitThreadCore();
        }
    }

    // ---------------------------------------------------------------- entry point & CLI

    static class Program
    {
        const string AgentMutexName = @"Local\BlueSnap.Agent";

        [STAThread]
        static int Main(string[] args)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(exeDir, "config.ini");
            string logPath = Path.Combine(exeDir, "agent.log");
            Log.Echo = Native.GetConsoleWindow() != IntPtr.Zero;

            Config cfg;
            try { cfg = Config.Load(cfgPath); }
            catch (Exception ex)
            {
                Log.FilePath = logPath;
                Log.Warn("Invalid " + cfgPath + ": " + ex.Message);
                return 2;
            }

            if (args.Length > 0)
            {
                int rc = 1;
                var t = new Thread(() => { rc = Cli.Run(args, cfg); });
                t.SetApartmentState(ApartmentState.MTA);
                t.Start();
                t.Join();
                return rc;
            }

            Log.FilePath = logPath;
            Log.Verbose = cfg.VerboseLog;
            if (cfg.Address == 0)
            {
                Log.Warn("DeviceAddress is not set in " + cfgPath + ". Run 'bluesnap-cli list' to find it.");
                return 2;
            }

            bool created;
            using (var single = new Mutex(true, AgentMutexName, out created))
            {
                if (!created) { Log.Info("Another agent instance is already running; exiting."); return 0; }
                using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, Agent.StopEventName))
                {
                    stop.Reset();
                    var agent = new Agent(cfg, stop);
                    var worker = new Thread(agent.Run) { IsBackground = true, Name = "monitor" };
                    worker.SetApartmentState(ApartmentState.MTA);
                    worker.Start();
                    if (Log.Echo) Console.CancelKeyPress += (s, e) => { e.Cancel = true; agent.Stop(); };
                    if (cfg.ShowTrayIcon) Application.Run(new TrayContext(agent, worker, logPath, cfg.AddressText));
                    worker.Join();
                }
                single.ReleaseMutex();
            }
            return 0;
        }

        public static bool AgentRunning()
        {
            Mutex m;
            if (!Mutex.TryOpenExisting(AgentMutexName, out m)) return false;
            m.Dispose();
            return true;
        }
    }

    static class Cli
    {
        public static int Run(string[] args, Config cfg)
        {
            string cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
            int timeout = 15;
            Log.Echo = true; // CLI output may be redirected (no console window), still print log lines
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                if (a == "address" && i + 1 < args.Length) cfg.Address = Config.ParseAddress(args[++i]);
                else if (a == "timeout" && i + 1 < args.Length) timeout = int.Parse(args[++i]);
                else if (a == "verbose") Log.Verbose = true;
            }
            try
            {
                switch (cmd)
                {
                    case "list": return List();
                    case "status": return Status(cfg);
                    case "connect": return Connect(cfg, timeout);
                    case "disconnect": return Disconnect(cfg);
                    case "set-default": return SetDefault(cfg);
                    case "stop": return StopAgent();
                    default: Help(); return cmd == "help" || cmd == "?" || cmd == "h" ? 0 : 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 1;
            }
        }

        static void Help()
        {
            Console.WriteLine(
@"BlueSnap - keeps a paired Bluetooth speaker connected.

  bluesnap-cli                       run the agent in this console (Ctrl+C to stop)
  bluesnap-cli list                  list paired Bluetooth A2DP speakers/headphones and their state
  bluesnap-cli status [--address A]  detailed diagnostics for the configured (or given) speaker
  bluesnap-cli connect [--address A] [--timeout S]
                                     one connect attempt; exit code 0 only if A2DP becomes ACTIVE
  bluesnap-cli disconnect            drop the audio connection (a running agent reconnects it -
                                     use the tray's ""Hand off"" to keep it released)
  bluesnap-cli set-default           make the (connected) speaker the default output
  bluesnap-cli stop                  stop the background agent

Configuration: config.ini next to the executable. Log: agent.log next to the executable.");
        }

        static int List()
        {
            using (var audio = new BtAudio())
            {
                var eps = audio.FindEndpoints(null, DeviceState.All);
                if (eps.Count == 0) { Console.WriteLine("No Bluetooth A2DP audio endpoints found. Pair the speaker first."); return 1; }
                Console.WriteLine("{0,-18} {1,-28} {2,-8} {3}", "Address", "State", "Link", "Endpoint name");
                foreach (var ep in eps)
                {
                    string link = "?";
                    if (ep.AddressText != "?")
                    {
                        BLUETOOTH_DEVICE_INFO bi;
                        if (Native.TryGetDeviceInfo(Config.ParseAddress(ep.AddressText), out bi)) link = bi.fConnected != 0 ? "up" : "down";
                        else link = "unpaired";
                    }
                    Console.WriteLine("{0,-18} {1,-28} {2,-8} {3}", ep.AddressText, DeviceState.Name(ep.State), link, ep.Name);
                }
                Console.WriteLine();
                Console.WriteLine("NOT_PRESENT entries are leftovers of earlier pairings and are ignored.");
            }
            return 0;
        }

        static int Status(Config cfg)
        {
            if (cfg.Address == 0) { Console.WriteLine("No DeviceAddress configured; pass --address or edit config.ini."); return 1; }
            Console.WriteLine("Target speaker      : " + cfg.AddressText);
            Console.WriteLine("Agent running       : " + (Program.AgentRunning() ? "yes" : "no"));
            Console.WriteLine("Bluetooth adapter   : " + (Native.RadioPresent() ? "present" : "NOT FOUND / disabled"));

            BLUETOOTH_DEVICE_INFO bi;
            if (Native.TryGetDeviceInfo(cfg.Address, out bi))
            {
                Console.WriteLine("Device name         : " + bi.szName);
                Console.WriteLine("Paired (remembered) : " + (bi.fRemembered != 0) + "   authenticated: " + (bi.fAuthenticated != 0));
                Console.WriteLine("Bluetooth link (ACL): " + (bi.fConnected != 0 ? "connected" : "not connected"));
                Console.WriteLine("Last seen / used    : " + bi.stLastSeen + " / " + bi.stLastUsed);
            }
            else Console.WriteLine("Device              : not known to Windows (not paired)");

            int rc = 1;
            using (var audio = new BtAudio())
            {
                var eps = audio.FindEndpoints(cfg.Mac12, DeviceState.All);
                if (eps.Count == 0) Console.WriteLine("A2DP endpoint       : none");
                string def = audio.GetDefaultRenderId(ERole.eMultimedia);
                foreach (var ep in eps)
                {
                    Console.WriteLine("A2DP endpoint       : " + ep.Name + "  [" + DeviceState.Name(ep.State) + "]" +
                                      (string.Equals(def, ep.EndpointId, StringComparison.OrdinalIgnoreCase) ? "  (default output)" : ""));
                    Console.WriteLine("  endpoint id       : " + ep.EndpointId);
                    Console.WriteLine("  KS filter         : " + ep.FilterId);
                    if (ep.State == DeviceState.Active) rc = 0;
                }
            }
            Console.WriteLine();
            Console.WriteLine(rc == 0 ? "RESULT: speaker audio is CONNECTED to this PC." : "RESULT: speaker audio is NOT connected to this PC.");
            return rc;
        }

        static int Connect(Config cfg, int timeoutSec)
        {
            if (cfg.Address == 0) { Console.WriteLine("No DeviceAddress configured; pass --address or edit config.ini."); return 1; }
            using (var audio = new BtAudio())
            {
                var eps = audio.FindEndpoints(cfg.Mac12, DeviceState.Active | DeviceState.Unplugged);
                if (eps.Count == 0) { Console.WriteLine("No usable A2DP endpoint for " + cfg.AddressText + " (not paired, or endpoint disabled)."); return 1; }
                var ep = eps.Find(e => e.State == DeviceState.Active);
                if (ep != null) { Console.WriteLine("Already connected: " + ep.Name); return 0; }
                ep = eps[0];

                using (var m = new Mutex(false, Agent.AttemptMutexName))
                {
                    bool owned;
                    try { owned = m.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { owned = true; }
                    try
                    {
                        Console.WriteLine("Requesting connection to " + ep.Name + " ...");
                        var sw = Stopwatch.StartNew();
                        string err = audio.RequestReconnect(ep);
                        if (err != null) { Console.WriteLine("Driver rejected the request: " + err); return 1; }
                        Console.WriteLine("Driver accepted the request; waiting up to " + timeoutSec + " s for the A2DP endpoint ...");
                        while (sw.Elapsed.TotalSeconds < timeoutSec)
                        {
                            if (audio.GetEndpointState(ep.EndpointId) == DeviceState.Active)
                            {
                                Console.WriteLine("CONNECTED after " + sw.ElapsedMilliseconds + " ms.");
                                return 0;
                            }
                            Thread.Sleep(250);
                        }
                        Console.WriteLine("NOT connected within " + timeoutSec + " s (speaker off, out of range, or held by another device).");
                        return 1;
                    }
                    finally { if (owned) m.ReleaseMutex(); }
                }
            }
        }

        static int Disconnect(Config cfg)
        {
            using (var audio = new BtAudio())
            {
                var ep = audio.FindEndpoints(cfg.Mac12, DeviceState.Active).Find(e => true);
                if (ep == null) { Console.WriteLine("Speaker is not connected."); return 0; }
                string err = audio.RequestDisconnect(ep);
                if (err != null) { Console.WriteLine("Driver rejected the request: " + err); return 1; }
                for (int i = 0; i < 40 && audio.GetEndpointState(ep.EndpointId) == DeviceState.Active; i++) Thread.Sleep(250);
                bool gone = audio.GetEndpointState(ep.EndpointId) != DeviceState.Active;
                Console.WriteLine(gone ? "Disconnected." : "Disconnect requested, but the endpoint is still active.");
                return gone ? 0 : 1;
            }
        }

        static int SetDefault(Config cfg)
        {
            using (var audio = new BtAudio())
            {
                var ep = audio.FindEndpoints(cfg.Mac12, DeviceState.Active).Find(e => true);
                if (ep == null) { Console.WriteLine("Speaker is not connected."); return 1; }
                BtAudio.SetDefault(ep.EndpointId, cfg.SetAsDefaultCommunications);
                Console.WriteLine("Default output set to " + ep.Name);
                return 0;
            }
        }

        static int StopAgent()
        {
            EventWaitHandle ev;
            if (!EventWaitHandle.TryOpenExisting(Agent.StopEventName, out ev)) { Console.WriteLine("Agent is not running."); return 0; }
            using (ev) ev.Set();
            for (int i = 0; i < 50 && Program.AgentRunning(); i++) Thread.Sleep(100);
            Console.WriteLine(Program.AgentRunning() ? "Stop signalled, agent still shutting down." : "Agent stopped.");
            return 0;
        }
    }
}
