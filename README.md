# ⚡ PulseWatch 2.0 · מרכז שליטה מאוחד

אפליקציה אחת במקום ארבע: **GCC** (עקומות מאווררים של לוח האם), **MSI Afterburner** (כוונון כרטיס מסך),
**NVIDIA Control Panel** (הגדרות דרייבר) ו-**L-Connect 3** (משאבה, מאווררי רדיאטור ותאורה של ה-Galahad II) —
עם טמפרטורות, גרפים והיסטוריה של שעה. כל הנתונים אמיתיים, נקראים ישירות מהחומרה.

## התקנה

מורידים מ-[Releases](https://github.com/AirZone1/PulseWatch/releases/latest):

| קובץ | מה זה |
|---|---|
| `PulseWatch-…-win-x64.msi` | מתקין: Program Files + קיצור בתפריט התחל. הסרה רגילה דרך "אפליקציות" ב-Windows. |
| `PulseWatch-…-win-x64-portable.zip` | בלי התקנה: פורסים לכל תיקייה (גם דיסק-און-קי) ומפעילים `PulseWatch.exe`. ההגדרות נשמרות בתיקיית `Data` לידו. |

שניהם כוללים את .NET — אין צורך להתקין אותו.

**לפני ההפעלה הראשונה**
- דרייבר **PawnIO** לחיישני לוח האם ולשליטה במאווררי לוח האם: `winget install namazso.PawnIO`
- דרייבר NVIDIA עדכני (לכרטיסי NVIDIA).
- PulseWatch מבקש הרשאות מנהל — נדרש לשליטה במאווררים, בכרטיס המסך וב-USB.
- הקבצים עוד לא חתומים דיגיטלית, לכן SmartScreen עלול להזהיר: *More info ← Run anyway*.

**מערכות נתמכות:** Windows 10/11 ‏x64. ‏Linux ו-macOS עוד לא נתמכים.

## הפעלה

- **מערכת והגדרות ← "הפעל עם Windows"** יוצר משימה מתוזמנת שמפעילה את PulseWatch בכניסה, ממוזער למגש ובלי חלון UAC.
- סגירת החלון משאירה את PulseWatch פועל במגש (אחרת העקומות מפסיקות לעבוד). יציאה מלאה: מהמגש או מעמוד "מערכת".

### בנייה מהקוד
- .NET 8 SDK, ואז `update_native.bat` (בונה ל-`bin\native_win64` ומפעיל).
- חבילות הפצה (MSI + Portable) נבנות אוטומטית ב-GitHub Actions בכל תג גרסה (`v2.0.1` …) — ראו `.github/workflows/build.yml` ו-`installer/`.

## English

PulseWatch is one Windows app for fan curves (motherboard SuperIO, NVMe fan), NVIDIA GPU tuning
(power/temperature limits, offsets, a real V/F curve editor with undervolt), NVIDIA driver settings, Lian Li Galahad II
pump/fans/lighting, per-game profiles, temperature alerts, hotkeys and an OSD with FPS.
Download the **MSI** or the **portable ZIP** from Releases (Windows 10/11 x64; needs the PawnIO driver:
`winget install namazso.PawnIO`; runs as administrator). The UI is Hebrew or English (System → Language).

## מה יש בכל עמוד

| עמוד | מקביל ל- | מה עושים שם |
|---|---|---|
| **סקירה** | — | טמפ' CPU/GPU, עומסים, משאבה ורדיאטור, כל המאווררים, גרפים של דקה עד שעה |
| **קירור ומאווררים** | GCC Smart Fan / L-Connect | לכל ערוץ: אוטומטי / מהירות קבועה / עקומה. עורך עקומה בגרירה, כמה חיישני מקור (העקומה מגיבה לחם מביניהם), היסטרזיס והאצה/האטה, "זהה מאוורר", שינוי שם והסתרה |
| **כרטיס מסך · OC** | MSI Afterburner | Power Limit, Temp Limit, Core/Memory offset ביחידות של Afterburner, **עורך עקומת מתח/תדר (V/F) עם 127 נקודות** בשתי תצוגות (מתח לכל תדר / Afterburner), Undervolt ב-mV על כל העקומה או טווח מסומן, תקרת מתח שנשמרת שטוחה גם כשהעקומה זזה עם הטמפרטורה, יישור עקומה, 5 פרופילי OC, החלה באתחול, ניטור חי + "מה מגביל עכשיו" |
| **הגדרות NVIDIA** | NVIDIA Control Panel | הגדרות 3D גלובליות (ניהול צריכה, Low Latency, Max FPS, V-Sync, סינון טקסטורות...), Digital Vibrance לכל מסך, המלצות בלחיצה |
| **תאורה · Lian Li** | L-Connect 3 | אפקטים לראש המשאבה ולמאווררים, צבע לפי טמפרטורה, סנכרון ARGB ללוח האם |
| **חיישנים וגרפים** | HWMonitor | כל החיישנים עם מינ'/מקס', חיפוש וסינון, שינוי שמות, ★ לגרף, ייצוא CSV |
| **אוטומציה · OSD** | RivaTuner / Afterburner profiles | פרופיל קירור ו-OC לכל משחק (חוזר אוטומטית כשהמשחק נסגר), התראות Windows על טמפרטורה גבוהה, קיצורי מקלדת (Ctrl+Alt+1–4 פרופילים, Ctrl+Alt+F1–F5 פרופילי OC, Ctrl+Alt+O OSD), OSD תמיד-עליון (כמה שורות או שורה אחת, 3 גדלים, כל מסך ופינה) עם FPS של המשחק שבחזית דרך ETW כמו PresentMon (DirectX 9–12; לא מעל Exclusive Fullscreen) |
| **מערכת והגדרות** | — | מפרט, בריאות כוננים, ניהול תוכנות היצרן, הפעלה עם Windows, הגנה תרמית, תוכנית חשמל, קצב קריאת HDD ו-NVMe |

**חומרה נוספת:** בקרי מאווררים ש-LibreHardwareMonitor מכיר (NZXT Kraken/Grid, Aquacomputer, AeroCool, Razer) וכרטיסי מסך AMD/Intel מופיעים אוטומטית כערוצים וכחיישנים. לוחות אם ללא פרופיל עובדים עם שמות כלליים.

**פרופילים מאוחדים** (בפס העליון ובמגש): שקט / מאוזן / ביצועים / מקסימום — כל פרופיל שומר הגדרה נפרדת לכל ערוץ מאוורר.

**שפה:** מערכת והגדרות ← עברית / English (ממשק מלא בשתי השפות, כולל כיווניות).

**גרפים:** לחיצה על שם בתחתית גרף מסתירה קו, לחיצה כפולה מבודדת אותו; הציר האנכי מתאים את עצמו לקווים הגלויים.

## ביצועים

נמדד על המחשב הזה עם הפרופיילר המובנה (שורת `PERF` בלוג כל דקה):

| מצב | CPU | זיכרון |
|---|---|---|
| במגש | ~0.05% מהמחשב | ~45–60 MB |
| חלון פתוח | ~0.15–0.25% | ~180 MB |

איך: כרטיס המסך נקרא ישירות (NVML + NvAPI, ~1.5ms במקום 86ms דרך LibreHardwareMonitor); חיישנים שמשפיעים על מאווררים נדגמים כל שנייה, וכל השאר בקצב איטי יותר (דיסק HDD ישן — SMART לוקח 270ms — פעם ב-2–10 דקות); כשהחלון מוסתר הדפים משוחררים מהזיכרון.

לעדכון גרסה: צא מ-PulseWatch מהמגש והרץ `update_native.bat`.

## בטיחות

- **הגנה תרמית:** CPU מעל 90°C או GPU מעל 87°C (ניתן לשינוי) ← כל המאווררים והמשאבה ל-100% עד ירידה של 6 מעלות.
- **חיישן חסר** ← הערוץ שעוקב אחריו עובר ל-100%.
- **ביציאה:** מאווררי לוח האם וה-GPU חוזרים ל-BIOS/דרייבר; משאבת ה-Galahad ל-100% והמאווררים שלו לערך שנבחר (ברירת מחדל 60%), כי ל-AIO אין מצב אוטומטי משלו.
- **תוכנות יצרן:** כש-Afterburner / L-Connect / GCC פתוחים, PulseWatch עובר לקריאה בלבד עבור החומרה שלהם (שתי תוכנות על אותו מאוורר = קפיצות). כפתור "סגור והשתלט" סוגר אותן; "החזר שליטה" מפעיל אותן מחדש.
- **גיבוי NVIDIA:** לפני השינוי הראשון נשמר גיבוי של הגדרות הדרייבר ל-`%AppData%\PulseWatch\nvidia-settings-backup.nvidia`.

## מבנה הקוד (`native/`)

```
Core/   SensorHub.cs       כל הגישה לחומרה בתהליכון אחד: דגימה, היסטוריה (3600 דגימות), מנוע עקומות, תאורה, הגנה תרמית
        FanChannels.cs     ערוצי מאוורר: SuperIO (לוח אם), NVML (GPU), Galahad II (משאבה + רדיאטור)
        Nvml.cs            NVIDIA NVML: מאווררים, Power/Temp limit, Clock offsets, סיבות הגבלה
        NvidiaSettings.cs  NvAPI DRS (הגדרות דרייבר) + Digital Vibrance
        LianLiTrinity.cs   פרוטוקול USB HID של Galahad II Trinity (0416:7373)
        Config.cs          הגדרות, עקומות ופרופילים → %AppData%\PulseWatch\config.json
        SystemTools.cs     תוכנות מתנגשות, משימת הפעלה, תוכניות חשמל
Controls/ TimeChart, FanCurveEditor, RingGauge   (ציור ישיר, ללא ספריות גרפים)
Views/  עמוד לכל לשונית
```

בנייה: `cd native && dotnet publish -c Release -o ..\bin\native_win64`

## קרדיטים
LibreHardwareMonitor + PawnIO (חיישנים ו-SuperIO) · NVIDIA NVML / NvAPI דרך NvAPIWrapper · HidSharp ·
פרוטוקול ה-Galahad II לפי פרויקט הקוד הפתוח lian-li-linux (MIT).
