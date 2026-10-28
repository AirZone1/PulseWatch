using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PulseWatch.Core
{
    /// <summary>
    /// The NvAPI calls MSI Afterburner relies on that the public SDK does not document:
    /// thermal sensors (hot spot / memory junction) and the voltage-frequency curve.
    /// Structure sizes were checked on this RTX 3080 (driver 621): curve status 0x1C28, control 0x2420, info 0x182C,
    /// 127 GPU points (450–1237.5 mV, 6.25 mV steps) followed by the memory point.
    /// </summary>
    public sealed class NvApiPrivate : IDisposable
    {
        private const uint IdThermal = 0x65FE3AAD;
        private const uint IdVfInfo = 0x507B4B59;      // ClkVfPointsGetInfo  (a.k.a. ClockBoostMask)
        private const uint IdVfStatus = 0x21537AD4;    // ClkVfPointsGetStatus (VFP curve)
        private const uint IdVfGetControl = 0x23F1B133; // ClkVfPointsGetControl (ClockBoostTable)
        private const uint IdVfSetControl = 0x0733E009; // ClkVfPointsSetControl

        private const int InfoSize = 0x182C, StatusSize = 0x1C28, ControlSize = 0x2420, ThermalSize = 168;
        private const int EntriesOffset = 68, StatusStride = 28, ControlStride = 36, InfoEntriesOffset = 52, InfoStride = 24;

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GpuFn(IntPtr gpu, IntPtr data);

        private readonly IntPtr _gpu;
        private readonly GpuFn? _thermal, _vfInfo, _vfStatus, _vfGet, _vfSet;
        private readonly IntPtr _thermalBuf, _statusBuf, _controlBuf;
        private uint _thermalMask;
        private readonly uint[] _mask = new uint[4];
        private readonly object _gate = new();

        /// <summary>Indices of real GPU voltage points (excludes the memory point).</summary>
        public int[] GpuPoints { get; private set; } = Array.Empty<int>();
        public bool CurveSupported => GpuPoints.Length > 0;

        private static GpuFn? Fn(uint id)
        {
            var p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<GpuFn>(p);
        }

        public NvApiPrivate(IntPtr nvapiGpuHandle)
        {
            _gpu = nvapiGpuHandle;
            _thermal = Fn(IdThermal);
            _vfInfo = Fn(IdVfInfo);
            _vfStatus = Fn(IdVfStatus);
            _vfGet = Fn(IdVfGetControl);
            _vfSet = Fn(IdVfSetControl);
            _thermalBuf = Marshal.AllocHGlobal(ThermalSize);
            _statusBuf = Marshal.AllocHGlobal(StatusSize);
            _controlBuf = Marshal.AllocHGlobal(ControlSize);
            DiscoverThermalMask();
            DiscoverCurve();
        }

        private static void Zero(IntPtr p, int size)
        {
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(p, i, 0);
        }

        private static void Header(IntPtr p, int size, int version)
        {
            Zero(p, size);
            Marshal.WriteInt32(p, 0, size | (version << 16));
        }

        // ───────────── thermal sensors ─────────────

        private void DiscoverThermalMask()
        {
            if (_thermal == null) return;
            for (int bit = 0; bit < 32; bit++)
            {
                uint mask = bit == 31 ? 0xFFFFFFFF : (1u << (bit + 1)) - 1;
                Header(_thermalBuf, ThermalSize, 2);
                Marshal.WriteInt32(_thermalBuf, 4, (int)mask);
                if (_thermal(_gpu, _thermalBuf) != 0) break;
                _thermalMask = mask;
            }
        }

        /// <summary>Hot spot and memory junction in °C (NaN when unavailable). ~1 ms.</summary>
        public (float hotspot, float memJunction) ReadThermals()
        {
            if (_thermal == null || _thermalMask == 0) return (float.NaN, float.NaN);
            lock (_gate)
            {
                Header(_thermalBuf, ThermalSize, 2);
                Marshal.WriteInt32(_thermalBuf, 4, (int)_thermalMask);
                if (_thermal(_gpu, _thermalBuf) != 0) return (float.NaN, float.NaN);
                float T(int i) => i < 32 && (_thermalMask & (1u << i)) != 0 ? Marshal.ReadInt32(_thermalBuf, 40 + i * 4) / 256f : float.NaN;
                float hot = T(1), mem = T(9);
                return (hot > 0 ? hot : float.NaN, mem > 0 ? mem : float.NaN);
            }
        }

        // ───────────── V/F curve ─────────────

        private void DiscoverCurve()
        {
            if (_vfInfo == null || _vfStatus == null || _vfGet == null || _vfSet == null) return;
            IntPtr info = Marshal.AllocHGlobal(InfoSize);
            try
            {
                Header(info, InfoSize, 1);
                for (int i = 0; i < 4; i++) Marshal.WriteInt32(info, 4 + i * 4, -1);
                if (_vfInfo(_gpu, info) != 0) return;
                for (int i = 0; i < 4; i++) _mask[i] = (uint)Marshal.ReadInt32(info, 4 + i * 4);
                var pts = new List<int>();
                for (int i = 0; i < 128; i++)
                {
                    if ((_mask[i / 32] & (1u << (i % 32))) == 0) continue;
                    // entry word 5 == 1 marks a graphics-clock point, word 4 == 1 a memory point
                    if (Marshal.ReadInt32(info, InfoEntriesOffset + i * InfoStride + 20) == 1) pts.Add(i);
                }
                GpuPoints = pts.ToArray();
            }
            catch (Exception ex) { Log.Warn("V/F curve discovery: " + ex.Message); GpuPoints = Array.Empty<int>(); }
            finally { Marshal.FreeHGlobal(info); }
        }

        public sealed class CurvePoint
        {
            public int Index;
            public double VoltageMv;
            /// <summary>Frequency the GPU would run at this voltage right now (includes global offset and point delta).</summary>
            public double FreqMhz;
            /// <summary>This point's own offset (Afterburner curve edit).</summary>
            public double DeltaMhz;
            public double BaseMhz => FreqMhz - DeltaMhz;
        }

        /// <summary>Reads the live curve (~3 ms). Returns empty when unsupported.</summary>
        public List<CurvePoint> ReadCurve()
        {
            var list = new List<CurvePoint>();
            if (!CurveSupported) return list;
            lock (_gate)
            {
                Header(_statusBuf, StatusSize, 1);
                Header(_controlBuf, ControlSize, 1);
                for (int i = 0; i < 4; i++)
                {
                    Marshal.WriteInt32(_statusBuf, 4 + i * 4, (int)_mask[i]);
                    Marshal.WriteInt32(_controlBuf, 4 + i * 4, (int)_mask[i]);
                }
                if (_vfStatus!(_gpu, _statusBuf) != 0 || _vfGet!(_gpu, _controlBuf) != 0) return list;
                foreach (int i in GpuPoints)
                {
                    uint f = (uint)Marshal.ReadInt32(_statusBuf, EntriesOffset + i * StatusStride + 4);
                    uint v = (uint)Marshal.ReadInt32(_statusBuf, EntriesOffset + i * StatusStride + 8);
                    int d = Marshal.ReadInt32(_controlBuf, EntriesOffset + i * ControlStride + 20);
                    if (v == 0) continue;
                    list.Add(new CurvePoint { Index = i, VoltageMv = v / 1000.0, FreqMhz = f / 1000.0, DeltaMhz = d / 1000.0 });
                }
            }
            return list;
        }

        /// <summary>Writes per-point offsets (MHz) for every GPU point; points missing from the map get 0.</summary>
        public string? WriteDeltas(IReadOnlyDictionary<int, int> deltasMhz)
        {
            if (!CurveSupported) return "Curve not supported";
            lock (_gate)
            {
                Header(_controlBuf, ControlSize, 1);
                for (int i = 0; i < 4; i++) Marshal.WriteInt32(_controlBuf, 4 + i * 4, (int)_mask[i]);
                if (_vfGet!(_gpu, _controlBuf) != 0) return "Read control failed";
                // only graphics points are written; the memory point keeps its current content
                var gpuMask = new uint[4];
                foreach (int i in GpuPoints)
                {
                    gpuMask[i / 32] |= 1u << (i % 32);
                    int mhz = deltasMhz.TryGetValue(i, out var d) ? Math.Clamp(d, -1000, 1000) : 0;
                    Marshal.WriteInt32(_controlBuf, EntriesOffset + i * ControlStride + 20, mhz * 1000);
                }
                for (int i = 0; i < 4; i++) Marshal.WriteInt32(_controlBuf, 4 + i * 4, (int)gpuMask[i]);
                int rc = _vfSet!(_gpu, _controlBuf);
                return rc == 0 ? null : $"NvAPI {rc}";
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(_thermalBuf);
            Marshal.FreeHGlobal(_statusBuf);
            Marshal.FreeHGlobal(_controlBuf);
        }
    }
}
