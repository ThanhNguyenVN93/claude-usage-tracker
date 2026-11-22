using System;
using System.IO;
using System.Text.Json;

namespace ClaudeUsageTracker.Services
{
    // Nhớ lần kiểm tra gần nhất và bản đã báo, để không gọi GitHub quá dày (60 yêu cầu/giờ khi
    // không có token) và không báo lặp cùng một phiên bản.
    public class UpdateState
    {
        public DateTime LastCheckUtc { get; set; } = DateTime.MinValue;
        public string? NotifiedTag { get; set; }

        public static UpdateState Load()
        {
            try
            {
                if (File.Exists(TrackerPaths.UpdateState))
                    return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(TrackerPaths.UpdateState)) ?? new UpdateState();
            }
            catch { /* file hỏng: bắt đầu lại */ }
            return new UpdateState();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(TrackerPaths.DataDir);
                AtomicFile.WriteAllText(TrackerPaths.UpdateState, JsonSerializer.Serialize(this));
            }
            catch { /* không lưu được thì lần sau kiểm tra lại, không sao */ }
        }
    }
}
