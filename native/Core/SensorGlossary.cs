using System.Text.RegularExpressions;

namespace PulseWatch.Core
{
    /// <summary>
    /// Plain-language names and explanations for the raw sensor names LibreHardwareMonitor reports,
    /// plus which ones matter enough to show by default.
    /// </summary>
    public static class SensorGlossary
    {
        public sealed record Entry(string Name, string Help, bool Key);

        public static Entry Describe(SensorInfo s)
        {
            string n = s.Name;
            string kind = s.HardwareKind;

            if (kind == "Cpu")
            {
                if (n.Contains("Tctl")) return E("CPU · טמפרטורה (Tctl/Tdie)", "CPU temperature (Tctl/Tdie)",
                    "הטמפרטורה הראשית של המעבד. ב-Zen 5 אין לה היסט — זה המספר האמיתי, ואחריו עוקבים המאווררים.",
                    "The CPU's main temperature. Zen 5 applies no offset — this is the real value fans should follow.", true);
                var ccd = Regex.Match(n, @"^CCD(\d+)");
                if (ccd.Success) return E($"CPU · שבב ליבות {ccd.Groups[1].Value} (CCD)", $"CPU · core die {ccd.Groups[1].Value} (CCD)",
                    "טמפרטורת השבב שבו יושבות הליבות. ב-9800X3D יש שבב אחד, ולכן הוא כמעט זהה ל-Tctl.",
                    "Temperature of the die that holds the cores. The 9800X3D has a single CCD, so it tracks Tctl closely.", false);
                if (n == "CPU Total") return E("CPU · עומס כולל", "CPU · total load", "ממוצע העומס על כל 16 התהליכונים.", "Average load across all logical processors.", true);
                if (n == "CPU Core Max") return E("CPU · עומס הליבה העמוסה ביותר", "CPU · busiest core load",
                    "העומס של התהליכון העמוס ביותר — מראה צוואר בקבוק של תהליכון יחיד במשחקים.", "Load of the busiest thread — reveals single-thread bottlenecks in games.", false);
                if (n == "Package" && s.Type == "Power") return E("CPU · הספק כולל (Package)", "CPU · package power",
                    "כל ההספק שהמעבד צורך כרגע, כולל ליבות, זיכרון מטמון ובקר הזיכרון.", "Total power the CPU draws right now (cores, cache, memory controller).", true);
                if (n == "Cores (Average Effective)") return E("CPU · תדר אפקטיבי ממוצע", "CPU · average effective clock",
                    "התדר שהליבות באמת עובדות בו, כולל זמן שינה. זה המספר הנכון להצגה.", "The clock the cores really run at, including sleep time. The right number to display.", true);
                if (n == "Cores (Average)") return E("CPU · תדר מבוקש ממוצע", "CPU · average requested clock",
                    "התדר המקסימלי שהליבות מבקשות — גבוה גם כשהן ישנות, ולכן מטעה בסרק.", "The clock the cores ask for — high even while they sleep, so it misleads at idle.", false);
                var core = Regex.Match(n, @"^Core #(\d+)( \(Effective\)| \(SMU\)| VID)?$");
                if (core.Success)
                {
                    string i = core.Groups[1].Value, suffix = core.Groups[2].Value;
                    return s.Type switch
                    {
                        "Clock" when suffix == " (Effective)" => E($"ליבה {i} · תדר אפקטיבי", $"Core {i} · effective clock", "תדר בפועל של הליבה, כולל זמן שינה.", "Real clock of this core including sleep time.", false),
                        "Clock" => E($"ליבה {i} · תדר מבוקש", $"Core {i} · requested clock", "התדר שהליבה מבקשת ברגע זה.", "The clock this core currently requests.", false),
                        "Power" => E($"ליבה {i} · הספק", $"Core {i} · power", "הספק של ליבה אחת לפי ה-SMU.", "Power of one core as reported by the SMU.", false),
                        "Voltage" => E($"ליבה {i} · מתח מבוקש (VID)", $"Core {i} · requested voltage (VID)", "המתח שהליבה מבקשת — לא המתח שנמדד בפועל.", "Voltage the core requests — not the measured voltage.", false),
                        "Factor" => E($"ליבה {i} · מכפיל", $"Core {i} · multiplier", "מכפיל התדר (תדר ÷ 100MHz).", "Clock multiplier (clock ÷ 100 MHz).", false),
                        _ => E(n, n, "", "", false)
                    };
                }
                var thread = Regex.Match(n, @"^CPU Core #(\d+)$");
                if (thread.Success && s.Type == "Load") return E($"תהליכון {thread.Groups[1].Value} · עומס", $"Thread {thread.Groups[1].Value} · load",
                    "עומס על תהליכון לוגי אחד (לכל ליבה יש שניים).", "Load on one logical processor (two per core).", false);
            }

            if (kind == "GpuNvidia")
            {
                if (s.Id == "virt/gpu/hotspot") return E(s.Name, s.Name,
                    "הנקודה החמה ביותר על שבב כרטיס המסך (לא המעבד ולא לוח האם). בדרך כלל 10–15°C מעל ליבת ה-GPU; עד 100°C תקין.",
                    "The hottest point on the graphics chip (not the CPU or motherboard). Usually 10–15 °C above GPU core; up to ~100 °C is normal.", true);
                if (s.Id == "virt/gpu/memtemp") return E(s.Name, s.Name,
                    "טמפרטורת שבבי זיכרון ה-GDDR6X של כרטיס המסך. עד ~100°C תקין, מעל 105°C הכרטיס מאט.",
                    "Temperature of the card's GDDR6X memory chips. Up to ~100 °C is fine; above 105 °C the card throttles.", true);
                if (s.Id == "virt/gpu/temp") return E(s.Name, s.Name, "טמפרטורת ליבת כרטיס המסך — המספר הראשי של ה-GPU.", "GPU core temperature — the main GPU number.", true);
                bool key = s.Id is "virt/gpu/load" or "virt/gpu/clock" or "virt/gpu/power" or "virt/gpu/fanpct";
                return E(s.Name, s.Name, "", "", key);
            }

            if (kind == "SuperIO" && s.Type == "Temperature")
                return E(s.DisplayName, s.DisplayName, s.CpuMirror
                    ? L.T("חיישן שלוח האם מודד ליד שקע המעבד — עוקב אחרי ה-CPU ולכן לא נחשב לטמפרטורת הלוח.", "A board sensor near the CPU socket — it follows the CPU, so it is not counted as a board temperature.")
                    : L.T("חיישן טמפרטורה של לוח האם. לחץ על השם כדי לתת לו שם (למשל VRM / שבב / PCIe).", "Motherboard temperature sensor. Click the name to label it (e.g. VRM / chipset / PCIe)."), true);
            if (kind == "SuperIO" && s.Type == "Fan") return E(s.Name, s.Name, L.T("סיבובי מאוורר בחיבור של לוח האם.", "Fan speed on a motherboard header."), true);
            if (kind == "SuperIO" && s.Type == "Voltage")
            {
                if (n == "CMOS Battery") return E(L.T("סוללת CMOS", "CMOS battery"), "CMOS battery", L.T("מתחת ל-2.8V כדאי להחליף.", "Replace below about 2.8 V."), false);
                return E(n, n, L.T("מתח שלוח האם מודד (המשמעות של כל ערוץ תלויה בלוח).", "A voltage the board measures (each channel's meaning depends on the board)."), false);
            }

            if (kind == "Storage")
            {
                if (n.Contains("Composite") || n == "Temperature") return E(L.T("טמפרטורת כונן", "Drive temperature"), "Drive temperature",
                    L.T("הטמפרטורה הראשית של הכונן. NVMe: עד ~70°C תקין.", "The drive's main temperature. NVMe: up to ~70 °C is fine."), true);
                if (n == "Life") return E(L.T("בריאות (Life)", "Health (life)"), "Health", L.T("כמה מאורך החיים של הכונן נשאר.", "How much of the drive's rated life remains."), true);
                return E(n, n, "", "", false);
            }

            if (kind == "Cooler" || s.Id.StartsWith("virt/")) return E(s.DisplayName, s.DisplayName, "", "", true);
            if (kind == "Memory" && s.Type == "Load") return E(n, n, "", "", true);
            return E(n, n, "", "", false);
        }

        private static Entry E(string he, string en, string helpHe, string helpEn, bool key) => new(L.T(he, en), L.T(helpHe, helpEn), key);
        /// <summary>For strings that are already localized.</summary>
        private static Entry E(string name, string unusedEn, string help, bool key) => new(name, help, key);
    }
}
