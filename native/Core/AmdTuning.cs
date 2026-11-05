using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PulseWatch.Core
{
    /// <summary>One tunable value of an AMD card, with the range the driver itself reports.</summary>
    public sealed class AmdSetting
    {
        public int Id { get; init; }
        public string He { get; init; } = "";
        public string En { get; init; } = "";
        public string Unit { get; init; } = "";
        public int Min { get; init; }
        public int Max { get; init; }
        public int Default { get; init; }
        public int Current { get; internal set; }
        public string Name => L.T(He, En);
    }

    public sealed class AmdAdapter
    {
        public int Index { get; init; }
        public string Name { get; init; } = "";
        public int Bus { get; init; }
        public bool Discrete { get; init; }
        public int Caps { get; init; }
        public List<AmdSetting> Settings { get; init; } = new();
        public override string ToString() => Name;
    }

    /// <summary>
    /// EXPERIMENTAL: AMD Radeon tuning through ADL Overdrive 8 (atiadlxx.dll, part of the Radeon driver), following the
    /// order of calls in AMD's own Overdrive8 sample. Only loading the library and listing adapters have been run (on a
    /// PC with a Ryzen iGPU, which has no Overdrive 8): reading and writing the settings have never run on a Radeon card.
    /// Nothing is applied unless the user presses Apply, and nothing is re-applied at start.
    /// Only used on PCs without an NVIDIA card, so the library is never loaded otherwise.
    /// </summary>
    public static class AmdTuning
    {
        private const string Dll = "atiadlxx.dll";
        private const int Ok = 0, Od8Count = 77, MaxPath = 256;
        private const int AsicDiscrete = 1 << 0;

        // ADLOD8SettingId
        private const int GfxFmax = 0, GfxFmin = 1, GfxFreq1 = 2, GfxFreq3 = 6, GfxVolt3 = 7, UclkFmax = 8, PowerPct = 9, UclkFmin = 34, PowerMode = 36, OdVoltage = 37;
        // ADLOD8FeatureControl
        private const int CapGfxLimits = 1 << 0, CapGfxCurve = 1 << 1, CapUclkMax = 1 << 2, CapPower = 1 << 3, CapPowerMode = 1 << 16, CapOdVoltage = 1 << 17;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AdlAlloc(int size);
        private static readonly AdlAlloc Alloc = size => Marshal.AllocCoTaskMem(size);   // kept alive for the driver's callbacks

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Main_Control_Create(AdlAlloc alloc, int connectedOnly, out IntPtr context);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr context, out int count);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Adapter_AdapterInfo_Get(IntPtr context, IntPtr info, int size);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Adapter_ASICFamilyType_Get(IntPtr context, int adapter, out int types, out int valids);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Overdrive_Caps(IntPtr context, int adapter, out int supported, out int enabled, out int version);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Overdrive8_Init_SettingX2_Get(IntPtr context, int adapter, out int caps, out int features, out IntPtr list);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Overdrive8_Current_SettingX2_Get(IntPtr context, int adapter, out int features, out IntPtr list);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int ADL2_Overdrive8_Setting_Set(IntPtr context, int adapter, IntPtr set, IntPtr current);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct AdapterInfo
        {
            public int iSize, iAdapterIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strUDID;
            public int iBusNumber, iDeviceNumber, iFunctionNumber, iVendorID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strAdapterName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strDisplayName;
            public int iPresent, iExist;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strDriverPath;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strDriverPathExt;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxPath)] public string strPNPString;
            public int iOSDisplayIndex;
        }

        private static readonly object Gate = new();
        private static IntPtr _context;

        /// <summary>id, feature bit, Hebrew, English, unit — only what the card reports as supported is offered.</summary>
        private static readonly (int id, int cap, string he, string en, string unit)[] Known =
        {
            (GfxFmax, CapGfxLimits, "תדר ליבה מקסימלי", "Max core clock", "MHz"),
            (GfxFmin, CapGfxLimits, "תדר ליבה מינימלי", "Min core clock", "MHz"),
            (GfxVolt3, CapGfxCurve, "מתח בתדר העליון", "Voltage at the top clock", "mV"),
            (OdVoltage, CapOdVoltage, "היסט מתח (Undervolt)", "Voltage offset (undervolt)", "mV"),
            (UclkFmax, CapUclkMax, "תדר זיכרון מקסימלי", "Max memory clock", "MHz"),
            (PowerPct, CapPower, "מגבלת הספק", "Power limit", "%"),
        };

        /// <summary>Radeon cards with Overdrive 8 and at least one known setting. Empty when there is no AMD driver.</summary>
        public static List<AmdAdapter> Detect()
        {
            var result = new List<AmdAdapter>();
            lock (Gate)
            {
                try
                {
                    if (_context == IntPtr.Zero && ADL2_Main_Control_Create(Alloc, 1, out _context) != Ok) { _context = IntPtr.Zero; return result; }
                    if (ADL2_Adapter_NumberOfAdapters_Get(_context, out int n) != Ok || n <= 0) return result;
                    int stride = Marshal.SizeOf<AdapterInfo>();
                    var buf = Marshal.AllocCoTaskMem(stride * n);
                    try
                    {
                        Zero(buf, stride * n);
                        if (ADL2_Adapter_AdapterInfo_Get(_context, buf, stride * n) != Ok) return result;
                        var seenBus = new HashSet<int>();
                        for (int i = 0; i < n; i++)
                        {
                            var info = Marshal.PtrToStructure<AdapterInfo>(buf + i * stride);
                            // one physical card appears once per display output
                            if (info.iBusNumber < 0 || !seenBus.Add(info.iBusNumber)) continue;
                            if (ADL2_Overdrive_Caps(_context, info.iAdapterIndex, out _, out _, out int version) != Ok || version != 8) continue;
                            var a = ReadAdapter(info);
                            if (a != null && a.Settings.Count > 0) result.Add(a);
                        }
                    }
                    finally { Marshal.FreeCoTaskMem(buf); }
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    Log.Info("AMD tuning unavailable: " + ex.GetType().Name);
                }
                catch (Exception ex) { Log.Warn("AMD tuning: " + ex.Message); }
            }
            return result;
        }

        private static AmdAdapter? ReadAdapter(AdapterInfo info)
        {
            if (ADL2_Overdrive8_Init_SettingX2_Get(_context, info.iAdapterIndex, out int caps, out int features, out IntPtr list) != Ok) return null;
            var table = new Dictionary<int, (int min, int max, int def)>();
            try
            {
                // ADLOD8SingleInitSetting { featureID, minValue, maxValue, defaultValue }
                for (int i = 0; i < features && i < Od8Count; i++)
                {
                    int o = i * 16;
                    table[i] = (Marshal.ReadInt32(list, o + 4), Marshal.ReadInt32(list, o + 8), Marshal.ReadInt32(list, o + 12));
                }
            }
            finally { if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list); }

            var current = ReadCurrent(info.iAdapterIndex);
            if (current == null) return null;
            bool discrete = ADL2_Adapter_ASICFamilyType_Get(_context, info.iAdapterIndex, out int types, out int valids) == Ok && (types & valids & AsicDiscrete) != 0;
            var adapter = new AmdAdapter { Index = info.iAdapterIndex, Name = info.strAdapterName.Trim(), Bus = info.iBusNumber, Discrete = discrete, Caps = caps };
            foreach (var k in Known)
            {
                if ((caps & k.cap) == 0 || !table.TryGetValue(k.id, out var r) || r.max <= r.min || k.id >= current.Length) continue;
                adapter.Settings.Add(new AmdSetting { Id = k.id, He = k.he, En = k.en, Unit = k.unit, Min = r.min, Max = r.max, Default = r.def, Current = current[k.id] });
            }
            return adapter;
        }

        private static int[]? ReadCurrent(int adapter)
        {
            if (ADL2_Overdrive8_Current_SettingX2_Get(_context, adapter, out int features, out IntPtr list) != Ok) return null;
            try
            {
                var v = new int[Math.Min(features, Od8Count)];
                for (int i = 0; i < v.Length; i++) v[i] = Marshal.ReadInt32(list, i * 4);
                return v;
            }
            finally { if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list); }
        }

        /// <summary>Re-reads the current values from the driver.</summary>
        public static bool Refresh(AmdAdapter a)
        {
            lock (Gate)
            {
                try
                {
                    var c = ReadCurrent(a.Index);
                    if (c == null) return false;
                    foreach (var s in a.Settings) if (s.Id < c.Length) s.Current = c[s.Id];
                    return true;
                }
                catch (Exception ex) { Log.Warn("AMD read: " + ex.Message); return false; }
            }
        }

        /// <summary>Sets one value (the driver rejects what it does not accept). Returns an error text, or null on success.</summary>
        public static string? Set(AmdAdapter a, AmdSetting s, int value) => Write(a, s, value, reset: false);

        /// <summary>Back to the card's defaults, one setting at a time. Returns the first error, or null.</summary>
        public static string? ResetAll(AmdAdapter a)
        {
            foreach (var s in a.Settings)
            {
                var err = Write(a, s, s.Default, reset: true);
                if (err != null) return err;
            }
            return null;
        }

        private static string? Write(AmdAdapter a, AmdSetting s, int value, bool reset)
        {
            if (value < s.Min || value > s.Max) return L.T($"{s.Name}: הטווח הוא {s.Min}–{s.Max}", $"{s.Name}: the range is {s.Min}–{s.Max}");
            lock (Gate)
            {
                // ADLOD8SetSetting { count, { value, requested, reset }[OD8_COUNT] } and ADLOD8CurrentSetting { count, int[OD8_COUNT] }
                int setSize = 4 + 12 * Od8Count, curSize = 4 + 4 * Od8Count;
                var set = Marshal.AllocCoTaskMem(setSize);
                var cur = Marshal.AllocCoTaskMem(curSize);
                try
                {
                    var now = ReadCurrent(a.Index);
                    if (now == null) return L.T("קריאת הערכים הנוכחיים נכשלה", "Reading the current values failed");
                    bool plus = (a.Caps & CapGfxLimits) != 0 && (a.Caps & CapGfxCurve) == 0;   // Navi 21 and newer

                    Zero(cur, curSize);
                    Marshal.WriteInt32(cur, 0, Od8Count);

                    // newer cards: leave the power presets for manual mode first (value 3), as AMD's sample does
                    if (plus && (a.Caps & CapPowerMode) != 0)
                    {
                        Zero(set, setSize);
                        Marshal.WriteInt32(set, 0, Od8Count);
                        Put(set, PowerMode, 3, reset: false);
                        int mode = ADL2_Overdrive8_Setting_Set(_context, a.Index, set, cur);
                        if (mode != Ok) return L.T($"המעבר למצב ידני נכשל (ADL {mode})", $"Switching to manual mode failed (ADL {mode})");
                    }

                    Zero(set, setSize);
                    Marshal.WriteInt32(set, 0, Od8Count);
                    int At(int id) => id < now.Length ? now[id] : 0;
                    if (plus)
                    {
                        // these pairs must be sent together
                        if (s.Id is GfxFmax or GfxFmin) { Put(set, GfxFmax, At(GfxFmax), false); Put(set, GfxFmin, At(GfxFmin), false); }
                        else if (s.Id == UclkFmax) { Put(set, UclkFmax, At(UclkFmax), false); Put(set, UclkFmin, At(UclkFmin), false); }
                    }
                    Put(set, s.Id, value, reset);
                    if (!plus && !reset)
                    {
                        // older Overdrive 8 cards: the top / bottom curve point follow the clock limit
                        if (s.Id == GfxFmax) Marshal.WriteInt32(set, Offset(GfxFreq3), value);
                        else if (s.Id == GfxFmin) Marshal.WriteInt32(set, Offset(GfxFreq1), value);
                    }

                    int rc = ADL2_Overdrive8_Setting_Set(_context, a.Index, set, cur);
                    Log.Info($"AMD {a.Name}: set {s.En} = {value} reset={reset} → ADL {rc}");
                    if (rc != Ok) return L.T($"{s.Name}: הדרייבר דחה את הערך (ADL {rc})", $"{s.Name}: the driver rejected the value (ADL {rc})");
                    var after = ReadCurrent(a.Index);
                    if (after != null && s.Id < after.Length) s.Current = after[s.Id];
                    return null;
                }
                catch (Exception ex) { Log.Warn("AMD write: " + ex.Message); return ex.Message; }
                finally { Marshal.FreeCoTaskMem(set); Marshal.FreeCoTaskMem(cur); }
            }
        }

        private static int Offset(int id) => 4 + id * 12;

        private static void Put(IntPtr set, int id, int value, bool reset)
        {
            int o = Offset(id);
            Marshal.WriteInt32(set, o, value);
            Marshal.WriteInt32(set, o + 4, 1);               // requested
            Marshal.WriteInt32(set, o + 8, reset ? 1 : 0);   // reset
        }

        private static void Zero(IntPtr p, int size) { for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0); }
    }
}
