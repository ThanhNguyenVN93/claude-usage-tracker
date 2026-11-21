using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Services
{
    public class ClaudeUsageService
    {
        public static readonly string UsageHistoryPath = TrackerPaths.DesktopHistory;

        // Ghi bởi statusline.js (Claude Code) mỗi khi CLI render thanh trạng thái —
        // cho số liệu chính xác hơn (có giờ reset thật) khi đang dùng CLI để code.
        public static readonly string CliUsagePath = TrackerPaths.CliUsage;

        private TimeSpan _staleThreshold = TimeSpan.FromMinutes(15);

        public Task<UsageInfo> GetUsageAsync(AppConfig config)
        {
            _staleThreshold = TimeSpan.FromMinutes(Math.Max(1, config.StaleThresholdMinutes));
            try
            {
                // Lấy % từ nguồn có dữ liệu mới nhất (phiên CLI đóng lại là file CLI
                // đứng yên vĩnh viễn, nên không thể ưu tiên cứng một nguồn nào).
                var cliInfo = TryRead(CliUsagePath, ReadFromCliFile);
                var desktopInfo = TryRead(UsageHistoryPath, ReadFromHistoryFile);

                UsageInfo? chosen = (cliInfo, desktopInfo) switch
                {
                    (null, null) => null,
                    (not null, null) => cliInfo,
                    (null, not null) => desktopInfo,
                    _ => cliInfo!.LastRefreshedAt >= desktopInfo!.LastRefreshedAt ? cliInfo : desktopInfo
                };

                if (chosen != null)
                {
                    var other = ReferenceEquals(chosen, cliInfo) ? desktopInfo : cliInfo;
                    if (other != null)
                    {
                        chosen.OtherSourcePercent = other.PercentUsed;
                        chosen.OtherSourceLabel = other.SourceLabel;
                        chosen.OtherSourceTime = other.LastRefreshedAt;
                    }

                    // Mốc reset là thời điểm tuyệt đối: file CLI dù cũ vẫn cho giờ reset
                    // đúng, miễn mốc đó chưa trôi qua.
                    ApplyCliResetTimes(chosen);
                    return Task.FromResult(chosen);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeUsage] Error: {ex.Message}");
            }

            return Task.FromResult(BuildFallbackInfo());
        }

        // Giờ reset lấy từ statusline CLI vẫn dùng được cho cả dữ liệu Desktop,
        // vì đó là mốc thời gian tuyệt đối (chỉ bỏ qua khi đã trôi qua).
        private void ApplyCliResetTimes(UsageInfo info)
        {
            try
            {
                if (!File.Exists(CliUsagePath)) return;

                using var doc = JsonDocument.Parse(File.ReadAllText(CliUsagePath));
                if (!doc.RootElement.TryGetProperty("fiveHourResetsAt", out var el) || el.ValueKind == JsonValueKind.Null)
                    return;

                var resetTime = DateTimeOffset.FromUnixTimeSeconds(el.GetInt64()).LocalDateTime;
                if (resetTime <= DateTime.Now) return;

                info.ResetTime = resetTime;
                info.ResetCountdown = FormatCountdown(resetTime);
            }
            catch
            {
                // không có giờ reset thì giữ nguyên thông tin sẵn có
            }
        }

        // "vừa xong" / "2 phút trước" / "1h 20m trước" — nhìn là biết số liệu cũ tới đâu.
        private static string FormatAge(DateTime timestamp)
        {
            var age = DateTime.Now - timestamp;
            if (age < TimeSpan.FromSeconds(45)) return "vừa xong";
            if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} phút trước";
            return $"{(int)age.TotalHours}h {age.Minutes}m trước";
        }

        private static string FormatCountdown(DateTime resetTime)
        {
            var remaining = resetTime - DateTime.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m (lúc {resetTime:HH:mm})"
                : $"{remaining.Minutes}m (lúc {resetTime:HH:mm})";
        }

        private UsageInfo? TryRead(string path, Func<UsageInfo?> reader)
        {
            if (!File.Exists(path)) return null;
            try { return reader(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ClaudeUsage] Read error ({path}): {ex.Message}");
                return null;
            }
        }

        private UsageInfo? ReadFromCliFile()
        {
            string json = File.ReadAllText(CliUsagePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("fiveHour", out var fhEl) || fhEl.ValueKind == JsonValueKind.Null)
                return null;

            double fiveHour = fhEl.GetDouble();
            double weekly = root.TryGetProperty("sevenDay", out var sdEl) && sdEl.ValueKind != JsonValueKind.Null
                ? sdEl.GetDouble() : 0;

            long writtenAtMs = root.TryGetProperty("t", out var tEl) ? tEl.GetInt64() : 0;
            var writtenAt = DateTimeOffset.FromUnixTimeMilliseconds(writtenAtMs).LocalDateTime;

            string resetCountdown = "không rõ";
            DateTime resetTime = DateTime.Now;
            if (root.TryGetProperty("fiveHourResetsAt", out var resetEl) && resetEl.ValueKind != JsonValueKind.Null)
            {
                resetTime = DateTimeOffset.FromUnixTimeSeconds(resetEl.GetInt64()).LocalDateTime;
                resetCountdown = FormatCountdown(resetTime);
            }

            bool isStale = (DateTime.Now - writtenAt) > _staleThreshold;
            string modelName = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? modelEl.GetString() ?? "Claude Code" : "Claude Code";

            string note = isStale
                ? $"⚠ Claude Code CLI chưa cập nhật gần đây · {FormatAge(writtenAt)} ({writtenAt:HH:mm:ss})"
                : $"Đọc từ statusline Claude Code ({modelName}) · {FormatAge(writtenAt)} ({writtenAt:HH:mm:ss})";

            double? extraPercent = root.TryGetProperty("extraUsagePercent", out var extraEl) && extraEl.ValueKind != JsonValueKind.Null
                ? extraEl.GetDouble() : (double?)null;
            string? extraDetail = null;
            if (extraPercent.HasValue)
            {
                double? usedUsd = root.TryGetProperty("extraUsageUsd", out var u) && u.ValueKind != JsonValueKind.Null ? u.GetDouble() : (double?)null;
                double? limitUsd = root.TryGetProperty("extraUsageLimitUsd", out var l) && l.ValueKind != JsonValueKind.Null ? l.GetDouble() : (double?)null;
                extraDetail = (usedUsd.HasValue && limitUsd.HasValue)
                    ? $"${usedUsd.Value:0.00} / ${limitUsd.Value:0.00}"
                    : null;
            }

            double? contextPercent = root.TryGetProperty("contextPercent", out var ctxEl) && ctxEl.ValueKind == JsonValueKind.Number
                ? ctxEl.GetDouble() : (double?)null;
            string? sessionId = root.TryGetProperty("sessionId", out var sidEl) && sidEl.ValueKind == JsonValueKind.String
                ? sidEl.GetString() : null;

            return new UsageInfo
            {
                SourceLabel = "CLI",
                AccountName = "Claude Code (CLI)",
                Tier = "Dữ liệu từ statusline",
                PercentUsed = fiveHour,
                WeeklyPercent = weekly,
                MessagesRemaining = $"Tuần (7 ngày): {weekly:0}% đã dùng",
                ResetCountdown = resetCountdown,
                ResetTime = resetTime,
                IsDemo = false,
                IsStale = isStale,
                DesktopRunning = true,
                LastRefreshedAt = writtenAt,
                ExtraUsagePercent = extraPercent,
                ExtraUsageDetail = extraDetail,
                ContextPercent = contextPercent,
                SessionId = sessionId,
                Note = note
            };
        }

        private UsageInfo? ReadFromHistoryFile()
        {
            // Copy ra temp để tránh lỗi file đang bị Claude Desktop khoá khi ghi.
            string tempPath = Path.Combine(Path.GetTempPath(), $"claude_usage_{Guid.NewGuid():N}.json");
            string json;
            try
            {
                File.Copy(UsageHistoryPath, tempPath, true);
                json = File.ReadAllText(tempPath);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("samples", out var samples) || samples.GetArrayLength() == 0)
                return null;

            int count = samples.GetArrayLength();
            var last = samples[count - 1];
            var usage = last.GetProperty("u");

            double fiveHour = usage.TryGetProperty("fh", out var fh) ? fh.GetDouble() : 0;
            double weekly = usage.TryGetProperty("sd", out var sd) ? sd.GetDouble() : 0;
            double? extra = usage.TryGetProperty("xu", out var xu) ? xu.GetDouble() : (double?)null;

            long lastT = last.GetProperty("t").GetInt64();
            var lastUpdated = DateTimeOffset.FromUnixTimeMilliseconds(lastT).LocalDateTime;

            bool desktopRunning = Process.GetProcessesByName("Claude").Length > 0;
            bool isStale = (DateTime.Now - lastUpdated) > _staleThreshold;

            string note;
            if (!desktopRunning)
            {
                note = $"⚠ Claude Desktop đang đóng · số liệu {FormatAge(lastUpdated)} ({lastUpdated:HH:mm:ss})";
            }
            else if (isStale)
            {
                note = $"⚠ Desktop chưa đồng bộ gần đây · {FormatAge(lastUpdated)} ({lastUpdated:HH:mm:ss})";
            }
            else
            {
                note = $"Đọc từ plan-usage-history.json · {FormatAge(lastUpdated)} ({lastUpdated:HH:mm:ss})";
            }

            return new UsageInfo
            {
                SourceLabel = "Desktop",
                AccountName = "Claude Desktop",
                Tier = "Dữ liệu cục bộ",
                PercentUsed = fiveHour,
                WeeklyPercent = weekly,
                MessagesRemaining = $"Tuần (7 ngày): {weekly:0}% đã dùng",
                ResetCountdown = "xem trong Claude Desktop/claude.ai",
                ResetTime = DateTime.Now,
                IsDemo = false,
                IsStale = isStale,
                DesktopRunning = desktopRunning,
                LastRefreshedAt = lastUpdated,
                ExtraUsagePercent = extra,
                Note = note
            };
        }

        private UsageInfo BuildFallbackInfo()
        {
            bool desktopRunning = Process.GetProcessesByName("Claude").Length > 0;
            return new UsageInfo
            {
                AccountName = "Claude Desktop",
                Tier = "Không có dữ liệu",
                PercentUsed = 0,
                MessagesRemaining = "Chưa tìm thấy dữ liệu usage",
                ResetCountdown = "--",
                ResetTime = DateTime.Now,
                IsDemo = true,
                IsStale = true,
                DesktopRunning = desktopRunning,
                LastRefreshedAt = DateTime.Now,
                Note = "⚠ Hãy dùng thử Claude Desktop hoặc Claude Code CLI ít nhất một lần để tool đọc được dữ liệu usage."
            };
        }
    }
}
