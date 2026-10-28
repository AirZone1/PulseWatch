using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;

namespace PulseWatch.Core
{
    /// <summary>Thin P/Invoke layer over NVIDIA's official management library (nvml.dll, ships with the driver).</summary>
    internal static class NvmlApi
    {
        private const string D = "nvml.dll";

        public const int ClockGraphics = 0, ClockSm = 1, ClockMem = 2, ClockVideo = 3;
        public const int TempThresholdShutdown = 0, TempThresholdSlowdown = 1, TempThresholdGpuMax = 3,
            TempThresholdAcousticMin = 4, TempThresholdAcousticCurr = 5, TempThresholdAcousticMax = 6;

        [StructLayout(LayoutKind.Sequential)]
        public struct Utilization { public uint Gpu; public uint Memory; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Memory { public ulong Total; public ulong Free; public ulong Used; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ClockOffset
        {
            public uint Version;
            public int Type;
            public int PState;
            public int ClockOffsetMHz;
            public int MinClockOffsetMHz;
            public int MaxClockOffsetMHz;
            public static ClockOffset Create(int type, int pstate) => new()
            {
                Version = (uint)(Marshal.SizeOf<ClockOffset>() | (1 << 24)),
                Type = type,
                PState = pstate
            };
        }

        [DllImport(D, EntryPoint = "nvmlInit_v2")] public static extern int Init();
        [DllImport(D, EntryPoint = "nvmlShutdown")] public static extern int Shutdown();
        [DllImport(D, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern int GetHandle(uint index, out IntPtr dev);
        [DllImport(D, EntryPoint = "nvmlDeviceGetName")] public static extern int GetName(IntPtr dev, StringBuilder name, uint len);
        [DllImport(D, EntryPoint = "nvmlSystemGetDriverVersion")] public static extern int GetDriverVersion(StringBuilder ver, uint len);
        [DllImport(D, EntryPoint = "nvmlDeviceGetTemperature")] public static extern int GetTemperature(IntPtr dev, int sensor, out uint t);
        [DllImport(D, EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern int GetPowerUsage(IntPtr dev, out uint mw);
        [DllImport(D, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] public static extern int GetPowerLimit(IntPtr dev, out uint mw);
        [DllImport(D, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] public static extern int SetPowerLimit(IntPtr dev, uint mw);
        [DllImport(D, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern int GetPowerLimitConstraints(IntPtr dev, out uint min, out uint max);
        [DllImport(D, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] public static extern int GetPowerDefaultLimit(IntPtr dev, out uint mw);
        [DllImport(D, EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern int GetUtilization(IntPtr dev, out Utilization u);
        [DllImport(D, EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern int GetMemory(IntPtr dev, out Memory m);
        [DllImport(D, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern int GetClock(IntPtr dev, int type, out uint mhz);
        [DllImport(D, EntryPoint = "nvmlDeviceGetMaxClockInfo")] public static extern int GetMaxClock(IntPtr dev, int type, out uint mhz);
        [DllImport(D, EntryPoint = "nvmlDeviceGetNumFans")] public static extern int GetNumFans(IntPtr dev, out uint n);
        [DllImport(D, EntryPoint = "nvmlDeviceGetFanSpeed_v2")] public static extern int GetFanSpeed(IntPtr dev, uint fan, out uint pct);
        [DllImport(D, EntryPoint = "nvmlDeviceGetTargetFanSpeed")] public static extern int GetTargetFanSpeed(IntPtr dev, uint fan, out uint pct);
        [DllImport(D, EntryPoint = "nvmlDeviceSetFanSpeed_v2")] public static extern int SetFanSpeed(IntPtr dev, uint fan, uint pct);
        [DllImport(D, EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2")] public static extern int SetDefaultFanSpeed(IntPtr dev, uint fan);
        [DllImport(D, EntryPoint = "nvmlDeviceGetMinMaxFanSpeed")] public static extern int GetMinMaxFanSpeed(IntPtr dev, out uint min, out uint max);
        [DllImport(D, EntryPoint = "nvmlDeviceGetClockOffsets")] public static extern int GetClockOffsets(IntPtr dev, ref ClockOffset o);
        [DllImport(D, EntryPoint = "nvmlDeviceSetClockOffsets")] public static extern int SetClockOffsets(IntPtr dev, ref ClockOffset o);
        [DllImport(D, EntryPoint = "nvmlDeviceGetTemperatureThreshold")] public static extern int GetTempThreshold(IntPtr dev, int type, out uint t);
        [DllImport(D, EntryPoint = "nvmlDeviceSetTemperatureThreshold")] public static extern int SetTempThreshold(IntPtr dev, int type, ref int t);
        [DllImport(D, EntryPoint = "nvmlDeviceGetCurrentClocksEventReasons")] public static extern int GetClockEventReasons(IntPtr dev, out ulong reasons);
        [DllImport(D, EntryPoint = "nvmlDeviceGetPerformanceState")] public static extern int GetPState(IntPtr dev, out int pstate);
        [DllImport(D, EntryPoint = "nvmlErrorString")] private static extern IntPtr ErrorString(int rc);

        public static string Error(int rc)
        {
            try { return Marshal.PtrToStringAnsi(ErrorString(rc)) ?? $"NVML {rc}"; }
            catch { return $"NVML {rc}"; }
        }
    }

    public class GpuLimits
    {
        public int PowerMinW, PowerMaxW, PowerDefaultW;
        /// <summary>Offsets are in MSI Afterburner units (NvAPI P-state 2.0 deltas).</summary>
        public int CoreOffsetMin = -1000, CoreOffsetMax = 1000;
        public int MemOffsetMin = -1000, MemOffsetMax = 3000;
        public int TempLimitMin = 65, TempLimitMax = 90;
        public int FanMinPct = 30, FanMaxPct = 100;
        public int FanCount = 1;
        public int MaxCoreClock, MaxMemClock;
        public int ShutdownTemp, SlowdownTemp;
        /// <summary>Highest temperature the card is specified to run at (NVML "GPU max"), 0 if unknown.</summary>
        public int GpuMaxTemp;
    }

    public class GpuLive
    {
        public double TempCore = double.NaN, Hotspot = double.NaN, MemJunction = double.NaN;
        public double PowerW, PowerLimitW;
        public double VoltageV = double.NaN;
        public int CoreOffset, MemOffset, TempLimit;
        public int FanPct, FanTargetPct;
        public int[] FanRpm = Array.Empty<int>();
        public int PState;
        public ulong Reasons;
        public int ClockCore, ClockMem;
        public int LoadGpu, LoadMem;
        public double VramUsedMb, VramTotalMb;
    }

    /// <summary>
    /// NVIDIA GPU: NVML for telemetry, fans, power and temperature target; NvAPI P-states 2.0 for clock offsets
    /// (identical units to MSI Afterburner — NVML's memory offset is 2× larger, measured on this card);
    /// private NvAPI for hot spot / memory junction and the V/F curve.
    /// </summary>
    public sealed class NvidiaGpu : IDisposable
    {
        private readonly IntPtr _dev;
        private readonly PhysicalGPUHandle? _nv;
        public NvApiPrivate? Private { get; }
        public string Name { get; } = "NVIDIA GPU";
        public string Driver { get; } = "";
        public GpuLimits Limits { get; } = new();
        public bool FanManual { get; private set; }
        private GpuLive _last = new();
        private int _readCount;

        private NvidiaGpu(IntPtr dev)
        {
            _dev = dev;
            var sb = new StringBuilder(96);
            if (NvmlApi.GetName(dev, sb, 96) == 0) Name = sb.ToString();
            sb.Clear();
            if (NvmlApi.GetDriverVersion(sb, 96) == 0) Driver = sb.ToString();

            if (NvmlApi.GetPowerLimitConstraints(dev, out uint pmin, out uint pmax) == 0)
            {
                Limits.PowerMinW = (int)(pmin / 1000);
                Limits.PowerMaxW = (int)(pmax / 1000);
            }
            if (NvmlApi.GetPowerDefaultLimit(dev, out uint pdef) == 0) Limits.PowerDefaultW = (int)(pdef / 1000);
            if (NvmlApi.GetTempThreshold(dev, NvmlApi.TempThresholdAcousticMin, out uint tmin) == 0) Limits.TempLimitMin = (int)tmin;
            if (NvmlApi.GetTempThreshold(dev, NvmlApi.TempThresholdAcousticMax, out uint tmax) == 0) Limits.TempLimitMax = (int)tmax;
            if (NvmlApi.GetTempThreshold(dev, NvmlApi.TempThresholdShutdown, out uint tsd) == 0) Limits.ShutdownTemp = (int)tsd;
            if (NvmlApi.GetTempThreshold(dev, NvmlApi.TempThresholdSlowdown, out uint tsl) == 0) Limits.SlowdownTemp = (int)tsl;
            if (NvmlApi.GetTempThreshold(dev, NvmlApi.TempThresholdGpuMax, out uint tgm) == 0) Limits.GpuMaxTemp = (int)tgm;
            if (NvmlApi.GetMinMaxFanSpeed(dev, out uint fmin, out uint fmax) == 0) { Limits.FanMinPct = (int)fmin; Limits.FanMaxPct = (int)fmax; }
            if (NvmlApi.GetNumFans(dev, out uint nf) == 0 && nf > 0) Limits.FanCount = (int)nf;
            if (NvmlApi.GetMaxClock(dev, NvmlApi.ClockGraphics, out uint mcc) == 0) Limits.MaxCoreClock = (int)mcc;
            if (NvmlApi.GetMaxClock(dev, NvmlApi.ClockMem, out uint mmc) == 0) Limits.MaxMemClock = (int)mmc;

            try
            {
                NvAPIWrapper.NVIDIA.Initialize();
                var gpus = PhysicalGPU.GetPhysicalGPUs();
                if (gpus.Length > 0)
                {
                    _nv = gpus[0].Handle;
                    Private = new NvApiPrivate(_nv.Value.MemoryAddress);
                    var ps = GPUApi.GetPerformanceStates20(_nv.Value);
                    foreach (var c in ps.Clocks[PerformanceStateId.P0_3DPerformance])
                    {
                        if (c.DomainId == PublicClockDomain.Graphics) { Limits.CoreOffsetMin = c.FrequencyDeltaInkHz.DeltaRange.Minimum / 1000; Limits.CoreOffsetMax = c.FrequencyDeltaInkHz.DeltaRange.Maximum / 1000; }
                        if (c.DomainId == PublicClockDomain.Memory) { Limits.MemOffsetMin = c.FrequencyDeltaInkHz.DeltaRange.Minimum / 1000; Limits.MemOffsetMax = c.FrequencyDeltaInkHz.DeltaRange.Maximum / 1000; }
                    }
                }
            }
            catch (Exception ex) { Log.Warn("NvAPI unavailable, using NVML only: " + ex.Message); }
        }

        public static NvidiaGpu? TryOpen()
        {
            try
            {
                if (NvmlApi.Init() != 0) return null;
                if (NvmlApi.GetHandle(0, out var dev) != 0 || dev == IntPtr.Zero) return null;
                return new NvidiaGpu(dev);
            }
            catch (Exception ex)
            {
                Log.Warn("NVML unavailable: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// One telemetry sample. Fast values (NVML + private thermals, ~1.5 ms) every call;
        /// core voltage and fan RPM (~6 ms) only when <paramref name="full"/>.
        /// </summary>
        public GpuLive Read(bool full)
        {
            var l = new GpuLive { VoltageV = _last.VoltageV, FanRpm = _last.FanRpm };
            _readCount++;
            if (NvmlApi.GetTemperature(_dev, 0, out uint t) == 0) l.TempCore = t;
            if (NvmlApi.GetPowerUsage(_dev, out uint pw) == 0) l.PowerW = pw / 1000.0;
            if (NvmlApi.GetPowerLimit(_dev, out uint pl) == 0) l.PowerLimitW = pl / 1000.0;
            if (NvmlApi.GetFanSpeed(_dev, 0, out uint fs) == 0) l.FanPct = (int)fs;
            if (NvmlApi.GetTargetFanSpeed(_dev, 0, out uint ft) == 0) l.FanTargetPct = (int)ft;
            if (NvmlApi.GetPState(_dev, out int ps) == 0) l.PState = ps;
            if (NvmlApi.GetClockEventReasons(_dev, out ulong r) == 0) l.Reasons = r;
            if (NvmlApi.GetClock(_dev, NvmlApi.ClockGraphics, out uint cc) == 0) l.ClockCore = (int)cc;
            if (NvmlApi.GetClock(_dev, NvmlApi.ClockMem, out uint mc) == 0) l.ClockMem = (int)mc;
            if (NvmlApi.GetUtilization(_dev, out var u) == 0) { l.LoadGpu = (int)u.Gpu; l.LoadMem = (int)u.Memory; }
            if (NvmlApi.GetMemory(_dev, out var m) == 0) { l.VramUsedMb = m.Used / 1048576.0; l.VramTotalMb = m.Total / 1048576.0; }
            if (Private != null) (l.Hotspot, l.MemJunction) = Private.ReadThermals();

            // settings rarely change: refresh every 5th sample
            if (_readCount % 5 == 1)
            {
                var co = NvmlApi.ClockOffset.Create(NvmlApi.ClockGraphics, 0);
                if (NvmlApi.GetClockOffsets(_dev, ref co) == 0) l.CoreOffset = co.ClockOffsetMHz;
                var mo = NvmlApi.ClockOffset.Create(NvmlApi.ClockMem, 0);
                if (NvmlApi.GetClockOffsets(_dev, ref mo) == 0) l.MemOffset = mo.ClockOffsetMHz / 2; // NVML = 2× Afterburner
                if (NvmlApi.GetTempThreshold(_dev, NvmlApi.TempThresholdAcousticCurr, out uint tl) == 0) l.TempLimit = (int)tl;
            }
            else { l.CoreOffset = _last.CoreOffset; l.MemOffset = _last.MemOffset; l.TempLimit = _last.TempLimit; }

            if (full && _nv != null)
            {
                try { l.VoltageV = GPUApi.GetCurrentVoltage(_nv.Value).ValueInMicroVolt / 1e6; } catch { }
                try { l.FanRpm = GPUApi.GetClientFanCoolersStatus(_nv.Value).FanCoolersStatusEntries.Select(f => (int)f.CurrentRPM).ToArray(); } catch { }
            }
            _last = l;
            return l;
        }

        /// <summary>Forces the next read to refresh offsets/limits (after applying settings).</summary>
        public void Invalidate() => _readCount = 0;

        public string? SetFans(int pct)
        {
            pct = Math.Clamp(pct, Limits.FanMinPct, Limits.FanMaxPct);
            string? err = null;
            for (uint i = 0; i < Limits.FanCount; i++)
            {
                int rc = NvmlApi.SetFanSpeed(_dev, i, (uint)pct);
                if (rc != 0) err = NvmlApi.Error(rc);
            }
            if (err == null) FanManual = true;
            return err;
        }

        public string? SetFansAuto()
        {
            string? err = null;
            for (uint i = 0; i < Limits.FanCount; i++)
            {
                int rc = NvmlApi.SetDefaultFanSpeed(_dev, i);
                if (rc != 0) err = NvmlApi.Error(rc);
            }
            if (err == null) FanManual = false;
            return err;
        }

        public string? SetPowerLimit(int watts)
        {
            watts = Math.Clamp(watts, Limits.PowerMinW, Limits.PowerMaxW);
            int rc = NvmlApi.SetPowerLimit(_dev, (uint)(watts * 1000));
            return rc == 0 ? null : NvmlApi.Error(rc);
        }

        /// <summary>Core and memory offsets in Afterburner units, applied to P0 through NvAPI P-states 2.0.</summary>
        public string? SetOffsets(int coreMhz, int memMhz)
        {
            coreMhz = Math.Clamp(coreMhz, Limits.CoreOffsetMin, Limits.CoreOffsetMax);
            memMhz = Math.Clamp(memMhz, Limits.MemOffsetMin, Limits.MemOffsetMax);
            Invalidate();
            if (_nv != null)
            {
                try
                {
                    var clocks = new[]
                    {
                        new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(coreMhz * 1000)),
                        new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memMhz * 1000))
                    };
                    var info = new PerformanceStates20InfoV1(new[] { new PerformanceStates20InfoV1.PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, Array.Empty<PerformanceStates20BaseVoltageEntryV1>()) }, 2, 0);
                    GPUApi.SetPerformanceStates20(_nv.Value, info);
                    return null;
                }
                catch (Exception ex) { Log.Warn("Pstates20 set failed, falling back to NVML: " + ex.Message); }
            }
            var co = NvmlApi.ClockOffset.Create(NvmlApi.ClockGraphics, 0);
            co.ClockOffsetMHz = coreMhz;
            int rc = NvmlApi.SetClockOffsets(_dev, ref co);
            if (rc != 0) return NvmlApi.Error(rc);
            var mo = NvmlApi.ClockOffset.Create(NvmlApi.ClockMem, 0);
            mo.ClockOffsetMHz = memMhz * 2;
            rc = NvmlApi.SetClockOffsets(_dev, ref mo);
            return rc == 0 ? null : NvmlApi.Error(rc);
        }

        public string? SetTempLimit(int c)
        {
            int v = Math.Clamp(c, Limits.TempLimitMin, Limits.TempLimitMax);
            Invalidate();
            int rc = NvmlApi.SetTempThreshold(_dev, NvmlApi.TempThresholdAcousticCurr, ref v);
            return rc == 0 ? null : NvmlApi.Error(rc);
        }

        /// <summary>Human-readable explanation of why clocks are being held back right now.</summary>
        public static string DescribeReasons(ulong r)
        {
            if (r == 0) return L.T("ללא הגבלה · בוסט מלא", "Nothing · full boost");
            var parts = new System.Collections.Generic.List<string>();
            if ((r & 0x1) != 0) parts.Add(L.T("סרק (Idle)", "Idle"));
            if ((r & 0x4) != 0) parts.Add(L.T("מגבלת הספק (Power)", "Power limit"));
            if ((r & 0x8) != 0) parts.Add(L.T("האטת חומרה", "HW slowdown"));
            if ((r & 0x20) != 0) parts.Add(L.T("מגבלת טמפ' (Thermal)", "Thermal limit"));
            if ((r & 0x40) != 0) parts.Add(L.T("האטה תרמית בחומרה", "HW thermal slowdown"));
            if ((r & 0x80) != 0) parts.Add(L.T("בלם הספק (PSU)", "Power brake (PSU)"));
            if ((r & 0x2) != 0) parts.Add("Application clocks");
            if ((r & 0x100) != 0) parts.Add(L.T("שעון תצוגה", "Display clock"));
            if ((r & 0x10) != 0) parts.Add("Sync boost");
            return parts.Count == 0 ? $"0x{r:X}" : string.Join(" · ", parts);
        }

        public void Dispose()
        {
            try { if (FanManual) SetFansAuto(); } catch { }
            try { Private?.Dispose(); } catch { }
            try { NvmlApi.Shutdown(); } catch { }
        }
    }
}
