using System;
using System.IO;

namespace ClaudeUsageTracker.Services
{
    // Ghi lỗi không lường trước ra file để tool nền không "chết im lặng".
    public static class ErrorLog
    {
        private const long MaxBytes = 256 * 1024;

        public static string PathOnDisk => Path.Combine(TrackerPaths.DataDir, "error.log");

        public static void Write(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(TrackerPaths.DataDir);

                var info = new FileInfo(PathOnDisk);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(PathOnDisk, PathOnDisk + ".old", overwrite: true);

                File.AppendAllText(PathOnDisk,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // ghi log thất bại thì đành bỏ qua, tuyệt đối không ném tiếp
            }
        }
    }
}
