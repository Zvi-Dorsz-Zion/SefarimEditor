using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SefarimEditor
{
    /// <summary>
    /// ספריית SQLite המקורית (e_sqlite3.dll) ארוזה בתוך ה-EXE. בהפעלה היא נשלפת לתיקיית
    /// ההגדרות ונטענת משם, כך שהתוכנה היא קובץ אחד בלבד.
    /// </summary>
    internal static class NativeLoader
    {
        private const string ResourceName = "SefarimEditor.e_sqlite3.dll";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        public static void Ensure()
        {
            // אם הקובץ נמצא ליד ה-EXE (למשל בזמן פיתוח), אין מה לעשות.
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "e_sqlite3.dll"))) return;

            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName))
            {
                if (res == null) return;
                var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SefarimEditor", "native", version + "-" + res.Length);
                var path = Path.Combine(dir, "e_sqlite3.dll");
                if (!File.Exists(path) || new FileInfo(path).Length != res.Length)
                {
                    Directory.CreateDirectory(dir);
                    var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var fs = File.Create(tmp)) res.CopyTo(fs);
                    try
                    {
                        if (!File.Exists(path)) File.Move(tmp, path);
                    }
                    finally
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                    }
                }
                SetDllDirectory(dir);
                LoadLibrary(path);
            }
        }
    }
}
