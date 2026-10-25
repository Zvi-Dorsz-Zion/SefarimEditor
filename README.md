# עורך הספרייה

עורך בעברית לקובצי `seforim.db` של [אוצריא](https://github.com/Otzaria/otzaria) ושל [זית](https://github.com/kdroidFilter/Zayit):
מחיקה ועריכה של ספרים וקטגוריות, עם גיבוי מלא שמאפשר להחזיר את הקובץ למצב מקורי לפני עדכון ספרייה,
ולהחיל את השינויים שוב אחריו.

התוכנה עצמאית ואינה קשורה רשמית לצוותים של אוצריא או של זית.

### קישורים
- אוצריא: https://github.com/Otzaria/otzaria
- זית: https://github.com/kdroidFilter/Zayit
- ספריית הספרים של אוצריא (מקור העדכונים): https://github.com/Otzaria/SeforimLibrary
- ספריית הספרים של זית: https://github.com/kdroidFilter/SeforimLibrary

## הורדה

את התוכנה המוכנה מורידים מדף ה-[Releases](https://github.com/Zvi-Dorsz-Zion/SefarimEditor/releases/latest):
קובץ `SefarimEditor.exe` אחד, בלי התקנה. דרישות: Windows 10 או 11.
הוראות שימוש: [מדריך למשתמש](dist-files/מדריך%20למשתמש.txt).

## מבנה

| תיקייה | מה יש בה |
|---|---|
| `src/Sefarim.Core` | הליבה: קריאת הקובץ, מנוע השינויים, מאגר הגיבוי, קטלוג זית, הגדרות |
| `src/SefarimEditor` | התוכנה: חלון העורך (WPF) ותהליך הרקע (`--tray`, WinForms בלבד) |
| `tests/Sefarim.Tests` | בדיקה על עותק של ספרייה אמיתית |
| `dist-files` | קבצים שנכנסים לחבילה (מדריך למשתמש) |
| `dist` | החבילה המוכנה (נוצרת ע"י `build.ps1`) |

טכנולוגיה: C# על ‎.NET Framework 4.8 (מותקן בכל Windows 10/11), Microsoft.Data.Sqlite, ZstdSharp.

## איך זה עובד

- **מנוע השינויים** (`ChangeEngine`): מחיקה מחושבת לפי הקשרים הזרים שבסכמה (CASCADE / SET NULL),
  ולכן מתאימה גם לאוצריא וגם לזית בלי קוד נפרד. לפני כל שינוי השורות המקוריות מועתקות לקובץ
  `originals.db` בתיקיית הגיבוי, עם טביעת אצבע. בהחזרה התוכן נבדק מול טביעת האצבע, ובאי-התאמה הכול מתבטל.
- **למה זה חשוב**: אוצריא מאמתת חתימת תוכן לוגית של כל הקובץ לפני עדכון חלקי (`LogicalContentHasher`
  ב-otzaria_library_updater). אחרי החזרה מלאה התוכן זהה בדיוק, ולכן העדכון החלקי עובד.
- **גרסת ספרייה**: באוצריא `schema_meta.db_version`; בזית `release_info.txt` (זית מחליפה את כל הקובץ בכל עדכון).
- **קטלוג זית** (`ZayitCatalog`): זית מציגה את העץ מ-`catalog.pb`. נשמר עותק מקורי בגיבוי, ואחרי כל שינוי
  הקובץ נבנה מחדש מהמקור לפי המסד.
- **תהליך הרקע** (`TrayApp`): בודק ב-GitHub (`Otzaria/SeforimLibrary`) אם יצאה גרסה חדשה, רק כשיש ספריית
  אוצריא ערוכה. מציע להחזיר למקור כשהתוכנה סגורה, ומחיל שוב אחרי שהגרסה השתנתה.
  יומן: `%LOCALAPPDATA%\SefarimEditor\tray.log`.

## בנייה ואריזה

```powershell
.\build.ps1
```

## בדיקות

רק על עותק של הקובץ, לעולם לא על המקור:

```powershell
dotnet build tests\Sefarim.Tests -c Release
tests\Sefarim.Tests\bin\Release\net48\win-x64\Sefarim.Tests.exe <עותק\seforim.db> <תיקיית גיבוי> [--quick] [--zayit]
```

בלי `--quick` הבדיקה מחשבת טביעת אצבע של כל הקובץ לפני ואחרי, ומוודאת שהוא זהה בדיוק למקור.

מתגי בדיקה (משתני סביבה, לבדיקות בלבד): `SEFARIM_SETTINGS_DIR` (תיקיית הגדרות חלופית),
`SEFARIM_TEST_PROCESS` (שם תהליך חלופי לאוצריא/זית), `SEFARIM_TEST_LATEST` (גרסה אחרונה מדומה),
`SEFARIM_TEST_ANSWER` (תשובה אוטומטית לשאלות של תהליך הרקע), `SEFARIM_TEST_FAST=1` (טיימרים מהירים).
הפעלה עם `--snapshot <קובץ.png>` מציירת את חלון העורך לקובץ בלי להציג אותו.

## מגבלות ידועות

- ספרים שנמחקו עשויים להופיע עדיין בחיפוש של אוצריא ושל זית (אינדקס חיפוש נפרד).
- אוצריא יכולה להסתיר ספרים בעצמה (הגדרות › ספרייה), והסתרה כזו גם מסירה אותם מהחיפוש.
