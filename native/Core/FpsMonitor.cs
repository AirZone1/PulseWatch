using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PulseWatch.Core
{
    /// <summary>
    /// Frames per second of every program, without touching the game: Windows' own event tracing (ETW) reports each
    /// frame a program hands to the screen — the DXGI provider for DirectX 10/11/12, the D3D9 provider for DirectX 9.
    /// This is the method PresentMon / CapFrameX use. Needs admin (PulseWatch has it). Vulkan and OpenGL games do not
    /// go through these providers and are not counted.
    /// Cost: one real-time session; per frame the callback reads three header fields and bumps a counter.
    /// Runs only while the OSD shows FPS.
    /// </summary>
    public sealed class FpsMonitor : IDisposable
    {
        private const string SessionName = "PulseWatch-FPS";
        private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");   // Microsoft-Windows-DXGI
        private static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");   // Microsoft-Windows-D3D9
        private const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3d9PresentStart = 1;

        // ── ETW (advapi32), x64 layouts ──
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern uint ControlTraceW(ulong handle, string? name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll")] private static extern ulong OpenTraceW(IntPtr logfile);
        [DllImport("advapi32.dll")] private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] private static extern uint CloseTrace(ulong handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RecordCallback(IntPtr record);

        private const int PropsSize = 120, LogfileSize = 448;
        private const uint ControlStop = 1, RealTimeMode = 0x100, TracedGuid = 0x20000, ErrorAlreadyExists = 183;
        private const uint ProcessRealTime = 0x100, ProcessEventRecord = 0x10000000;

        private ulong _session, _trace = ulong.MaxValue;
        private IntPtr _logfile, _loggerName;
        private RecordCallback? _callback;   // kept alive while ETW holds the pointer
        private Thread? _thread;

        private readonly object _gate = new();
        private Dictionary<int, int> _counts = new(), _spare = new();
        private Dictionary<int, double> _fps = new();
        private long _lastSample = Environment.TickCount64;

        public bool Running { get; private set; }

        public bool Start()
        {
            try
            {
                uint rc = StartSession();
                if (rc == ErrorAlreadyExists)
                {
                    // a session left behind by a crash: stop it and start fresh
                    var p = Props();
                    ControlTraceW(0, SessionName, p, ControlStop);
                    Marshal.FreeHGlobal(p);
                    rc = StartSession();
                }
                if (rc != 0) { Log.Warn($"FPS: StartTrace failed ({rc})"); return false; }

                var dxgi = Dxgi; var d3d9 = D3d9;
                EnableTraceEx2(_session, ref dxgi, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);
                EnableTraceEx2(_session, ref d3d9, 1, 4, ulong.MaxValue, 0, 0, IntPtr.Zero);

                _callback = OnRecord;
                _loggerName = Marshal.StringToHGlobalUni(SessionName);
                _logfile = Marshal.AllocHGlobal(LogfileSize);
                Zero(_logfile, LogfileSize);
                Marshal.WriteIntPtr(_logfile, 8, _loggerName);                                   // LoggerName
                Marshal.WriteInt32(_logfile, 28, unchecked((int)(ProcessRealTime | ProcessEventRecord))); // ProcessTraceMode
                Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback));    // EventRecordCallback
                _trace = OpenTraceW(_logfile);
                if (_trace == ulong.MaxValue) { Log.Warn("FPS: OpenTrace failed"); Stop(); return false; }

                _thread = new Thread(() =>
                {
                    try { ProcessTrace(new[] { _trace }, 1, IntPtr.Zero, IntPtr.Zero); }
                    catch (Exception ex) { Log.Warn("FPS: " + ex.Message); }
                }) { IsBackground = true, Name = "FpsMonitor", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
                Running = true;
                Log.Info("FPS monitor started");
                return true;
            }
            catch (Exception ex) { Log.Warn("FPS: " + ex.Message); Stop(); return false; }
        }

        private uint StartSession()
        {
            var p = Props();
            try { return StartTraceW(out _session, SessionName, p); }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>EVENT_TRACE_PROPERTIES (120 bytes on x64) followed by room for the session name.</summary>
        private static IntPtr Props()
        {
            int size = PropsSize + (SessionName.Length + 1) * 2 + 16;
            var p = Marshal.AllocHGlobal(size);
            Zero(p, size);
            Marshal.WriteInt32(p, 0, size);                      // Wnode.BufferSize
            Marshal.WriteInt32(p, 40, 1);                        // Wnode.ClientContext = QPC
            Marshal.WriteInt32(p, 44, unchecked((int)TracedGuid)); // Wnode.Flags
            Marshal.WriteInt32(p, 48, 64);                       // BufferSize (KB)
            Marshal.WriteInt32(p, 64, unchecked((int)RealTimeMode)); // LogFileMode
            Marshal.WriteInt32(p, 68, 1);                        // FlushTimer (s)
            Marshal.WriteInt32(p, 116, PropsSize);               // LoggerNameOffset
            return p;
        }

        private static void Zero(IntPtr p, int size) { for (int i = 0; i < size; i += 8) Marshal.WriteInt64(p, i, 0); }

        /// <summary>EVENT_RECORD starts with EVENT_HEADER: ProcessId @12, ProviderId @24, EventDescriptor.Id @40.</summary>
        private void OnRecord(IntPtr r)
        {
            ushort id = (ushort)Marshal.ReadInt16(r, 40);
            if (id != DxgiPresentStart && id != DxgiPresentMpoStart && id != D3d9PresentStart) return;
            long a = Marshal.ReadInt64(r, 24), b = Marshal.ReadInt64(r, 32);
            bool frame = id == D3d9PresentStart ? a == D3d9Lo && b == D3d9Hi : a == DxgiLo && b == DxgiHi;
            if (!frame) return;
            int pid = Marshal.ReadInt32(r, 12);
            lock (_gate) { _counts.TryGetValue(pid, out int n); _counts[pid] = n + 1; }
        }

        private static readonly long DxgiLo = BitConverter.ToInt64(Dxgi.ToByteArray(), 0), DxgiHi = BitConverter.ToInt64(Dxgi.ToByteArray(), 8);
        private static readonly long D3d9Lo = BitConverter.ToInt64(D3d9.ToByteArray(), 0), D3d9Hi = BitConverter.ToInt64(D3d9.ToByteArray(), 8);

        /// <summary>Call about once a second: turns the frame counts since the last call into FPS per process.</summary>
        public void Sample()
        {
            long now = Environment.TickCount64;
            double sec = (now - _lastSample) / 1000.0;
            if (sec < 0.5) return;
            _lastSample = now;
            Dictionary<int, int> c;
            lock (_gate) { c = _counts; _counts = _spare; }
            var fps = new Dictionary<int, double>(c.Count);
            foreach (var kv in c) fps[kv.Key] = kv.Value / sec;
            c.Clear();
            _spare = c;
            _fps = fps;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        /// <summary>FPS and name of the program in the foreground (the game being played), or null if it draws nothing.</summary>
        public (double fps, string name)? Foreground()
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out int pid);
            return pid != 0 && _fps.TryGetValue(pid, out double f) && f >= 1 ? (f, Where(pid).name) : null;
        }

        /// <summary>
        /// The program drawing the most frames whose main window is on the given screen (display device name), or null.
        /// The screen and name of each process are looked up once per 5 s, and only for processes that draw.
        /// </summary>
        public (double fps, string name)? OnScreen(string device)
        {
            (double fps, string name)? best = null;
            foreach (var kv in _fps)
            {
                if (kv.Value < 1) continue;
                var w = Where(kv.Key);
                if (w.screen != device || w.name is "dwm" or "PulseWatch" or "") continue;
                if (best == null || kv.Value > best.Value.fps) best = (kv.Value, w.name);
            }
            return best;
        }

        private readonly Dictionary<int, (string screen, string name, long at)> _where = new();

        private (string screen, string name) Where(int pid)
        {
            long now = Environment.TickCount64;
            if (_where.TryGetValue(pid, out var w) && now - w.at < 5000) return (w.screen, w.name);
            string screen = "", name = "";
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                name = p.ProcessName;
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero) screen = System.Windows.Forms.Screen.FromHandle(h).DeviceName;
            }
            catch { }
            if (_where.Count > 64) _where.Clear();
            _where[pid] = (screen, name, now);
            return (screen, name);
        }

        public void Stop()
        {
            Running = false;
            if (_session != 0)
            {
                var p = Props();
                ControlTraceW(_session, null, p, ControlStop);
                Marshal.FreeHGlobal(p);
                _session = 0;
            }
            if (_trace != ulong.MaxValue) { CloseTrace(_trace); _trace = ulong.MaxValue; }
            _thread?.Join(2000);
            _thread = null;
            if (_logfile != IntPtr.Zero) { Marshal.FreeHGlobal(_logfile); _logfile = IntPtr.Zero; }
            if (_loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(_loggerName); _loggerName = IntPtr.Zero; }
        }

        public void Dispose() => Stop();
    }
}
