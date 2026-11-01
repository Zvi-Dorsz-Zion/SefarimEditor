using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sefarim.Core
{
    /// <summary>תוצאת בדיקה של הגרסה האחרונה. Error != null = הבדיקה נכשלה (והסיבה).</summary>
    public sealed class ReleaseCheckResult
    {
        public long? Latest { get; set; }

        /// <summary>מזהה התשובה של גיטהאב, לבקשה מותנית בפעם הבאה.</summary>
        public string ETag { get; set; }

        /// <summary>גיטהאב השיב "לא השתנה מאז" (304) – הגרסה השמורה עדיין נכונה. בקשה כזו לא נספרת במכסה.</summary>
        public bool NotModified { get; set; }

        public string Error { get; set; }

        /// <summary>כשחרגנו ממכסת הבדיקות של גיטהאב: מתי היא מתחדשת.</summary>
        public DateTime? RetryAfter { get; set; }
    }

    /// <summary>
    /// בודק ב-GitHub מה גרסת הספרייה האחרונה שפורסמה לאוצריא, באותו מקום שאוצריא
    /// עצמה בודקת (Otzaria/SeforimLibrary). זית מורידה חבילה מלאה בכל עדכון, ולכן
    /// אצלה אין צורך בבדיקה מראש.
    /// </summary>
    public static class ReleaseChecker
    {
        private const string OtzariaReleases = "https://api.github.com/repos/Otzaria/SeforimLibrary/releases?per_page=40";

        private static readonly Regex VersionTag = new Regex(@"^v(\d+)-\d+$", RegexOptions.Compiled);

        /// <summary>מחזיר את גרסת הספרייה היציבה האחרונה, או null אם הבדיקה נכשלה.</summary>
        public static long? LatestOtzaria(bool includePrerelease = false) => CheckOtzaria(null, includePrerelease).Latest;

        /// <summary>
        /// בדיקה עם הסיבה לכישלון. etag (מהבדיקה הקודמת) הופך את הבקשה למותנית: אם שום דבר לא השתנה,
        /// גיטהאב משיב 304 בלי לספור את הבקשה במכסה של 60 בשעה (שמשותפת גם לאוצריא עצמה).
        /// </summary>
        public static ReleaseCheckResult CheckOtzaria(string etag, bool includePrerelease = false)
        {
            if (long.TryParse(Environment.GetEnvironmentVariable("SEFARIM_TEST_LATEST"), out var fake))
                return new ReleaseCheckResult { Latest = fake };
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var req = (HttpWebRequest)WebRequest.Create(OtzariaReleases);
                req.UserAgent = "SefarimEditor";
                req.Accept = "application/vnd.github+json";
                req.Timeout = 60000;
                req.ReadWriteTimeout = 60000;
                if (!string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var result = new ReleaseCheckResult { ETag = resp.Headers[HttpResponseHeader.ETag] };
                    using (var doc = JsonDocument.Parse(reader.ReadToEnd()))
                    {
                        foreach (var rel in doc.RootElement.EnumerateArray())
                        {
                            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                            if (!includePrerelease && rel.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
                            var m = VersionTag.Match(rel.GetProperty("tag_name").GetString() ?? "");
                            if (!m.Success) continue;
                            var v = long.Parse(m.Groups[1].Value);
                            if (result.Latest == null || v > result.Latest) result.Latest = v;
                        }
                    }
                    if (result.Latest == null) result.Error = "לא נמצאה גרסת ספרייה ברשימת הגרסאות של גיטהאב";
                    return result;
                }
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                using (r)
                {
                    if (r.StatusCode == HttpStatusCode.NotModified) return new ReleaseCheckResult { NotModified = true, ETag = etag };
                    var result = new ReleaseCheckResult { Error = $"גיטהאב השיב {(int)r.StatusCode} ({r.StatusCode})" };
                    if (r.Headers["X-RateLimit-Remaining"] == "0" && long.TryParse(r.Headers["X-RateLimit-Reset"], out var reset))
                    {
                        result.RetryAfter = DateTimeOffset.FromUnixTimeSeconds(reset).LocalDateTime;
                        result.Error = $"חרגנו ממכסת הבדיקות של גיטהאב (60 בשעה, משותפת גם לאוצריא); מתחדשת ב-{result.RetryAfter:HH:mm}";
                    }
                    return result;
                }
            }
            catch (WebException ex) { return new ReleaseCheckResult { Error = "אין חיבור לגיטהאב: " + ex.Message }; }
            catch (JsonException ex) { return new ReleaseCheckResult { Error = "תשובה לא תקינה מגיטהאב: " + ex.Message }; }
            catch (InvalidOperationException ex) { return new ReleaseCheckResult { Error = "תשובה לא צפויה מגיטהאב: " + ex.Message }; }
            catch (IOException ex) { return new ReleaseCheckResult { Error = "הקריאה מגיטהאב נקטעה: " + ex.Message }; }
        }

        /// <summary>
        /// שומר בהגדרות את תוצאת הבדיקה (אם הצליחה). מחזיר את הגרסה האחרונה הידועה, או null אם נכשלה.
        /// משותף לתהליך הרקע ולחלון, כדי ששניהם יכתבו את אותם שדות באותה צורה.
        /// </summary>
        public static long? Store(AppSettings st, ReleaseCheckResult r)
        {
            if (r.Error != null) return null;
            var latest = r.NotModified ? st.LatestOtzariaRelease : r.Latest;
            if (latest == null) return null;
            st.LatestOtzariaRelease = latest;
            st.LastReleaseCheck = DateTime.Now;
            if (!r.NotModified) st.OtzariaReleasesETag = r.ETag;
            return latest;
        }

        /// <summary>ה-ETag לשלוח: רק כשיש גרסה שמורה (אחרת תשובת 304 לא תגיד לנו כלום).</summary>
        public static string ETagFor(AppSettings st) => st.LatestOtzariaRelease != null ? st.OtzariaReleasesETag : null;
    }
}
