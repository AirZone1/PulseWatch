using System;
using System.IO;

namespace PulseWatch.Core
{
    public static class AppPaths
    {
        /// <summary>True when a file named "portable" sits next to the exe: settings and log then live in a Data folder beside it.</summary>
        public static bool Portable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

        public static string DataDir { get; } = Portable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PulseWatch");
        public static string ConfigFile => Path.Combine(DataDir, "config.json");
        public static string LogFile => Path.Combine(DataDir, "pulsewatch.log");
        public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PulseWatch.exe");

        static AppPaths()
        {
            try { Directory.CreateDirectory(DataDir); } catch { }
        }
    }

    public static class Log
    {
        private static readonly object Gate = new();

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex}");

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    var fi = new FileInfo(AppPaths.LogFile);
                    if (fi.Exists && fi.Length > 2_000_000)
                        fi.Delete();
                    File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {msg}{Environment.NewLine}");
                }
            }
            catch { }
        }
    }
}
