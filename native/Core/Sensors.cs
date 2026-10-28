using System;

namespace PulseWatch.Core
{
    /// <summary>Fixed-size history (one sample per hub tick). Thread-safe for one writer + many readers.</summary>
    public sealed class Ring
    {
        private readonly float[] _data;
        private int _head;   // next write position
        private int _count;

        public Ring(int capacity, int prefillNaN = 0)
        {
            _data = new float[capacity];
            for (int i = 0; i < Math.Min(prefillNaN, capacity); i++) Add(float.NaN);
        }

        public int Capacity => _data.Length;
        public int Count { get { lock (_data) return _count; } }

        public void Add(float v)
        {
            lock (_data)
            {
                _data[_head] = v;
                _head = (_head + 1) % _data.Length;
                if (_count < _data.Length) _count++;
            }
        }

        /// <summary>Copies the newest <paramref name="n"/> samples, oldest first. Missing samples are NaN.</summary>
        public float[] Last(int n)
        {
            var res = new float[n];
            lock (_data)
            {
                for (int i = 0; i < n; i++)
                {
                    int back = n - i; // 1 = newest
                    if (back > _count) { res[i] = float.NaN; continue; }
                    int idx = (_head - back + _data.Length) % _data.Length;
                    res[i] = _data[idx];
                }
            }
            return res;
        }
    }

    public sealed class SensorInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Hardware { get; init; } = "";
        public string HardwareKind { get; init; } = "";
        public string Type { get; init; } = "";
        public string Unit { get; init; } = "";
        public string? Label { get; set; }
        /// <summary>A motherboard sensor that actually measures the CPU (excluded from "hottest board sensor").</summary>
        public bool CpuMirror { get; init; }
        public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label!;

        public float Value = float.NaN;
        public float Min = float.NaN;
        public float Max = float.NaN;
        public Ring History { get; init; } = new Ring(3600);

        public bool HasValue => !float.IsNaN(Value);

        public void Set(float v)
        {
            Value = v;
            if (float.IsNaN(v)) return;
            if (float.IsNaN(Min) || v < Min) Min = v;
            if (float.IsNaN(Max) || v > Max) Max = v;
        }

        public void ResetMinMax() { Min = Value; Max = Value; }

        public string Format(float v)
        {
            if (float.IsNaN(v)) return "—";
            return Type switch
            {
                "Temperature" => $"{v:0.0}",
                "Voltage" => $"{v:0.000}",
                "Fan" or "Clock" => $"{v:0}",
                "Power" => v >= 100 ? $"{v:0}" : $"{v:0.0}",
                "Data" => $"{v:0.00}",
                "SmallData" => $"{v:0}",
                "Throughput" => v >= 10 ? $"{v:0}" : $"{v:0.00}",
                _ => v >= 100 ? $"{v:0}" : $"{v:0.0}"
            };
        }
    }

    public enum ChannelKind { Motherboard, Gpu, AioFans, Pump, NvmeFan }
}
