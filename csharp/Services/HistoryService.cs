using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Services
{
    // Gộp lịch sử usage từ file Desktop (plan-usage-history.json) và log CLI
    // (cli-history.jsonl) thành một danh sách mẫu theo thời gian.
    public class HistoryService
    {
        private readonly string _desktopPath;
        private readonly string _cliPath;

        private (DateTime stamp, long length, List<UsageSample>? data) _desktopCache;
        private (DateTime stamp, long length, List<UsageSample>? data) _cliCache;

        public HistoryService(string? desktopPath = null, string? cliPath = null)
        {
            _desktopPath = desktopPath ?? TrackerPaths.DesktopHistory;
            _cliPath = cliPath ?? TrackerPaths.CliHistory;
        }

        public List<UsageSample> Load(DateTime since)
        {
            var all = new List<UsageSample>();
            all.AddRange(ReadCached(_desktopPath, ref _desktopCache, ParseDesktop));
            all.AddRange(ReadCached(_cliPath, ref _cliCache, ParseCli));

            return all.Where(s => s.Time >= since).OrderBy(s => s.Time).ToList();
        }

        // Giữ log CLI không phình mãi: bỏ các dòng quá hạn khi file đã lớn.
        public void PruneCliHistory(int retentionDays)
        {
            try
            {
                var info = new FileInfo(_cliPath);
                if (!info.Exists || info.Length < 1_000_000) return;

                long cutoff = new DateTimeOffset(DateTime.Now.AddDays(-retentionDays)).ToUnixTimeMilliseconds();
                var kept = new List<string>();
                foreach (var line in File.ReadLines(_cliPath))
                {
                    if (TryGetTimestamp(line, out long t) && t >= cutoff) kept.Add(line);
                }
                AtomicFile.WriteAllLines(_cliPath, kept);
            }
            catch
            {
                // dọn dẹp thất bại không ảnh hưởng chức năng chính
            }
        }

        private static bool TryGetTimestamp(string line, out long t)
        {
            t = 0;
            try
            {
                using var doc = JsonDocument.Parse(line);
                return doc.RootElement.TryGetProperty("t", out var el) && el.TryGetInt64(out t);
            }
            catch { return false; }
        }

        private static List<UsageSample> ReadCached(string path,
            ref (DateTime stamp, long length, List<UsageSample>? data) cache,
            Func<string, List<UsageSample>> parse)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return new List<UsageSample>();

                if (cache.data != null && cache.stamp == info.LastWriteTimeUtc && cache.length == info.Length)
                    return cache.data;

                var data = parse(path);
                cache = (info.LastWriteTimeUtc, info.Length, data);
                return data;
            }
            catch
            {
                return new List<UsageSample>();
            }
        }

        private static List<UsageSample> ParseDesktop(string path)
        {
            var result = new List<UsageSample>();
            // Desktop có thể đang khoá file khi ghi: copy ra temp rồi đọc.
            string temp = Path.Combine(Path.GetTempPath(), $"claude_hist_{Guid.NewGuid():N}.json");
            try
            {
                File.Copy(path, temp, true);
                using var doc = JsonDocument.Parse(File.ReadAllText(temp));
                if (!doc.RootElement.TryGetProperty("samples", out var samples)) return result;

                foreach (var s in samples.EnumerateArray())
                {
                    if (!s.TryGetProperty("t", out var tEl) || !s.TryGetProperty("u", out var u)) continue;
                    double fh = u.TryGetProperty("fh", out var fhEl) ? fhEl.GetDouble() : 0;
                    double sd = u.TryGetProperty("sd", out var sdEl) ? sdEl.GetDouble() : 0;
                    result.Add(new UsageSample(FromMs(tEl.GetInt64()), fh, sd));
                }
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
            return result;
        }

        private static List<UsageSample> ParseCli(string path)
        {
            var result = new List<UsageSample>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    if (!r.TryGetProperty("t", out var tEl) || !r.TryGetProperty("fh", out var fhEl)) continue;
                    if (fhEl.ValueKind != JsonValueKind.Number) continue;

                    double sd = r.TryGetProperty("sd", out var sdEl) && sdEl.ValueKind == JsonValueKind.Number ? sdEl.GetDouble() : 0;
                    string? model = r.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() : null;
                    double? ctx = r.TryGetProperty("ctx", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetDouble() : null;
                    string? sid = r.TryGetProperty("sid", out var sEl) && sEl.ValueKind == JsonValueKind.String ? sEl.GetString() : null;
                    string? project = r.TryGetProperty("project", out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() : null;

                    result.Add(new UsageSample(FromMs(tEl.GetInt64()), fhEl.GetDouble(), sd, model, ctx, sid, project));
                }
                catch
                {
                    // bỏ qua dòng hỏng (ví dụ đang ghi dở)
                }
            }
            return result;
        }

        private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    }
}
