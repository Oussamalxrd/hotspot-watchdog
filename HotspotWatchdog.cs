// HotspotWatchdog.cs - Windows Mobile Hotspot tray watchdog
// Single file, C# 5 / .NET Framework 4.x. Build with build.cmd (no SDK needed).
//
// - Tray icon: green = ON, amber = starting / waiting, grey = OFF, red = can't start
// - Turn the hotspot on/off any time (menu, dashboard, Ctrl+Alt+H)
// - Watchdog: turns it back on when it drops (sleep, eduroam reconnect, Windows auto-off)
// - Devices list with IPs (click to copy), nicknames, ADB port check, join/leave notifications
// - Keeps the PC awake while devices are connected, edits SSID/password/band, starts with Windows
//
// Windows hotspot API (Windows.Networking.NetworkOperators) is reached through the CLR's built-in
// WinRT projection via reflection, so no Windows SDK / .winmd references are needed to compile.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace HotspotWatchdog
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\HotspotWatchdog.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Hotspot Watchdog is already running - look for it in the tray.",
                        "Hotspot Watchdog", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                bool startup = args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
                Application.Run(new TrayApp(startup));
                GC.KeepAlive(mutex);
            }
        }
    }

    // ------------------------------------------------------------------ settings

    enum LaunchMode { TurnOn = 0, RestoreLast = 1, LeaveAsIs = 2 }

    class Settings
    {
        public bool StartWithWindows;
        public LaunchMode Launch = LaunchMode.TurnOn;
        public int StartupDelaySec = 20;
        public bool Watchdog = true;
        public int IntervalSec = 15;
        public bool RestartOnUpstreamChange = true;
        public bool DisableAutoOff = true;
        public bool KeepAwake = true;
        public bool NotifyDropRestore = true;
        public bool NotifyDevices = true;
        public bool HotkeyEnabled = true;
        public string HotkeyKey = "H";
        public bool DesiredOn = true;
        public Dictionary<string, string> Nicknames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotspotWatchdog"); }
        }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var raw in File.ReadAllLines(FilePath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (k.StartsWith("nick.", StringComparison.OrdinalIgnoreCase))
                        {
                            if (v.Length > 0) s.Nicknames[k.Substring(5).ToLowerInvariant()] = v;
                            continue;
                        }
                        switch (k)
                        {
                            case "Launch": s.Launch = (LaunchMode)Clamp(Int(v, 0), 0, 2); break;
                            case "StartupDelaySec": s.StartupDelaySec = Clamp(Int(v, 20), 0, 300); break;
                            case "Watchdog": s.Watchdog = Bool(v); break;
                            case "IntervalSec": s.IntervalSec = Clamp(Int(v, 15), 5, 300); break;
                            case "RestartOnUpstreamChange": s.RestartOnUpstreamChange = Bool(v); break;
                            case "DisableAutoOff": s.DisableAutoOff = Bool(v); break;
                            case "KeepAwake": s.KeepAwake = Bool(v); break;
                            case "NotifyDropRestore": s.NotifyDropRestore = Bool(v); break;
                            case "NotifyDevices": s.NotifyDevices = Bool(v); break;
                            case "HotkeyEnabled": s.HotkeyEnabled = Bool(v); break;
                            case "HotkeyKey": if (IsValidKey(v)) s.HotkeyKey = v.ToUpperInvariant(); break;
                            case "DesiredOn": s.DesiredOn = Bool(v); break;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("Couldn't read settings: " + ex.Message); }
            s.StartWithWindows = Autostart.IsEnabled();
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var l = new List<string>();
                l.Add("# Hotspot Watchdog settings");
                l.Add("Launch=" + (int)Launch);
                l.Add("StartupDelaySec=" + StartupDelaySec);
                l.Add("Watchdog=" + B(Watchdog));
                l.Add("IntervalSec=" + IntervalSec);
                l.Add("RestartOnUpstreamChange=" + B(RestartOnUpstreamChange));
                l.Add("DisableAutoOff=" + B(DisableAutoOff));
                l.Add("KeepAwake=" + B(KeepAwake));
                l.Add("NotifyDropRestore=" + B(NotifyDropRestore));
                l.Add("NotifyDevices=" + B(NotifyDevices));
                l.Add("HotkeyEnabled=" + B(HotkeyEnabled));
                l.Add("HotkeyKey=" + HotkeyKey);
                l.Add("DesiredOn=" + B(DesiredOn));
                foreach (var kv in Nicknames) l.Add("nick." + kv.Key + "=" + kv.Value);
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, l);
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Write("Couldn't save settings: " + ex.Message); }
        }

        public static bool IsValidKey(string v)
        {
            if (string.IsNullOrEmpty(v)) return false;
            v = v.ToUpperInvariant();
            if (v.Length == 1 && v[0] >= 'A' && v[0] <= 'Z') return true;
            int n;
            return v.Length >= 2 && v[0] == 'F' && int.TryParse(v.Substring(1), out n) && n >= 1 && n <= 12;
        }

        static string B(bool b) { return b ? "1" : "0"; }
        static bool Bool(string v) { return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase); }
        static int Int(string v, int def) { int n; return int.TryParse(v, out n) ? n : def; }
        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "HotspotWatchdog";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\" --startup");
                else if (k.GetValue(Name) != null) k.DeleteValue(Name, false);
            }
        }
    }

    static class Log
    {
        static readonly object Sync = new object();
        public static event Action<string> Written;
        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg;
            lock (Sync)
            {
                try
                {
                    Directory.CreateDirectory(Settings.Dir);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 512 * 1024)
                    {
                        var lines = File.ReadAllLines(FilePath);
                        File.WriteAllLines(FilePath, lines.Skip(Math.Max(0, lines.Length - 2000)));
                    }
                    File.AppendAllText(FilePath, line + Environment.NewLine);
                }
                catch { }
            }
            var h = Written;
            if (h != null) { try { h(line); } catch { } }
        }
    }

    // ------------------------------------------------------------------ WinRT bridge (reflection)

    static class WinRT
    {
        static MethodInfo asTaskOperation, asTaskAction;

        public static Type T(string ns, string name)
        {
            try { return Type.GetType(ns + "." + name + ", " + ns + ", ContentType=WindowsRuntime", false); }
            catch { return null; }
        }

        static void EnsureExtensions()
        {
            if (asTaskOperation != null && asTaskAction != null) return;
            Assembly asm = null;
            try { asm = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); }
            catch
            {
                string p = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Runtime.WindowsRuntime.dll");
                asm = Assembly.LoadFrom(p);
            }
            var ext = asm.GetType("System.WindowsRuntimeSystemExtensions", true);
            foreach (var m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "AsTask") continue;
                var ps = m.GetParameters();
                if (ps.Length != 1) continue;
                string pn = ps[0].ParameterType.Name;
                if (pn == "IAsyncOperation`1" && m.IsGenericMethodDefinition) asTaskOperation = m;
                else if (pn == "IAsyncAction" && !m.IsGenericMethodDefinition) asTaskAction = m;
            }
            if (asTaskOperation == null || asTaskAction == null)
                throw new PlatformNotSupportedException("WinRT bridge not found in .NET Framework.");
        }

        public static object Await(object asyncOp, Type resultType, int timeoutMs)
        {
            EnsureExtensions();
            var task = (Task)Invoke(asTaskOperation.MakeGenericMethod(resultType), null, new object[] { asyncOp });
            if (!task.Wait(timeoutMs)) throw new TimeoutException("Windows didn't answer in time.");
            return task.GetType().GetProperty("Result").GetValue(task, null);
        }

        public static void AwaitAction(object asyncAction, int timeoutMs)
        {
            EnsureExtensions();
            var task = (Task)Invoke(asTaskAction, null, new object[] { asyncAction });
            if (!task.Wait(timeoutMs)) throw new TimeoutException("Windows didn't answer in time.");
        }

        public static object Get(object o, Type t, string prop)
        {
            var p = t.GetProperty(prop);
            if (p == null) throw new MissingMemberException(t.Name, prop);
            try { return p.GetValue(o, null); }
            catch (TargetInvocationException e) { throw Unwrap(e); }
        }

        public static void Set(object o, Type t, string prop, object value)
        {
            var p = t.GetProperty(prop);
            if (p == null) throw new MissingMemberException(t.Name, prop);
            try { p.SetValue(o, value, null); }
            catch (TargetInvocationException e) { throw Unwrap(e); }
        }

        public static bool Has(Type t, string method, Type[] sig)
        {
            return t != null && t.GetMethod(method, sig) != null;
        }

        public static object Call(object o, Type t, string method, Type[] sig, params object[] args)
        {
            var m = t.GetMethod(method, sig);
            if (m == null) throw new MissingMethodException(t.Name, method);
            return Invoke(m, o, args);
        }

        static object Invoke(MethodInfo m, object o, object[] args)
        {
            try { return m.Invoke(o, args); }
            catch (TargetInvocationException e) { throw Unwrap(e); }
        }

        // Enumerates a projected IReadOnlyList<T> / IEnumerable<T> coming back from WinRT.
        public static List<object> Items(object list, Type elementType)
        {
            var result = new List<object>();
            if (list == null) return result;
            IEnumerator e = null;
            try
            {
                var ienum = typeof(IEnumerable<>).MakeGenericType(elementType);
                e = (IEnumerator)ienum.GetMethod("GetEnumerator").Invoke(list, null);
            }
            catch
            {
                var plain = list as IEnumerable;
                if (plain != null) e = plain.GetEnumerator();
            }
            if (e == null) return result;
            while (e.MoveNext()) result.Add(e.Current);
            return result;
        }

        public static Exception Unwrap(Exception e)
        {
            while (true)
            {
                var tie = e as TargetInvocationException;
                if (tie != null && tie.InnerException != null) { e = tie.InnerException; continue; }
                var ae = e as AggregateException;
                if (ae != null && ae.InnerException != null) { e = ae.InnerException; continue; }
                return e;
            }
        }

        public static string Message(Exception e)
        {
            e = Unwrap(e);
            var com = e as COMException;
            string msg = (e.Message ?? "").Trim();
            if (com != null) msg += " (0x" + com.ErrorCode.ToString("X8") + ")";
            return msg.Length == 0 ? e.GetType().Name : msg;
        }
    }

    // ------------------------------------------------------------------ hotspot model

    class ClientInfo
    {
        public string Mac;   // aa:bb:cc:dd:ee:ff
        public string Host;
        public string Ip;
        public bool? Adb;    // TCP 5555 reachable (only checked while the dashboard is open)
    }

    class HotspotInfo
    {
        public string State = "Unknown";   // On, Off, InTransition, NoInternet, Error, Unknown
        public bool IsOn { get { return State == "On"; } }
        public string Upstream;
        public int ClientCount, MaxClients;
        public List<ClientInfo> Clients = new List<ClientInfo>();
        public bool ClientsKnown;            // true = list came from Windows (safe to diff for notifications)
        public string Ssid, Passphrase, Band;
        public int BandValue = -1;
        public string Capability;
        public string Error;
        public DateTime Time = DateTime.Now;

        // filled in by the watchdog
        public bool DesiredOn;
        public DateTime? OnSince, LastDrop;
        public int RestartsToday, Failures;
        public DateTime NextAttempt;
        public List<ClientInfo> Joined, Left;
    }

    static class Hotspot
    {
        const string NsNO = "Windows.Networking.NetworkOperators";
        const string NsConn = "Windows.Networking.Connectivity";
        const int TimeoutMs = 30000;

        static Type tNetInfo, tProfile, tOpResult, tClient, tHostName, tApConfig, tBand, tMgr;
        static List<int> supportedBands;
        static bool bandsProbed;

        public static List<int> SupportedBands { get { return supportedBands; } }

        static void Init()
        {
            if (tMgr != null) return;
            tNetInfo = WinRT.T(NsConn, "NetworkInformation");
            tProfile = WinRT.T(NsConn, "ConnectionProfile");
            tOpResult = WinRT.T(NsNO, "NetworkOperatorTetheringOperationResult");
            tClient = WinRT.T(NsNO, "NetworkOperatorTetheringClient");
            tApConfig = WinRT.T(NsNO, "NetworkOperatorTetheringAccessPointConfiguration");
            tBand = WinRT.T(NsNO, "TetheringWiFiBand");
            tHostName = WinRT.T("Windows.Networking", "HostName");
            var mgr = WinRT.T(NsNO, "NetworkOperatorTetheringManager");
            if (tNetInfo == null || tProfile == null || tOpResult == null || mgr == null)
                throw new PlatformNotSupportedException("The Windows mobile hotspot API isn't available on this PC.");
            tMgr = mgr;
        }

        static object Profile(out string name)
        {
            var p = WinRT.Call(null, tNetInfo, "GetInternetConnectionProfile", Type.EmptyTypes);
            name = null;
            if (p != null)
            {
                try { name = (string)WinRT.Get(p, tProfile, "ProfileName"); } catch { }
                if (string.IsNullOrEmpty(name)) name = "(unnamed network)";
            }
            return p;
        }

        static object Manager(object profile)
        {
            return WinRT.Call(null, tMgr, "CreateFromConnectionProfile", new[] { tProfile }, profile);
        }

        public static HotspotInfo Query(bool details)
        {
            var info = new HotspotInfo();
            try
            {
                Init();
                string up;
                var prof = Profile(out up);
                info.Upstream = up;
                if (prof == null) { info.State = "NoInternet"; return info; }

                object mgr;
                try { mgr = Manager(prof); }
                catch (Exception ex)
                {
                    info.State = "Error";
                    info.Error = "Can't share \"" + up + "\": " + WinRT.Message(ex);
                    return info;
                }

                info.State = WinRT.Get(mgr, tMgr, "TetheringOperationalState").ToString();
                if (info.State == "Unknown") info.State = "Off";
                info.ClientCount = Convert.ToInt32(WinRT.Get(mgr, tMgr, "ClientCount"));
                info.MaxClients = Convert.ToInt32(WinRT.Get(mgr, tMgr, "MaxClientCount"));

                if (!info.IsOn)
                {
                    try
                    {
                        info.Capability = WinRT.Call(null, tMgr, "GetTetheringCapabilityFromConnectionProfile",
                            new[] { tProfile }, prof).ToString();
                    }
                    catch { }
                }

                ReadConfig(mgr, info);

                if (info.IsOn)
                {
                    try { ReadClients(mgr, info); }
                    catch (Exception ex) { info.ClientsKnown = false; info.Clients.Clear(); Debug.WriteLine(ex.Message); }
                    if (!info.ClientsKnown) ClientsFromArp(info);
                    FillIps(info);
                    if (details)
                        foreach (var c in info.Clients)
                            if (c.Ip != null) c.Adb = TcpProbe(c.Ip, 5555, 300);
                }
            }
            catch (Exception ex)
            {
                info.State = "Error";
                info.Error = WinRT.Message(ex);
            }
            return info;
        }

        static void ReadConfig(object mgr, HotspotInfo info)
        {
            if (tApConfig == null) return;
            try
            {
                var cfg = WinRT.Call(mgr, tMgr, "GetCurrentAccessPointConfiguration", Type.EmptyTypes);
                info.Ssid = (string)WinRT.Get(cfg, tApConfig, "Ssid");
                info.Passphrase = (string)WinRT.Get(cfg, tApConfig, "Passphrase");
                if (tBand != null && tApConfig.GetProperty("Band") != null)
                {
                    var b = WinRT.Get(cfg, tApConfig, "Band");
                    info.BandValue = Convert.ToInt32(b);
                    info.Band = BandName(info.BandValue);
                    if (!bandsProbed) ProbeBands(cfg);
                }
            }
            catch { }
        }

        static void ProbeBands(object cfg)
        {
            bandsProbed = true;
            var list = new List<int> { 0 };
            if (!WinRT.Has(tApConfig, "IsBandSupported", new[] { tBand }))
            {
                list.Add(1); list.Add(2);
                supportedBands = list;
                return;
            }
            for (int b = 1; b <= 3; b++)
            {
                try
                {
                    if ((bool)WinRT.Call(cfg, tApConfig, "IsBandSupported", new[] { tBand }, Enum.ToObject(tBand, b)))
                        list.Add(b);
                }
                catch { if (b < 3) list.Add(b); }
            }
            supportedBands = list;
        }

        public static string BandName(int v)
        {
            switch (v)
            {
                case 0: return "Auto";
                case 1: return "2.4 GHz";
                case 2: return "5 GHz";
                case 3: return "6 GHz";
                default: return null;
            }
        }

        static void ReadClients(object mgr, HotspotInfo info)
        {
            if (tClient == null || !WinRT.Has(tMgr, "GetTetheringClients", Type.EmptyTypes)) return;
            var list = WinRT.Call(mgr, tMgr, "GetTetheringClients", Type.EmptyTypes);
            foreach (var c in WinRT.Items(list, tClient))
            {
                var ci = new ClientInfo();
                try { ci.Mac = NormMac((string)WinRT.Get(c, tClient, "MacAddress")); } catch { }
                if (tHostName != null)
                {
                    try
                    {
                        foreach (var h in WinRT.Items(WinRT.Get(c, tClient, "HostNames"), tHostName))
                        {
                            string type = WinRT.Get(h, tHostName, "Type").ToString();
                            string raw = (string)WinRT.Get(h, tHostName, "RawName");
                            if (type == "Ipv4" && ci.Ip == null) ci.Ip = raw;
                            else if (type == "DomainName" && ci.Host == null) ci.Host = raw;
                        }
                    }
                    catch { }
                }
                info.Clients.Add(ci);
            }
            info.ClientsKnown = true;
        }

        // Fallback when Windows won't list clients: ARP entries on the hotspot adapter (no names, may lag).
        static void ClientsFromArp(HotspotInfo info)
        {
            int idx = HotspotIfIndex();
            if (idx < 0) return;
            foreach (var r in Arp.Table())
            {
                if (r.Index != idx || r.Type != 3) continue;   // 3 = dynamic
                if (r.Ip.EndsWith(".255")) continue;
                info.Clients.Add(new ClientInfo { Mac = r.Mac, Ip = r.Ip });
            }
        }

        static void FillIps(HotspotInfo info)
        {
            if (info.Clients.All(c => c.Ip != null)) return;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Arp.Table())
            {
                if (r.Type == 2) continue;   // invalid
                string cur;
                if (!map.TryGetValue(r.Mac, out cur) || r.Ip.StartsWith("192.168.137.")) map[r.Mac] = r.Ip;
            }
            foreach (var c in info.Clients)
            {
                string ip;
                if (c.Ip == null && c.Mac != null && map.TryGetValue(c.Mac, out ip)) c.Ip = ip;
            }
        }

        static int HotspotIfIndex()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    var props = ni.GetIPProperties();
                    bool match = (ni.Description ?? "").IndexOf("Wi-Fi Direct Virtual", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match)
                        foreach (var ua in props.UnicastAddresses)
                            if (ua.Address.ToString() == "192.168.137.1") match = true;
                    if (!match) continue;
                    var v4 = props.GetIPv4Properties();
                    if (v4 != null) return v4.Index;
                }
            }
            catch { }
            return -1;
        }

        static bool TcpProbe(string ip, int port, int ms)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(ip, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(ms)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public static string NormMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            return mac.Trim().Replace('-', ':').ToLowerInvariant();
        }

        public static string Start() { return Op("StartTetheringAsync"); }
        public static string Stop() { return Op("StopTetheringAsync"); }

        static string Op(string method)
        {
            try
            {
                Init();
                string up;
                var prof = Profile(out up);
                if (prof == null) return "No internet connection to share.";
                var mgr = Manager(prof);
                var op = WinRT.Call(mgr, tMgr, method, Type.EmptyTypes);
                var res = WinRT.Await(op, tOpResult, TimeoutMs);
                string status = WinRT.Get(res, tOpResult, "Status").ToString();
                if (status == "Success") return null;
                string extra = null;
                try { extra = (string)WinRT.Get(res, tOpResult, "AdditionalErrorMessage"); } catch { }
                return Friendly(status) + (string.IsNullOrEmpty(extra) ? "" : " (" + extra + ")");
            }
            catch (Exception ex) { return WinRT.Message(ex); }
        }

        static string Friendly(string status)
        {
            switch (status)
            {
                case "WiFiDeviceOff": return "Wi-Fi is off.";
                case "OperationInProgress": return "Windows is already switching the hotspot.";
                case "NetworkLimitedConnectivity": return "The shared network has limited connectivity.";
                case "MobileBroadbandDeviceOff": return "Mobile broadband device is off.";
                case "BluetoothDeviceOff": return "Bluetooth is off.";
                case "EntitlementCheckFailure":
                case "EntitlementCheckTimeout": return "The network refused tethering (" + status + ").";
                default: return "Windows said: " + status;
            }
        }

        // Windows "power saving: turn off when no devices are connected".
        // Returns 1 = changed, 0 = already as wanted, -1 = failed (error set).
        public static int SetNoConnectionsTimeout(bool enabled, out string error)
        {
            error = null;
            try
            {
                Init();
                if (!WinRT.Has(tMgr, "IsNoConnectionsTimeoutEnabled", Type.EmptyTypes))
                {
                    error = "not supported on this Windows version";
                    return -1;
                }
                var cur = WinRT.Call(null, tMgr, "IsNoConnectionsTimeoutEnabled", Type.EmptyTypes);
                if (cur is bool && (bool)cur == enabled) return 0;
                WinRT.Call(null, tMgr, enabled ? "EnableNoConnectionsTimeout" : "DisableNoConnectionsTimeout", Type.EmptyTypes);
                return 1;
            }
            catch (Exception ex) { error = WinRT.Message(ex); return -1; }
        }

        public static string Configure(string ssid, string pass, int band)
        {
            try
            {
                Init();
                if (tApConfig == null) return "Hotspot settings API not available.";
                string up;
                var prof = Profile(out up);
                if (prof == null) return "Windows needs an internet connection to change hotspot settings from here.";
                var mgr = Manager(prof);
                var cfg = WinRT.Call(mgr, tMgr, "GetCurrentAccessPointConfiguration", Type.EmptyTypes);
                WinRT.Set(cfg, tApConfig, "Ssid", ssid);
                WinRT.Set(cfg, tApConfig, "Passphrase", pass);
                if (band >= 0 && tBand != null && tApConfig.GetProperty("Band") != null)
                    WinRT.Set(cfg, tApConfig, "Band", Enum.ToObject(tBand, band));
                var act = WinRT.Call(mgr, tMgr, "ConfigureAccessPointAsync", new[] { tApConfig }, cfg);
                WinRT.AwaitAction(act, TimeoutMs);
                return null;
            }
            catch (Exception ex) { return WinRT.Message(ex); }
        }
    }

    class ArpRow
    {
        public int Index, Type;
        public string Mac, Ip;
    }

    static class Arp
    {
        [DllImport("iphlpapi.dll")]
        static extern int GetIpNetTable(IntPtr pIpNetTable, ref int pdwSize, bool bOrder);

        public static List<ArpRow> Table()
        {
            var rows = new List<ArpRow>();
            try
            {
                int size = 0;
                int r = GetIpNetTable(IntPtr.Zero, ref size, false);
                if (r != 122 || size <= 0) return rows;   // 122 = ERROR_INSUFFICIENT_BUFFER
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (GetIpNetTable(buf, ref size, false) != 0) return rows;
                    int n = Marshal.ReadInt32(buf);
                    IntPtr row = IntPtr.Add(buf, 4);
                    for (int i = 0; i < n; i++)
                    {
                        // MIB_IPNETROW: index(4) physLen(4) phys[8] addr(4) type(4) = 24 bytes
                        int index = Marshal.ReadInt32(row, 0);
                        int macLen = Marshal.ReadInt32(row, 4);
                        int addr = Marshal.ReadInt32(row, 16);
                        int type = Marshal.ReadInt32(row, 20);
                        if (macLen == 6)
                        {
                            var sb = new StringBuilder();
                            for (int j = 0; j < 6; j++)
                            {
                                if (j > 0) sb.Append(':');
                                sb.Append(Marshal.ReadByte(row, 8 + j).ToString("x2"));
                            }
                            string mac = sb.ToString();
                            if (mac != "00:00:00:00:00:00" && mac != "ff:ff:ff:ff:ff:ff" && !mac.StartsWith("01:00:5e"))
                                rows.Add(new ArpRow
                                {
                                    Index = index,
                                    Type = type,
                                    Mac = mac,
                                    Ip = new IPAddress(BitConverter.GetBytes(addr)).ToString()
                                });
                        }
                        row = IntPtr.Add(row, 24);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { }
            return rows;
        }
    }

    // ------------------------------------------------------------------ tray app

    class TrayApp : ApplicationContext
    {
        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint esFlags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

        public readonly Settings S;
        readonly NotifyIcon tray;
        readonly Control invoker;
        readonly System.Windows.Forms.Timer timer, debounce, firstCheck;
        readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        readonly Thread worker;
        readonly Icon icoOn, icoOff, icoBusy, icoErr;
        ToolStripMenuItem miStatus, miToggle, miRestart, miDevices, miWatchdog;
        HotkeyWindow hotkey;
        DashboardForm dashboard;
        SettingsForm settingsForm;
        bool awakeHeld, exiting;

        // shared with the worker
        volatile bool desiredOn;
        volatile bool detailsWanted;
        volatile HotspotInfo last;
        int checkPending;

        // worker-only state
        bool initialStart, everOn, clientsBaseline;
        int failures, restartsToday;
        DateTime nextAttempt = DateTime.MinValue, restartsDay = DateTime.Today;
        DateTime? onSince, lastDrop, downSince;
        string lastUpstream;
        Dictionary<string, ClientInfo> knownClients = new Dictionary<string, ClientInfo>();

        public Icon AppIcon { get { return icoOn; } }
        public HotspotInfo Last { get { return last; } }
        public bool DesiredOn { get { return desiredOn; } }

        public TrayApp(bool launchedAtStartup)
        {
            S = Settings.Load();
            invoker = new Control();
            invoker.CreateControl();
            var force = invoker.Handle;

            icoOn = Icons.Make(Color.FromArgb(34, 160, 84));
            icoOff = Icons.Make(Color.FromArgb(128, 128, 128));
            icoBusy = Icons.Make(Color.FromArgb(224, 150, 0));
            icoErr = Icons.Make(Color.FromArgb(210, 58, 58));

            switch (S.Launch)
            {
                case LaunchMode.TurnOn: desiredOn = true; break;
                case LaunchMode.RestoreLast: desiredOn = S.DesiredOn; break;
                default: desiredOn = false; break;   // adopts the current state at the first check
            }
            initialStart = desiredOn;
            if (S.Launch == LaunchMode.TurnOn && !S.DesiredOn) { S.DesiredOn = true; S.Save(); }

            tray = new NotifyIcon();
            tray.Icon = icoBusy;
            tray.Text = "Hotspot Watchdog - checking...";
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowDashboard(); };
            tray.Visible = true;

            Log.Write("Started" + (launchedAtStartup ? " with Windows" : "") + " - launch mode " + S.Launch);
            if (S.StartWithWindows) { try { Autostart.Set(true); } catch { } }   // keeps the path current if the exe moved

            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "Hotspot worker";
            worker.Start();

            if (S.DisableAutoOff) queue.Add(ApplyAutoOff);

            hotkey = new HotkeyWindow();
            hotkey.Pressed += () => Toggle(true);
            RegisterHotkey();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = S.IntervalSec * 1000;
            timer.Tick += (s, e) => RequestCheck();

            debounce = new System.Windows.Forms.Timer();
            debounce.Tick += (s, e) => { debounce.Stop(); RequestCheck(); };

            firstCheck = new System.Windows.Forms.Timer();
            firstCheck.Interval = launchedAtStartup ? Math.Max(1, S.StartupDelaySec) * 1000 : 300;
            firstCheck.Tick += (s, e) => { firstCheck.Stop(); RequestCheck(); timer.Start(); };
            firstCheck.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            miStatus = new ToolStripMenuItem("Hotspot: checking...");
            miStatus.Enabled = false;
            miToggle = new ToolStripMenuItem("Turn hotspot off", null, (s, e) => Toggle(false));
            miToggle.Font = new Font(m.Font, FontStyle.Bold);
            miRestart = new ToolStripMenuItem("Restart hotspot", null, (s, e) => Restart());
            miDevices = new ToolStripMenuItem("Devices");
            miDevices.DropDownItems.Add(new ToolStripMenuItem("No devices connected") { Enabled = false });
            miWatchdog = new ToolStripMenuItem("Auto-restart when it drops", null, (s, e) => SetWatchdog(!S.Watchdog));
            miWatchdog.Checked = S.Watchdog;
            m.Items.AddRange(new ToolStripItem[]
            {
                miStatus,
                new ToolStripSeparator(),
                miToggle,
                miRestart,
                miDevices,
                new ToolStripSeparator(),
                miWatchdog,
                new ToolStripMenuItem("Open dashboard", null, (s, e) => ShowDashboard()),
                new ToolStripMenuItem("Settings...", null, (s, e) => ShowSettings()),
                new ToolStripMenuItem("Windows hotspot settings", null, (s, e) => OpenUri("ms-settings:network-mobilehotspot")),
                new ToolStripMenuItem("Open log", null, (s, e) => OpenLog()),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Exit (hotspot stays as it is)", null, (s, e) => ExitApp())
            });
            return m;
        }

        // ---------------- worker

        void WorkerLoop()
        {
            foreach (var a in queue.GetConsumingEnumerable())
            {
                try { a(); }
                catch (Exception ex) { Log.Write("Error: " + WinRT.Message(ex)); }
            }
        }

        void Enqueue(Action a)
        {
            try { if (!queue.IsAddingCompleted) queue.Add(a); } catch (InvalidOperationException) { }
        }

        public void RequestCheck()
        {
            if (Interlocked.CompareExchange(ref checkPending, 1, 0) == 0) Enqueue(DoCheck);
        }

        void Post(Action a)
        {
            try { if (!exiting && invoker.IsHandleCreated) invoker.BeginInvoke(a); } catch { }
        }

        void SetDesired(bool v)
        {
            if (desiredOn == v) return;
            desiredOn = v;
            Post(() => { S.DesiredOn = v; S.Save(); });
        }

        void DoCheck()
        {
            Interlocked.Exchange(ref checkPending, 0);
            var info = Hotspot.Query(detailsWanted);
            DateTime now = DateTime.Now;
            if (restartsDay != DateTime.Today) { restartsDay = DateTime.Today; restartsToday = 0; }

            // Turned on outside the app (Windows settings / quick settings): follow it.
            if (info.IsOn && !desiredOn)
            {
                SetDesired(true);
                Log.Write("Hotspot is on (switched on outside the app) - watching it");
            }

            bool wasOn = last != null && last.IsOn;
            if (wasOn && !info.IsOn && info.State != "InTransition" && desiredOn)
            {
                lastDrop = now;
                downSince = now;
                Log.Write("Hotspot dropped (" + Describe(info) + ")");
            }

            // Shared network changed while the hotspot is on -> restart so clients get internet again.
            if (info.IsOn && desiredOn && S.RestartOnUpstreamChange && lastUpstream != null && info.Upstream != null
                && !string.Equals(lastUpstream, info.Upstream, StringComparison.Ordinal))
            {
                Log.Write("Shared network changed: " + lastUpstream + " -> " + info.Upstream + " - restarting hotspot");
                Hotspot.Stop();
                Thread.Sleep(1500);
                string e1 = Hotspot.Start();
                if (e1 != null) Log.Write("Restart failed: " + e1);
                else restartsToday++;
                info = Hotspot.Query(detailsWanted);
            }
            if (info.Upstream != null) lastUpstream = info.Upstream;

            // Watchdog (and the first start at launch).
            if (desiredOn && info.State == "Off" && info.Upstream != null && (S.Watchdog || initialStart) && now >= nextAttempt)
            {
                bool recovery = everOn;
                Log.Write(recovery ? "Restarting hotspot (attempt " + (failures + 1) + ")" : "Turning hotspot on");
                string err = Hotspot.Start();
                if (err == null)
                {
                    initialStart = false;
                    if (recovery)
                    {
                        restartsToday++;
                        string took = downSince.HasValue ? " after " + Fmt.Span(DateTime.Now - downSince.Value) : "";
                        Log.Write("Hotspot restored" + took);
                        if (S.NotifyDropRestore)
                        {
                            string msg = "It dropped and was turned back on" + took + ".";
                            Post(() => Notify("Hotspot restored", msg, ToolTipIcon.Info));
                        }
                    }
                    failures = 0;
                    nextAttempt = DateTime.MinValue;
                    downSince = null;
                    info = Hotspot.Query(detailsWanted);
                }
                else
                {
                    failures++;
                    int delay = Math.Min(S.IntervalSec * (1 << Math.Min(failures - 1, 5)), 300);
                    nextAttempt = DateTime.Now.AddSeconds(delay);
                    info.Error = err;
                    Log.Write("Couldn't start: " + err + " - next try in " + delay + " s");
                    if (failures == 2 && S.NotifyDropRestore)
                    {
                        string msg = err + " Retrying automatically.";
                        Post(() => Notify("Hotspot is down", msg, ToolTipIcon.Warning));
                    }
                    if (!S.Watchdog) initialStart = false;
                }
            }

            if (info.IsOn)
            {
                everOn = true;
                if (!onSince.HasValue) onSince = now;
                failures = 0;
                downSince = null;
            }
            else if (info.State != "InTransition") onSince = null;

            // Device join / leave (only when Windows gave us a real list).
            if (info.IsOn && info.ClientsKnown)
            {
                var current = new Dictionary<string, ClientInfo>();
                foreach (var c in info.Clients) if (c.Mac != null) current[c.Mac] = c;
                if (clientsBaseline)
                {
                    info.Joined = current.Where(kv => !knownClients.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                    info.Left = knownClients.Where(kv => !current.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
                }
                knownClients = current;
                clientsBaseline = true;
            }
            else if (!info.IsOn)
            {
                knownClients.Clear();
                clientsBaseline = false;
            }

            info.DesiredOn = desiredOn;
            info.OnSince = onSince;
            info.LastDrop = lastDrop;
            info.RestartsToday = restartsToday;
            info.Failures = failures;
            info.NextAttempt = nextAttempt;
            last = info;
            Post(() => UpdateUi(info));
        }

        static string Describe(HotspotInfo i)
        {
            if (i.State == "NoInternet") return "no internet to share";
            return i.Error ?? i.State;
        }

        void DoTurnOn()
        {
            SetDesired(true);
            failures = 0;
            nextAttempt = DateTime.MinValue;
            Log.Write("Turning hotspot on (manual)");
            string err = Hotspot.Start();
            if (err != null)
            {
                Log.Write("Couldn't turn on: " + err);
                string msg = err + (S.Watchdog ? " It will turn on as soon as possible." : "");
                Post(() => Notify("Couldn't turn the hotspot on", msg, ToolTipIcon.Warning));
            }
            DoCheck();
        }

        void DoTurnOff()
        {
            SetDesired(false);
            Log.Write("Turning hotspot off (manual)");
            var before = last;
            string err = Hotspot.Stop();
            if (err != null && before != null && before.IsOn)
            {
                Log.Write("Couldn't turn off: " + err);
                Post(() => Notify("Couldn't turn the hotspot off", err, ToolTipIcon.Warning));
            }
            DoCheck();
        }

        void DoRestart()
        {
            Log.Write("Restarting hotspot (manual)");
            SetDesired(true);
            Hotspot.Stop();
            Thread.Sleep(1500);
            string err = Hotspot.Start();
            if (err != null) Log.Write("Restart failed: " + err);
            DoCheck();
        }

        void ApplyAutoOff()
        {
            string err;
            int r = Hotspot.SetNoConnectionsTimeout(false, out err);
            if (r == 1) Log.Write("Disabled Windows' \"turn off when no devices are connected\"");
            else if (r < 0) Log.Write("Couldn't disable Windows hotspot auto-off (" + err + "). The watchdog still restarts it; you can also switch off 'Power saving' in Windows hotspot settings.");
        }

        void DoConfigure(string ssid, string pass, int band)
        {
            var before = Hotspot.Query(false);
            string err = Hotspot.Configure(ssid, pass, band);
            if (err != null)
            {
                Log.Write("Couldn't save hotspot network settings: " + err);
                Post(() => Notify("Hotspot settings not saved", err, ToolTipIcon.Warning));
                DoCheck();
                return;
            }
            Log.Write("Hotspot network saved: \"" + ssid + "\"" + (band >= 0 ? ", band " + Hotspot.BandName(band) : ""));
            bool wasOn = before.IsOn;
            if (wasOn)
            {
                Hotspot.Stop();
                Thread.Sleep(1500);
                string e2 = Hotspot.Start();
                if (e2 != null) Log.Write("Restart after settings change failed: " + e2);
            }
            Post(() => Notify("Hotspot settings saved",
                wasOn ? "Restarted with the new settings - devices need to reconnect if you changed name or password."
                      : "They apply the next time it starts.", ToolTipIcon.Info));
            DoCheck();
        }

        // ---------------- UI-thread actions

        public void Toggle(bool fromHotkey)
        {
            tray.Icon = icoBusy;
            Enqueue(() =>
            {
                var l = last;
                bool turnOff = desiredOn || (l != null && l.IsOn);
                if (fromHotkey) Post(() => Notify("Hotspot", turnOff ? "Turning off..." : "Turning on...", ToolTipIcon.None));
                if (turnOff) DoTurnOff(); else DoTurnOn();
            });
        }

        public void Restart()
        {
            tray.Icon = icoBusy;
            Enqueue(DoRestart);
        }

        public void SetWatchdog(bool on)
        {
            S.Watchdog = on;
            S.Save();
            miWatchdog.Checked = on;
            Log.Write("Watchdog " + (on ? "enabled" : "disabled"));
            if (dashboard != null && !dashboard.IsDisposed && last != null) dashboard.UpdateInfo(last);
            RequestCheck();
        }

        public void ApplySettings(bool enforceAutoOff, bool apChanged, string ssid, string pass, int band)
        {
            S.Save();
            timer.Interval = Math.Max(5, S.IntervalSec) * 1000;
            miWatchdog.Checked = S.Watchdog;
            RegisterHotkey();
            if (enforceAutoOff) Enqueue(ApplyAutoOff);
            if (apChanged) Enqueue(() => DoConfigure(ssid, pass, band));
            if (last != null) UpdateUi(last);
            Log.Write("Settings saved");
            RequestCheck();
        }

        void RegisterHotkey()
        {
            if (hotkey == null) return;
            hotkey.Unregister();
            if (!S.HotkeyEnabled) return;
            Keys k;
            try { k = (Keys)Enum.Parse(typeof(Keys), S.HotkeyKey, true); }
            catch { k = Keys.H; }
            if (!hotkey.Register(k))
            {
                Log.Write("Shortcut Ctrl+Alt+" + S.HotkeyKey + " is already used by another app");
                Notify("Shortcut not available", "Ctrl+Alt+" + S.HotkeyKey + " is taken by another app. Pick another key in Settings.", ToolTipIcon.Warning);
            }
        }

        public string NameFor(ClientInfo c)
        {
            string n;
            if (c.Mac != null && S.Nicknames.TryGetValue(c.Mac, out n)) return n;
            if (!string.IsNullOrEmpty(c.Host)) return c.Host;
            return c.Mac ?? "Unknown device";
        }

        public void Rename(string mac, string name)
        {
            if (mac == null) return;
            if (string.IsNullOrWhiteSpace(name)) S.Nicknames.Remove(mac);
            else S.Nicknames[mac] = name.Trim();
            S.Save();
            if (last != null) UpdateUi(last);
        }

        public void Copy(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        public void Notify(string title, string text, ToolTipIcon icon)
        {
            if (exiting) return;
            try { tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? " " : text, icon); } catch { }
        }

        public string StatusText(HotspotInfo i)
        {
            switch (i.State)
            {
                case "On": return "ON - " + Fmt.Devices(i.ClientCount);
                case "InTransition": return "switching...";
                case "NoInternet": return i.DesiredOn ? "waiting for internet" : "OFF (no internet)";
                case "Off":
                    if (!i.DesiredOn) return "OFF";
                    if (i.Failures > 0) return "DOWN - retrying";
                    return S.Watchdog ? "OFF - restarting..." : "OFF (auto-restart disabled)";
                case "Error": return "error";
                default: return i.State;
            }
        }

        public Color StateColor(HotspotInfo i)
        {
            var ico = IconFor(i);
            if (ico == icoOn) return Color.FromArgb(34, 160, 84);
            if (ico == icoBusy) return Color.FromArgb(200, 130, 0);
            if (ico == icoErr) return Color.FromArgb(200, 50, 50);
            return Color.DimGray;
        }

        Icon IconFor(HotspotInfo i)
        {
            switch (i.State)
            {
                case "On": return icoOn;
                case "InTransition": return icoBusy;
                case "NoInternet": return i.DesiredOn ? icoBusy : icoOff;
                case "Off":
                    if (!i.DesiredOn) return icoOff;
                    if (i.Failures > 0) return icoErr;
                    return S.Watchdog ? icoBusy : icoOff;
                default: return icoErr;
            }
        }

        void UpdateUi(HotspotInfo info)
        {
            if (exiting) return;

            if (info.Joined != null && info.Joined.Count > 0)
            {
                string names = string.Join(", ", info.Joined.Select(c => NameFor(c) + (c.Ip != null ? " (" + c.Ip + ")" : "")));
                Log.Write("Device connected: " + names);
                if (S.NotifyDevices) Notify("Device connected", names, ToolTipIcon.None);
                info.Joined = null;
            }
            if (info.Left != null && info.Left.Count > 0)
            {
                string names = string.Join(", ", info.Left.Select(c => NameFor(c)));
                Log.Write("Device left: " + names);
                if (S.NotifyDevices) Notify("Device left", names, ToolTipIcon.None);
                info.Left = null;
            }

            string st = StatusText(info);
            tray.Icon = IconFor(info);
            tray.Text = Fmt.Cut("Hotspot " + st + (info.Upstream != null && info.IsOn ? " - " + info.Upstream : ""), 63);
            miStatus.Text = "Hotspot: " + st;
            miToggle.Text = (info.DesiredOn || info.IsOn) ? "Turn hotspot off" : "Turn hotspot on";
            miToggle.ShortcutKeyDisplayString = S.HotkeyEnabled ? "Ctrl+Alt+" + S.HotkeyKey : "";
            miRestart.Enabled = info.IsOn;
            miWatchdog.Checked = S.Watchdog;

            miDevices.Text = info.IsOn && info.ClientCount > 0 ? "Devices (" + info.ClientCount + ")" : "Devices";
            if (tray.ContextMenuStrip.Visible) { }   // don't rebuild the submenu under the cursor
            else if (info.Clients.Count == 0)
            {
                miDevices.DropDownItems.Clear();
                string t = info.IsOn && info.ClientCount > 0 ? Fmt.Devices(info.ClientCount) + " (details unavailable)" : "No devices connected";
                miDevices.DropDownItems.Add(new ToolStripMenuItem(t) { Enabled = false });
            }
            else
            {
                miDevices.DropDownItems.Clear();
                foreach (var c in info.Clients)
                {
                    var cc = c;
                    string text = NameFor(cc) + (cc.Ip != null ? "  -  " + cc.Ip : "");
                    var item = new ToolStripMenuItem(text, null, (s, e) => Copy(cc.Ip ?? cc.Mac));
                    item.ToolTipText = "Click to copy " + (cc.Ip != null ? "the IP" : "the MAC address");
                    miDevices.DropDownItems.Add(item);
                }
            }

            UpdateKeepAwake(info);
            if (dashboard != null && !dashboard.IsDisposed) dashboard.UpdateInfo(info);
        }

        void UpdateKeepAwake(HotspotInfo info)
        {
            bool want = S.KeepAwake && info.IsOn && info.ClientCount > 0;
            if (want == awakeHeld) return;
            SetThreadExecutionState(want ? (ES_CONTINUOUS | ES_SYSTEM_REQUIRED) : ES_CONTINUOUS);
            awakeHeld = want;
            Log.Write(want ? "Keeping the PC awake (devices connected)" : "PC may sleep again");
        }

        public void ShowDashboard()
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new DashboardForm(this);
                dashboard.FormClosed += (s, e) => { detailsWanted = false; dashboard = null; };
                detailsWanted = true;
                dashboard.Show();
                if (last != null) dashboard.UpdateInfo(last);
                RequestCheck();
            }
            else
            {
                if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
                dashboard.Activate();
            }
        }

        public void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            settingsForm = new SettingsForm(this, S, last);
            settingsForm.FormClosed += (s, e) => settingsForm = null;
            settingsForm.Show();
        }

        public void OpenLog()
        {
            if (!File.Exists(Log.FilePath)) Log.Write("Log opened");
            try { Process.Start("notepad.exe", "\"" + Log.FilePath + "\""); } catch { }
        }

        public void OpenUri(string uri)
        {
            try { Process.Start(uri); } catch (Exception ex) { Log.Write("Couldn't open " + uri + ": " + ex.Message); }
        }

        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume) return;
            Log.Write("Resumed from sleep");
            Post(() => { debounce.Stop(); debounce.Interval = 8000; debounce.Start(); });
        }

        void OnNetworkChanged(object sender, EventArgs e)
        {
            Post(() => { debounce.Stop(); debounce.Interval = 4000; debounce.Start(); });
        }

        void ExitApp()
        {
            exiting = true;
            Log.Write("Exit");
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
            }
            catch { }
            timer.Stop();
            debounce.Stop();
            firstCheck.Stop();
            if (hotkey != null) { hotkey.Dispose(); hotkey = null; }
            if (awakeHeld) SetThreadExecutionState(ES_CONTINUOUS);
            queue.CompleteAdding();
            if (dashboard != null && !dashboard.IsDisposed) dashboard.Close();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ helpers

    static class Fmt
    {
        public static string Devices(int n) { return n == 1 ? "1 device" : n + " devices"; }

        public static string Span(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + " s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " min";
            if (t.TotalHours < 24) return (int)t.TotalHours + " h " + t.Minutes + " min";
            return (int)t.TotalDays + " d " + t.Hours + " h";
        }

        public static string Cut(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 3) + "..."; }
    }

    static class Icons
    {
        public static Icon Make(Color c)
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 30, 30);
                    using (var p = new Pen(Color.White, 3.2f))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawArc(p, 4.5f, 9.5f, 23f, 23f, 225f, 90f);    // r = 11.5 around (16, 21)
                        g.DrawArc(p, 9.5f, 14.5f, 13f, 13f, 225f, 90f);   // r = 6.5
                    }
                    using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, 13.6f, 18.6f, 4.8f, 4.8f);
                }
                return Icon.FromHandle(bmp.GetHicon());   // kept for the app's lifetime
            }
        }
    }

    class HotkeyWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        const int WM_HOTKEY = 0x0312, Id = 0x4857;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        bool registered;
        public event Action Pressed;

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public bool Register(Keys key)
        {
            Unregister();
            registered = RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key);
            return registered;
        }

        public void Unregister()
        {
            if (!registered) return;
            UnregisterHotKey(Handle, Id);
            registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id)
            {
                var h = Pressed;
                if (h != null) h();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            DestroyHandle();
        }
    }

    // ------------------------------------------------------------------ dashboard

    class DashboardForm : Form
    {
        readonly TrayApp app;
        readonly Label lblState = new Label(), lblSub = new Label(), lblStats = new Label(), lblEvent = new Label();
        readonly Button btnToggle = new Button(), btnRestart = new Button(), btnPass = new Button(), btnSettings = new Button();
        readonly CheckBox chkWatchdog = new CheckBox();
        readonly ListView lv = new ListView();
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();
        readonly ContextMenuStrip rowMenu = new ContextMenuStrip();
        readonly float dpi = 1f;
        HotspotInfo info;
        bool updating;

        public DashboardForm(TrayApp app)
        {
            this.app = app;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX / 96f; } catch { }

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Hotspot Watchdog";
            Icon = app.AppIcon;
            ClientSize = new Size(580, 440);
            MinimumSize = new Size(480, 360);
            StartPosition = FormStartPosition.Manual;

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.Padding = new Padding(14, 10, 14, 10);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 7; i++)
                root.RowStyles.Add(i == 4 ? new RowStyle(SizeType.Percent, 100F) : new RowStyle(SizeType.AutoSize));

            lblState.AutoSize = true;
            lblState.Font = new Font("Segoe UI Semibold", 15F);
            lblState.Margin = new Padding(0, 0, 0, 2);
            lblState.Text = "Checking...";

            lblSub.AutoSize = true;
            lblSub.ForeColor = Color.DimGray;
            lblSub.Margin = new Padding(0, 0, 0, 10);

            var buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 0, 0, 6);
            foreach (var b in new[] { btnToggle, btnRestart, btnPass, btnSettings })
            {
                b.AutoSize = true;
                b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                b.Padding = new Padding(8, 3, 8, 3);
                b.Margin = new Padding(0, 0, 8, 0);
                buttons.Controls.Add(b);
            }
            btnToggle.Text = "Turn off";
            btnRestart.Text = "Restart";
            btnPass.Text = "Copy password";
            btnSettings.Text = "Settings...";
            btnToggle.Click += (s, e) => app.Toggle(false);
            btnRestart.Click += (s, e) => app.Restart();
            btnSettings.Click += (s, e) => app.ShowSettings();
            btnPass.Click += (s, e) =>
            {
                if (info == null || string.IsNullOrEmpty(info.Passphrase)) return;
                app.Copy(info.Passphrase);
                btnPass.Text = "Copied!";
                var t = new System.Windows.Forms.Timer();
                t.Interval = 1500;
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); if (!btnPass.IsDisposed) btnPass.Text = "Copy password"; };
                t.Start();
            };

            chkWatchdog.AutoSize = true;
            chkWatchdog.Text = "Auto-restart the hotspot when it drops";
            chkWatchdog.Margin = new Padding(2, 2, 0, 8);
            chkWatchdog.CheckedChanged += (s, e) => { if (!updating) app.SetWatchdog(chkWatchdog.Checked); };

            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            lv.MultiSelect = false;
            lv.Dock = DockStyle.Fill;
            lv.Columns.Add("Device", (int)(170 * dpi));
            lv.Columns.Add("IP", (int)(120 * dpi));
            lv.Columns.Add("MAC", (int)(130 * dpi));
            lv.Columns.Add("ADB (5555)", (int)(80 * dpi));
            lv.DoubleClick += (s, e) => CopySelected(true);

            rowMenu.Items.Add("Copy IP", null, (s, e) => CopySelected(true));
            rowMenu.Items.Add("Copy MAC", null, (s, e) => CopySelected(false));
            rowMenu.Items.Add(new ToolStripSeparator());
            rowMenu.Items.Add("Rename...", null, (s, e) => RenameSelected());
            rowMenu.Items.Add("Reset name", null, (s, e) => { var c = Selected(); if (c != null) app.Rename(c.Mac, null); });
            rowMenu.Opening += (s, e) => { if (Selected() == null) e.Cancel = true; };
            lv.ContextMenuStrip = rowMenu;

            lblStats.AutoSize = true;
            lblStats.ForeColor = Color.DimGray;
            lblStats.Margin = new Padding(0, 8, 0, 0);

            lblEvent.AutoSize = true;
            lblEvent.ForeColor = Color.Gray;
            lblEvent.Margin = new Padding(0, 2, 0, 0);

            root.Controls.Add(lblState, 0, 0);
            root.Controls.Add(lblSub, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            root.Controls.Add(chkWatchdog, 0, 3);
            root.Controls.Add(lv, 0, 4);
            root.Controls.Add(lblStats, 0, 5);
            root.Controls.Add(lblEvent, 0, 6);
            Controls.Add(root);
            ResumeLayout(false);
            PerformLayout();

            tick.Interval = 1000;
            tick.Tick += (s, e) => RenderTexts();
            tick.Start();
            Log.Written += OnLog;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Log.Written -= OnLog;
            tick.Stop();
            tick.Dispose();
            base.OnFormClosed(e);
        }

        void OnLog(string line)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke((Action)(() => { if (!IsDisposed) lblEvent.Text = "Last event: " + line; }));
            }
            catch { }
        }

        public void UpdateInfo(HotspotInfo i)
        {
            info = i;
            updating = true;
            chkWatchdog.Checked = app.S.Watchdog;
            updating = false;
            btnToggle.Text = (i.DesiredOn || i.IsOn) ? "Turn off" : "Turn on";
            btnRestart.Enabled = i.IsOn;
            btnPass.Enabled = !string.IsNullOrEmpty(i.Passphrase);

            string sel = null;
            var cur = Selected();
            if (cur != null) sel = cur.Mac;
            lv.BeginUpdate();
            lv.Items.Clear();
            foreach (var c in i.Clients)
            {
                var it = new ListViewItem(app.NameFor(c));
                it.SubItems.Add(c.Ip ?? "-");
                it.SubItems.Add(c.Mac ?? "-");
                it.SubItems.Add(c.Adb.HasValue ? (c.Adb.Value ? "open" : "closed") : "");
                it.Tag = c;
                if (c.Adb == true) it.SubItems[3].ForeColor = Color.FromArgb(34, 140, 74);
                it.UseItemStyleForSubItems = false;
                lv.Items.Add(it);
                if (sel != null && c.Mac == sel) it.Selected = true;
            }
            if (i.Clients.Count == 0 && i.IsOn && i.ClientCount > 0)
                lv.Items.Add(new ListViewItem(Fmt.Devices(i.ClientCount) + " connected (Windows didn't share details)"));
            lv.EndUpdate();
            RenderTexts();
        }

        void RenderTexts()
        {
            var i = info;
            if (i == null) return;
            lblState.Text = "Hotspot " + app.StatusText(i);
            lblState.ForeColor = app.StateColor(i);

            var parts = new List<string>();
            if (i.Upstream != null) parts.Add("Sharing: " + i.Upstream);
            if (i.Ssid != null) parts.Add("Network: " + i.Ssid);
            if (i.Band != null) parts.Add("Band: " + i.Band);
            if (i.IsOn && i.OnSince.HasValue) parts.Add("Up " + Fmt.Span(DateTime.Now - i.OnSince.Value));
            if (i.IsOn && i.MaxClients > 0) parts.Add(i.ClientCount + "/" + i.MaxClients + " devices");
            string sub = string.Join("  \u00B7  ", parts);
            if (!i.IsOn && i.Capability != null && i.Capability != "Enabled") sub += (sub.Length > 0 ? Environment.NewLine : "") + "Windows reports: " + i.Capability;
            lblSub.Text = sub.Length > 0 ? sub : " ";

            var st = new List<string>();
            st.Add("Auto-restarts today: " + i.RestartsToday);
            st.Add("Last drop: " + (i.LastDrop.HasValue ? i.LastDrop.Value.ToString("HH:mm") : "-"));
            st.Add("Check every " + app.S.IntervalSec + " s");
            if (i.Failures > 0 && i.NextAttempt > DateTime.Now) st.Add("Next try in " + Fmt.Span(i.NextAttempt - DateTime.Now));
            string stats = string.Join("  \u00B7  ", st);
            if (i.Error != null) stats += Environment.NewLine + "Last error: " + i.Error;
            lblStats.Text = stats;
        }

        ClientInfo Selected()
        {
            if (lv.SelectedItems.Count == 0) return null;
            return lv.SelectedItems[0].Tag as ClientInfo;
        }

        void CopySelected(bool ip)
        {
            var c = Selected();
            if (c == null) return;
            app.Copy(ip ? (c.Ip ?? c.Mac) : c.Mac);
        }

        void RenameSelected()
        {
            var c = Selected();
            if (c == null || c.Mac == null) return;
            string n = Prompt.Show(this, "Rename device", "Name for " + c.Mac + ":", app.NameFor(c));
            if (n != null) app.Rename(c.Mac, n);
        }
    }

    static class Prompt
    {
        public static string Show(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new Form())
            {
                f.SuspendLayout();
                f.AutoScaleDimensions = new SizeF(96F, 96F);
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.Font = new Font("Segoe UI", 9F);
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.AutoSize = true;
                f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                f.Padding = new Padding(12);

                var root = new TableLayoutPanel();
                root.AutoSize = true;
                root.ColumnCount = 1;
                var lbl = new Label();
                lbl.Text = label;
                lbl.AutoSize = true;
                var tb = new TextBox();
                tb.Text = value ?? "";
                tb.Width = 280;
                tb.Margin = new Padding(0, 6, 0, 10);
                var buttons = new FlowLayoutPanel();
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                var cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                var ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                root.Controls.Add(lbl);
                root.Controls.Add(tb);
                root.Controls.Add(buttons);
                f.Controls.Add(root);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.ResumeLayout(false);
                f.PerformLayout();
                tb.SelectAll();
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
            }
        }
    }

    // ------------------------------------------------------------------ settings window

    class Grid
    {
        public readonly TableLayoutPanel Panel = new TableLayoutPanel();
        int row;

        public Grid()
        {
            Panel.AutoSize = true;
            Panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Panel.ColumnCount = 2;
            Panel.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            Panel.Margin = new Padding(0, 0, 28, 0);
            Panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        void Next()
        {
            Panel.RowCount = row + 1;
            Panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        public void Section(string title)
        {
            Next();
            var l = new Label();
            l.Text = title;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI Semibold", 10F);
            l.Margin = new Padding(0, row == 0 ? 0 : 14, 0, 4);
            Panel.Controls.Add(l, 0, row);
            Panel.SetColumnSpan(l, 2);
            row++;
        }

        public void Wide(Control c)
        {
            Next();
            if (c is CheckBox) c.Margin = new Padding(2, 3, 0, 3);
            Panel.Controls.Add(c, 0, row);
            Panel.SetColumnSpan(c, 2);
            row++;
        }

        public void Row(string label, Control c)
        {
            Next();
            var l = new Label();
            l.Text = label;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(2, 4, 10, 4);
            c.Anchor = AnchorStyles.Left;
            c.Margin = new Padding(0, 3, 0, 3);
            Panel.Controls.Add(l, 0, row);
            Panel.Controls.Add(c, 1, row);
            row++;
        }

        public void Note(string text)
        {
            Next();
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = Color.Gray;
            l.MaximumSize = new Size(330, 0);
            l.Margin = new Padding(2, 2, 0, 2);
            Panel.Controls.Add(l, 0, row);
            Panel.SetColumnSpan(l, 2);
            row++;
        }
    }

    class Item
    {
        public string Text;
        public int Value;
        public Item(string text, int value) { Text = text; Value = value; }
        public override string ToString() { return Text; }
    }

    class SettingsForm : Form
    {
        readonly TrayApp app;
        readonly Settings S;
        readonly HotspotInfo info;
        readonly CheckBox chkAutostart = new CheckBox(), chkWatchdog = new CheckBox(), chkUpstream = new CheckBox(),
            chkAutoOff = new CheckBox(), chkAwake = new CheckBox(), chkNotifyDrop = new CheckBox(),
            chkNotifyDevices = new CheckBox(), chkHotkey = new CheckBox(), chkShowPass = new CheckBox();
        readonly ComboBox cbLaunch = new ComboBox(), cbKey = new ComboBox(), cbBand = new ComboBox();
        readonly NumericUpDown numInterval = new NumericUpDown(), numDelay = new NumericUpDown();
        readonly TextBox tbSsid = new TextBox(), tbPass = new TextBox();

        public SettingsForm(TrayApp app, Settings settings, HotspotInfo current)
        {
            this.app = app;
            S = settings;
            info = current;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9F);
            Text = "Hotspot Watchdog - Settings";
            Icon = app.AppIcon;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(14);

            var left = new Grid();
            var right = new Grid();

            // General
            left.Section("General");
            chkAutostart.Text = "Start with Windows";
            chkAutostart.Checked = Autostart.IsEnabled();
            chkAutostart.AutoSize = true;
            left.Wide(chkAutostart);
            cbLaunch.DropDownStyle = ComboBoxStyle.DropDownList;
            cbLaunch.Width = 180;
            cbLaunch.Items.AddRange(new object[] { "Turn the hotspot on", "Restore its last state", "Leave it as it is" });
            cbLaunch.SelectedIndex = (int)S.Launch;
            left.Row("When the app starts:", cbLaunch);
            numDelay.Minimum = 0;
            numDelay.Maximum = 300;
            numDelay.Value = Settings.Clamp(S.StartupDelaySec, 0, 300);
            numDelay.Width = 70;
            left.Row("Wait after sign-in (s):", numDelay);

            // Watchdog
            left.Section("Watchdog");
            chkWatchdog.Text = "Auto-restart the hotspot when it drops";
            chkWatchdog.Checked = S.Watchdog;
            chkWatchdog.AutoSize = true;
            left.Wide(chkWatchdog);
            numInterval.Minimum = 5;
            numInterval.Maximum = 300;
            numInterval.Value = Settings.Clamp(S.IntervalSec, 5, 300);
            numInterval.Width = 70;
            left.Row("Check every (s):", numInterval);
            chkUpstream.Text = "Restart when the shared network changes";
            chkUpstream.Checked = S.RestartOnUpstreamChange;
            chkUpstream.AutoSize = true;
            left.Wide(chkUpstream);
            chkAutoOff.Text = "Stop Windows from turning it off when idle";
            chkAutoOff.Checked = S.DisableAutoOff;
            chkAutoOff.AutoSize = true;
            left.Wide(chkAutoOff);
            chkAwake.Text = "Keep the PC awake while devices are connected";
            chkAwake.Checked = S.KeepAwake;
            chkAwake.AutoSize = true;
            left.Wide(chkAwake);
            left.Note("Keep-awake blocks idle sleep only; closing the lid still follows your power settings.");

            // Hotspot network
            right.Section("Hotspot network");
            tbSsid.Width = 190;
            tbSsid.MaxLength = 32;
            tbPass.Width = 190;
            tbPass.MaxLength = 63;
            tbPass.UseSystemPasswordChar = true;
            chkShowPass.Text = "Show";
            chkShowPass.AutoSize = true;
            chkShowPass.Margin = new Padding(8, 4, 0, 0);
            chkShowPass.CheckedChanged += (s, e) => tbPass.UseSystemPasswordChar = !chkShowPass.Checked;
            var passRow = new FlowLayoutPanel();
            passRow.AutoSize = true;
            passRow.WrapContents = false;
            passRow.Margin = new Padding(0);
            tbPass.Margin = new Padding(0);
            passRow.Controls.Add(tbPass);
            passRow.Controls.Add(chkShowPass);
            cbBand.DropDownStyle = ComboBoxStyle.DropDownList;
            cbBand.Width = 120;

            bool apKnown = info != null && info.Ssid != null;
            tbSsid.Text = apKnown ? info.Ssid : "";
            tbPass.Text = apKnown ? (info.Passphrase ?? "") : "";
            var bands = Hotspot.SupportedBands;
            if (apKnown && bands != null && info.BandValue >= 0)
            {
                foreach (int b in bands) cbBand.Items.Add(new Item(Hotspot.BandName(b), b));
                foreach (Item it in cbBand.Items) if (it.Value == info.BandValue) cbBand.SelectedItem = it;
                if (cbBand.SelectedIndex < 0 && cbBand.Items.Count > 0) cbBand.SelectedIndex = 0;
            }
            else cbBand.Enabled = false;
            tbSsid.Enabled = apKnown;
            tbPass.Enabled = apKnown;
            chkShowPass.Enabled = apKnown;

            right.Row("Name (SSID):", tbSsid);
            right.Row("Password:", passRow);
            right.Row("Band:", cbBand);
            right.Note(apKnown
                ? "Changing these restarts the hotspot; devices reconnect with the new name/password. Keep Band on Auto if unsure."
                : "Not available right now - Windows needs an internet connection to read these.");

            // Notifications
            right.Section("Notifications");
            chkNotifyDrop.Text = "Hotspot restored / can't restart";
            chkNotifyDrop.Checked = S.NotifyDropRestore;
            chkNotifyDrop.AutoSize = true;
            right.Wide(chkNotifyDrop);
            chkNotifyDevices.Text = "Device connected / left";
            chkNotifyDevices.Checked = S.NotifyDevices;
            chkNotifyDevices.AutoSize = true;
            right.Wide(chkNotifyDevices);

            // Shortcut
            right.Section("Shortcut");
            var hk = new FlowLayoutPanel();
            hk.AutoSize = true;
            hk.WrapContents = false;
            hk.Margin = new Padding(0);
            chkHotkey.Text = "Toggle the hotspot with Ctrl + Alt +";
            chkHotkey.Checked = S.HotkeyEnabled;
            chkHotkey.AutoSize = true;
            chkHotkey.Margin = new Padding(2, 5, 4, 0);
            cbKey.DropDownStyle = ComboBoxStyle.DropDownList;
            cbKey.Width = 60;
            for (char ch = 'A'; ch <= 'Z'; ch++) cbKey.Items.Add(ch.ToString());
            for (int f = 1; f <= 12; f++) cbKey.Items.Add("F" + f);
            cbKey.SelectedItem = S.HotkeyKey;
            if (cbKey.SelectedIndex < 0) cbKey.SelectedItem = "H";
            hk.Controls.Add(chkHotkey);
            hk.Controls.Add(cbKey);
            right.Wide(hk);

            // Bottom
            var root = new TableLayoutPanel();
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 2;
            root.RowCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Controls.Add(left.Panel, 0, 0);
            root.Controls.Add(right.Panel, 1, 0);

            var bottom = new FlowLayoutPanel();
            bottom.FlowDirection = FlowDirection.RightToLeft;
            bottom.AutoSize = true;
            bottom.Dock = DockStyle.Fill;
            bottom.Margin = new Padding(0, 16, 0, 0);
            var btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.AutoSize = true;
            btnCancel.DialogResult = DialogResult.Cancel;
            var btnSave = new Button();
            btnSave.Text = "Save";
            btnSave.AutoSize = true;
            btnSave.Click += (s, e) => SaveAndClose();
            var btnLog = new LinkLabel();
            btnLog.Text = "Open log";
            btnLog.AutoSize = true;
            btnLog.Margin = new Padding(0, 6, 16, 0);
            btnLog.LinkClicked += (s, e) => app.OpenLog();
            bottom.Controls.Add(btnCancel);
            bottom.Controls.Add(btnSave);
            bottom.Controls.Add(btnLog);
            root.Controls.Add(bottom, 0, 1);
            root.SetColumnSpan(bottom, 2);

            Controls.Add(root);
            AcceptButton = btnSave;
            CancelButton = btnCancel;
            ResumeLayout(false);
            PerformLayout();
        }

        void SaveAndClose()
        {
            bool apEditable = tbSsid.Enabled;
            string ssid = tbSsid.Text.Trim();
            string pass = tbPass.Text;
            if (apEditable)
            {
                if (ssid.Length == 0 || Encoding.UTF8.GetByteCount(ssid) > 32)
                {
                    MessageBox.Show(this, "The network name must be 1-32 characters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (pass.Length < 8 || pass.Length > 63)
                {
                    MessageBox.Show(this, "The password must be 8-63 characters.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            bool autoOffBefore = S.DisableAutoOff;
            S.Launch = (LaunchMode)Math.Max(0, cbLaunch.SelectedIndex);
            S.StartupDelaySec = (int)numDelay.Value;
            S.Watchdog = chkWatchdog.Checked;
            S.IntervalSec = (int)numInterval.Value;
            S.RestartOnUpstreamChange = chkUpstream.Checked;
            S.DisableAutoOff = chkAutoOff.Checked;
            S.KeepAwake = chkAwake.Checked;
            S.NotifyDropRestore = chkNotifyDrop.Checked;
            S.NotifyDevices = chkNotifyDevices.Checked;
            S.HotkeyEnabled = chkHotkey.Checked;
            S.HotkeyKey = (cbKey.SelectedItem as string) ?? "H";

            try
            {
                Autostart.Set(chkAutostart.Checked);
                S.StartWithWindows = chkAutostart.Checked;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't change \"Start with Windows\": " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            int band = -1;
            var bi = cbBand.SelectedItem as Item;
            if (cbBand.Enabled && bi != null) band = bi.Value;
            bool apChanged = apEditable && info != null &&
                (ssid != (info.Ssid ?? "") || pass != (info.Passphrase ?? "") || (band >= 0 && band != info.BandValue));

            app.ApplySettings(!autoOffBefore && S.DisableAutoOff, apChanged, ssid, pass, band);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
