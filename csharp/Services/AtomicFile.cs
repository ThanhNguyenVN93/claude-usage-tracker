using System;
using System.IO;

namespace ClaudeUsageTracker.Services
{
    // Ghi qua file tạm rồi Move đè: crash giữa chừng không để lại file cụt.
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string content)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, content);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        public static void WriteAllLines(string path, System.Collections.Generic.IEnumerable<string> lines)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllLines(temp, lines);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
    }
}
