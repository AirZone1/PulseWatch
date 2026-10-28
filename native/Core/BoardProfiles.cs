using System;
using System.Collections.Generic;
using System.Linq;

namespace PulseWatch.Core
{
    /// <summary>
    /// Board-specific knowledge (which header is which, what a sensor really measures, voltage dividers).
    /// LibreHardwareMonitor ships such tables for many boards but not all; anything here overrides its generic
    /// "Temperature #n / Fan #n" names. A board without a profile still works with neutral names.
    /// </summary>
    public sealed class BoardProfile
    {
        public string Match { get; init; } = "";
        /// <summary>Short name shown in the UI.</summary>
        public string Display { get; init; } = "";
        /// <summary>SuperIO control/fan index → (Hebrew, English, kind).</summary>
        public Dictionary<int, (string he, string en, ChannelKind kind)> Headers { get; init; } = new();
        /// <summary>Plain-language explanation per header index → (Hebrew, English).</summary>
        public Dictionary<int, (string he, string en)> HeaderHelp { get; init; } = new();
        /// <summary>PWM outputs the chip has that the board most likely does not wire to a header — created, hidden by default.</summary>
        public HashSet<int> AbsentHeaders { get; init; } = new();
        /// <summary>SuperIO temperature index → (Hebrew, English, true if it measures the CPU rather than the board).</summary>
        public Dictionary<int, (string he, string en, bool cpuMirror)> Temps { get; init; } = new();
        /// <summary>SuperIO voltage index → (name, Ri, Rf, Vf): real = raw × (1 + Ri/Rf) + Vf, as in LibreHardwareMonitor's tables.</summary>
        public Dictionary<int, (string name, float ri, float rf, float vf)> Volts { get; init; } = new();

        public float ScaleVoltage(int index, float raw) =>
            Volts.TryGetValue(index, out var v) && v.rf != 0 ? raw * (1 + v.ri / v.rf) + v.vf : raw;

        public static readonly BoardProfile[] All =
        {
            new()
            {
                // Gigabyte X870I AORUS PRO ICE (ITE IT8696E). Not in LibreHardwareMonitor 0.9.6.
                // Sources: GCC's own fan table on this PC (CPU / System / PT / CPU OPT), LHM's tables for the
                // IT8696E siblings (X870 AORUS ELITE, X870E AORUS PRO), cross-checked against live readings:
                // temp #3 tracks Tctl, voltage #2/#3/#4 scale to 3.30 / 12.2 / 5.05 V, tach #1 follows the AIO pump,
                // header #3 spins the M.2 fan (≈4000 RPM BIOS auto, ≈9500 at 50%), headers #4/#6 have no tach.
                Match = "X870I AORUS PRO ICE",
                Display = "X870I AORUS PRO ICE",
                Headers = new()
                {
                    [0] = ("CPU_FAN · טכומטר המשאבה", "CPU_FAN · pump tach", ChannelKind.Motherboard),
                    [1] = ("SYS_FAN", "SYS_FAN", ChannelKind.Motherboard),
                    [2] = ("מאוורר NVMe · PT_FAN (M.2 קדמי)", "NVMe fan · PT_FAN (front M.2)", ChannelKind.NvmeFan),
                    [4] = ("CPU_OPT", "CPU_OPT", ChannelKind.Motherboard),
                },
                HeaderHelp = new()
                {
                    [0] = ("המחבר CPU_FAN ליד שקע המעבד. אצלך מחובר אליו כבל הטכומטר של משאבת ה-Galahad — לכן הוא מראה את סל״ד המשאבה. המשאבה עצמה נשלטת דרך ה-USB של ה-AIO (ערוץ המשאבה), לא דרך המחבר הזה.",
                           "The CPU_FAN header next to the CPU socket. On this PC the Galahad pump tach cable is plugged in here — that is why it shows the pump RPM. The pump itself is controlled over the AIO USB link (the pump channel), not through this header."),
                    [1] = ("מחבר מאוורר מערכת (SYS_FAN) — למאווררי מארז. אם מוצג 0 סל״ד: אין מאוורר מחובר, או שהמאווררים מחוברים דרך מפצל/רכזת שלא מעבירים אות סל״ד (שליטה עדיין עובדת). נסה ״זהה מאוורר״ ובדוק איזה מאוורר מאיץ.",
                           "System fan header (SYS_FAN) — for case fans. Showing 0 RPM? Either nothing is connected, or the fans hang off a splitter/hub that does not pass the RPM signal back (control still works). Try Identify fan and see which fan speeds up."),
                    [2] = ("מאוורר הקירור הקטן שעל גוף הקירור של ה-M.2 הקדמי (מעל ה-Crucial P3). נשלט כאן לפי טמפרטורת הכונן.",
                           "The small fan on the front M.2 heatsink (above the Crucial P3). Controlled here from the drive temperature."),
                    [4] = ("CPU_OPT = מחבר מאוורר מעבד נוסף (OPTional). נועד למאוורר שני על קירור אוויר או למשאבה של AIO, ובברירת המחדל של ה-BIOS הוא מקבל את אותה עקומה כמו CPU_FAN. אפשר לחבר אליו גם מאוורר מארז. 0 סל״ד = כנראה לא מחובר.",
                           "CPU_OPT = an extra (OPTional) CPU fan header. Meant for a second cooler fan or an AIO pump, and by default the BIOS drives it with the same curve as CPU_FAN. A case fan works on it too. 0 RPM = most likely nothing connected."),
                    [3] = ("יציאת PWM של השבב שלא מסומנת על הלוח לפי GCC. מוסתרת כברירת מחדל. אם מאוורר לא נמצא באף ערוץ — נסה כאן ״זהה מאוורר״.",
                           "A chip PWM output that GCC does not list as a header on this board. Hidden by default. If a fan is missing from every channel, try Identify fan here."),
                    [5] = ("יציאת PWM של השבב שלא מסומנת על הלוח לפי GCC. מוסתרת כברירת מחדל. אם מאוורר לא נמצא באף ערוץ — נסה כאן ״זהה מאוורר״.",
                           "A chip PWM output that GCC does not list as a header on this board. Hidden by default. If a fan is missing from every channel, try Identify fan here."),
                },
                AbsentHeaders = new() { 3, 5 },
                Temps = new()
                {
                    [0] = ("לוח אם · מערכת", "Board · System", false),
                    [1] = ("לוח אם · שבב (PCH)", "Board · Chipset (PCH)", false),
                    [2] = ("CPU (חיישן השקע בלוח האם)", "CPU (motherboard socket sensor)", true),
                    [3] = ("לוח אם · אזור PCIe x16", "Board · PCIe x16 area", false),
                    [4] = ("לוח אם · ספק מתח VRM", "Board · VRM MOS", false),
                    [5] = ("לוח אם · בקר EC", "Board · EC", false),
                },
                Volts = new()
                {
                    [0] = ("Vcore", 0, 1, 0),
                    [1] = ("+3.3V", 6.49f, 10, 0),
                    [2] = ("+12V", 5, 1, 0),
                    [3] = ("+5V", 1.5f, 1, 0),
                    [4] = ("CPU SoC", 0, 1, 0),
                    [5] = ("CPU Misc", 0, 1, 0),
                    [6] = ("CPU VDDIO / Memory", 0, 1, 0),
                }
            }
        };

        public static BoardProfile? Find(string? boardName) =>
            boardName == null ? null : All.FirstOrDefault(p => boardName.Contains(p.Match, StringComparison.OrdinalIgnoreCase));
    }
}
