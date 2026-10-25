namespace Sefarim.Core
{
    /// <summary>
    /// קובע את ספק SQLite פעם אחת. הספק טוען את e_sqlite3.dll בדרך הרגילה של Windows,
    /// ולכן מוצא אותה גם כשהיא נטענה מראש מתוך קובץ ה-EXE.
    /// </summary>
    public static class SqliteInit
    {
        private static readonly object Gate = new object();
        private static bool _done;

        public static void Ensure()
        {
            if (_done) return;
            lock (Gate)
            {
                if (_done) return;
                SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                _done = true;
            }
        }
    }
}