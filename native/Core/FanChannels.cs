using System;
using LibreHardwareMonitor.Hardware;

namespace PulseWatch.Core
{
    /// <summary>One controllable fan output. All calls happen on the hub thread.</summary>
    public abstract class FanChannel
    {
        public string Id { get; protected init; } = "";
        public string DefaultName { get; protected init; } = "";
        public string Group { get; protected init; } = "";
        public ChannelKind Kind { get; protected init; }
        public string ConflictGroup { get; protected init; } = "";
        /// <summary>Lowest duty the hardware accepts.</summary>
        public int MinPct { get; protected init; }
        /// <summary>0% from a curve means "hand back to firmware" (GPU zero-RPM).</summary>
        public bool ZeroMeansAuto { get; protected init; }
        public string AutoLabel { get; protected init; } = "";
        /// <summary>What this header is / where it sits on the board (from the board profile); shown as an ⓘ.</summary>
        public string Help { get; init; } = "";
        public string? RpmSensorId { get; protected init; }
        public string? PctSensorId { get; protected init; }

        // Live state, written by the engine
        public double? AppliedPct { get; internal set; }
        public bool InAuto { get; internal set; } = true;
        public bool Blocked { get; internal set; }
        public string? LastError { get; internal set; }
        public double? SourceTemp { get; internal set; }
        public double? TargetPct { get; internal set; }
        public DateTime LastWrite { get; internal set; } = DateTime.MinValue;
        public TimeSpan Refresh { get; protected init; } = TimeSpan.FromSeconds(5);
        public bool Emergency { get; internal set; }

        public abstract void Apply(int pct);
        public abstract void ApplyAuto();
    }

    public sealed class SuperIoChannel : FanChannel
    {
        private readonly ISensor _control;

        public SuperIoChannel(ISensor control, ISensor? fan, string chipName, ChannelKind kind = ChannelKind.Motherboard, string? name = null)
        {
            _control = control;
            Id = "mb:" + control.Identifier;
            DefaultName = name ?? control.Name;
            Group = L.T($"לוח אם · {chipName}", $"Motherboard · {chipName}");
            Kind = kind;
            ConflictGroup = "gcc";
            MinPct = 0;
            // the tach sensor may not be active yet when the fan was stopped at start-up; its id is predictable
            RpmSensorId = fan?.Identifier.ToString() ?? control.Identifier.ToString().Replace("/control/", "/fan/");
            PctSensorId = control.Identifier.ToString();
            AutoLabel = L.T("אוטומטי (Smart Fan של ה-BIOS)", "Auto (BIOS Smart Fan)");
        }

        public override void Apply(int pct) => _control.Control.SetSoftware(Math.Clamp(pct, 0, 100));
        public override void ApplyAuto() => _control.Control.SetDefault();
    }

    /// <summary>
    /// A fan/pump output LibreHardwareMonitor exposes on other hardware: fan controllers and hubs (NZXT, Aquacomputer,
    /// T-Balancer, …) and AMD / Intel graphics cards. Starts in Auto (nothing is written) until the user picks a mode.
    /// </summary>
    public sealed class LhmControlChannel : FanChannel
    {
        private readonly ISensor _control;

        public LhmControlChannel(ISensor control, ISensor? fan, IHardware hw)
        {
            _control = control;
            Id = "lhm:" + control.Identifier;
            DefaultName = control.Name;
            bool gpu = hw.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel;
            Group = gpu ? L.T($"כרטיס מסך · {hw.Name}", $"Graphics card · {hw.Name}")
                  : hw.HardwareType == HardwareType.Psu ? L.T($"ספק כוח · {hw.Name}", $"Power supply · {hw.Name}")
                  : L.T($"בקר מאווררים · {hw.Name}", $"Fan controller · {hw.Name}");
            Kind = ChannelKind.Motherboard;
            MinPct = 0;
            RpmSensorId = fan?.Identifier.ToString();
            PctSensorId = control.Identifier.ToString();
            AutoLabel = gpu ? L.T("אוטומטי (דרייבר)", "Auto (driver)") : L.T("אוטומטי (הבקר)", "Auto (controller)");
        }

        public override void Apply(int pct) => _control.Control.SetSoftware(Math.Clamp(pct, 0, 100));
        public override void ApplyAuto() => _control.Control.SetDefault();
    }

    public sealed class GpuFanChannel : FanChannel
    {
        private readonly NvidiaGpu _gpu;

        public GpuFanChannel(NvidiaGpu gpu, string? rpmSensorId)
        {
            _gpu = gpu;
            Id = "gpu:fans";
            string model = gpu.Name.Replace("NVIDIA GeForce ", "");
            DefaultName = L.T($"מאווררי {model} ({gpu.Limits.FanCount})", $"{model} fans ({gpu.Limits.FanCount})");
            Group = L.T("כרטיס מסך · NVIDIA", "Graphics card · NVIDIA");
            Kind = ChannelKind.Gpu;
            ConflictGroup = "afterburner";
            MinPct = gpu.Limits.FanMinPct;
            ZeroMeansAuto = true;
            AutoLabel = L.T("אוטומטי (דרייבר · Zero RPM)", "Auto (driver · Zero RPM)");
            RpmSensorId = rpmSensorId;
            PctSensorId = "virt/gpu/fanpct";
            Refresh = TimeSpan.FromSeconds(10);
        }

        public override void Apply(int pct)
        {
            var err = _gpu.SetFans(pct);
            if (err != null) throw new InvalidOperationException(err);
        }

        public override void ApplyAuto()
        {
            var err = _gpu.SetFansAuto();
            if (err != null) throw new InvalidOperationException(err);
        }
    }

    public sealed class TrinityFanChannel : FanChannel
    {
        private readonly LianLiTrinity _aio;

        public TrinityFanChannel(LianLiTrinity aio)
        {
            _aio = aio;
            Id = "lianli:fans";
            DefaultName = L.T("מאווררי רדיאטור", "Radiator fans");
            Group = $"Lian Li · {aio.Model}";
            Kind = ChannelKind.AioFans;
            ConflictGroup = "lconnect";
            MinPct = aio.FanMinPct;
            AutoLabel = L.T("סנכרון ללוח האם (MB Sync)", "Motherboard sync (MB Sync)");
            RpmSensorId = "virt/aio/fan";
            Refresh = TimeSpan.FromSeconds(20);
        }

        public override void Apply(int pct) => _aio.SetFanPct(pct);
        public override void ApplyAuto() => _aio.SetFanPct(_aio.LastFanPct ?? 60, mbSync: true);
    }

    public sealed class TrinityPumpChannel : FanChannel
    {
        private readonly LianLiTrinity _aio;

        public TrinityPumpChannel(LianLiTrinity aio)
        {
            _aio = aio;
            Id = "lianli:pump";
            DefaultName = L.T("משאבה", "Pump");
            Group = $"Lian Li · {aio.Model}";
            Kind = ChannelKind.Pump;
            ConflictGroup = "lconnect";
            MinPct = aio.PumpMinPct;
            AutoLabel = L.T("סנכרון ללוח האם (MB Sync)", "Motherboard sync (MB Sync)");
            RpmSensorId = "virt/aio/pump";
            Refresh = TimeSpan.FromSeconds(20);
        }

        public override void Apply(int pct) => _aio.SetPumpPct(pct);
        public override void ApplyAuto() => _aio.SetPumpPct(_aio.LastPumpPct ?? 100, mbSync: true);
    }
}
