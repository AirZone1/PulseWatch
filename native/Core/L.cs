using System.Globalization;

namespace PulseWatch.Core
{
    /// <summary>
    /// UI language. Every user-facing string is written as a Hebrew/English pair at the call site:
    /// <c>L.T("שקט", "Silent")</c> — no keys to keep in sync, and nothing can go missing.
    /// </summary>
    public static class L
    {
        public static bool En { get; private set; }
        public static string Code => En ? "en" : "he";
        public static CultureInfo Culture => CultureInfo.GetCultureInfo(En ? "en-US" : "he-IL");

        public static void Set(string? code) => En = code == "en";

        public static string T(string he, string en) => En ? en : he;
    }
}
