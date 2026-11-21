using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageTracker.Services
{
    /// <summary>
    /// Gọi /api/organizations/{id}/usage và ghi kết quả vào đúng format
    /// mà ClaudeUsageService đọc, giải quyết triệt để vấn đề đồng bộ trễ.
    ///
    /// Ghi 2 file:
    ///   cli-usage.json      — fiveHour%, sevenDay%, fiveHourResetsAt (unix s)
    ///   plan-usage-history.json — { samples: [ { t, u: { fh, sd } } ] }
    /// </summary>
    public class ClaudeApiSyncService : IDisposable
    {
        private readonly HttpClient _http;
        private CancellationTokenSource? _cts;

        // Org-id được cache sau lần đầu gọi API thành công.
        private string? _cachedOrgId;

        public ClaudeApiSyncService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ClaudeUsageTracker/2.0");
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Gọi một lần rồi trả về, không ném exception ra ngoài.</summary>
        public async Task SyncOnceAsync(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.SessionKey) ||
                !config.SessionKey.StartsWith("sk-ant-"))
                return;

            try
            {
                await DoSyncAsync(config.SessionKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] Exception: {ex.Message}");
            }
        }

        /// <summary>Bắt đầu vòng lặp sync định kỳ (chạy ngầm).</summary>
        public void StartBackground(AppConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.SessionKey) ||
                !config.SessionKey.StartsWith("sk-ant-"))
                return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            int secs = config.ApiSyncIntervalSeconds;
            if (secs <= 0) return; // 0 = chỉ sync một lần khi khởi động, không lặp
            var interval = TimeSpan.FromSeconds(Math.Max(30, secs));

            Task.Run(async () =>
            {
                // Sync ngay khi khởi động
                await SyncOnceAsync(config);

                while (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(interval, token); } catch (TaskCanceledException) { break; }
                    await SyncOnceAsync(config);
                }
            }, token);
        }

        public void Stop() => _cts?.Cancel();

        // ── Internals ────────────────────────────────────────────────────────

        private async Task DoSyncAsync(string sessionKey)
        {
            string? orgId = _cachedOrgId ?? await FetchOrgIdAsync(sessionKey);
            if (string.IsNullOrEmpty(orgId))
            {
                System.Diagnostics.Debug.WriteLine("[ApiSync] Không lấy được org_id — sessionKey hết hạn?");
                return;
            }
            _cachedOrgId = orgId;

            var usage = await FetchUsageAsync(sessionKey, orgId);
            if (usage == null)
            {
                System.Diagnostics.Debug.WriteLine("[ApiSync] Không lấy được dữ liệu usage.");
                return;
            }

            WriteCliUsage(usage);
            WriteDesktopHistory(usage);

            System.Diagnostics.Debug.WriteLine(
                $"[ApiSync] ✅ session={usage.FiveHourPct:0.0}% weekly={usage.SevenDayPct:0.0}% " +
                $"reset={usage.FiveHourResetsAt:HH:mm}");
        }

        // ── HTTP calls ───────────────────────────────────────────────────────

        private async Task<string?> FetchOrgIdAsync(string sessionKey)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://claude.ai/api/organizations");
            AddHeaders(req, sessionKey);
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] /organizations → {(int)res.StatusCode}");
                _cachedOrgId = null; // invalidate cache on auth failure
                return null;
            }
            string body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var org = root[0];
            return org.TryGetProperty("uuid", out var u) ? u.GetString() :
                   org.TryGetProperty("id",   out var i) ? i.GetString() : null;
        }

        private async Task<ApiUsageResult?> FetchUsageAsync(string sessionKey, string orgId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://claude.ai/api/organizations/{orgId}/usage");
            AddHeaders(req, sessionKey);
            var res = await _http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                if ((int)res.StatusCode == 401) _cachedOrgId = null;
                return null;
            }
            string body = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return ParseUsage(doc.RootElement);
        }

        private static void AddHeaders(HttpRequestMessage req, string sessionKey)
        {
            req.Headers.Add("Cookie",   $"sessionKey={sessionKey}");
            req.Headers.Add("Accept",   "application/json");
            req.Headers.Add("Referer",  "https://claude.ai/");
            req.Headers.Add("anthropic-client-platform", "web_claude_ai");
        }

        // ── Parse /usage response ────────────────────────────────────────────

        // Cấu trúc thực tế (xác minh 21/11/2026):
        //   five_hour  : { utilization: 8.0,  resets_at: "2026-...+00:00" }
        //   limits[]   : [ { kind:"session",    percent:8,  resets_at:"..." },
        //                  { kind:"weekly_all", percent:15, resets_at:"..." } ]
        private static ApiUsageResult? ParseUsage(JsonElement root)
        {
            double fiveHourPct  = 0;
            double sevenDayPct  = 0;
            DateTime? resetsAt  = null;

            // five_hour.utilization
            if (root.TryGetProperty("five_hour", out var fh) && fh.ValueKind == JsonValueKind.Object)
            {
                if (fh.TryGetProperty("utilization", out var util) && util.ValueKind == JsonValueKind.Number)
                    fiveHourPct = util.GetDouble();
                if (fh.TryGetProperty("resets_at", out var rat) && rat.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(rat.GetString(), out var dto))
                        resetsAt = dto.UtcDateTime;
                }
            }

            // limits[] → session % và weekly %
            if (root.TryGetProperty("limits", out var limArr) && limArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in limArr.EnumerateArray())
                {
                    if (!item.TryGetProperty("kind", out var kindEl)) continue;
                    string kind = kindEl.GetString() ?? "";
                    double pct = item.TryGetProperty("percent", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetDouble() : 0;

                    if (kind == "session" && fiveHourPct == 0)
                    {
                        fiveHourPct = pct;
                        if (resetsAt == null && item.TryGetProperty("resets_at", out var r2)
                            && r2.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(r2.GetString(), out var dto2))
                            resetsAt = dto2.UtcDateTime;
                    }
                    else if (kind.Contains("weekly") && sevenDayPct == 0)
                        sevenDayPct = pct;
                }
            }

            if (fiveHourPct == 0 && sevenDayPct == 0 && resetsAt == null) return null;

            return new ApiUsageResult
            {
                FiveHourPct      = fiveHourPct,
                SevenDayPct      = sevenDayPct,
                FiveHourResetsAt = resetsAt?.ToLocalTime() ?? DateTime.Now.AddHours(5),
            };
        }

        // ── Write cli-usage.json ─────────────────────────────────────────────

        private static void WriteCliUsage(ApiUsageResult u)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TrackerPaths.CliUsage)!);

                // Bảo toàn các field khác (model, sessionId, contextPercent, v.v.) nếu file đã tồn tại
                var existing = new System.Collections.Generic.Dictionary<string, object?>();
                if (File.Exists(TrackerPaths.CliUsage))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(TrackerPaths.CliUsage));
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            // Giữ lại những field không phải usage numbers
                            if (prop.Name is "fiveHour" or "sevenDay" or "t" or "fiveHourResetsAt")
                                continue;
                            existing[prop.Name] = prop.Value.Clone();
                        }
                    }
                    catch { /* corrupt file, start fresh */ }
                }

                long nowMs  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long resetS = new DateTimeOffset(u.FiveHourResetsAt.ToUniversalTime()).ToUnixTimeSeconds();

                using var ms  = new MemoryStream();
                using var w   = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
                w.WriteStartObject();
                w.WriteNumber("fiveHour",         u.FiveHourPct);
                w.WriteNumber("sevenDay",          u.SevenDayPct);
                w.WriteNumber("t",                 nowMs);
                w.WriteNumber("fiveHourResetsAt",  resetS);
                // Ghi lại các field cũ
                foreach (var kv in existing)
                {
                    if (kv.Value is JsonElement je)
                    {
                        w.WritePropertyName(kv.Key);
                        je.WriteTo(w);
                    }
                }
                w.WriteEndObject();
                w.Flush();

                File.WriteAllBytes(TrackerPaths.CliUsage, ms.ToArray());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] WriteCliUsage error: {ex.Message}");
            }
        }

        // ── Write plan-usage-history.json ────────────────────────────────────

        private static void WriteDesktopHistory(ApiUsageResult u)
        {
            try
            {
                string path = TrackerPaths.DesktopHistory;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Đọc samples cũ
                var samplesRaw = new System.Collections.Generic.List<string>();
                if (File.Exists(path))
                {
                    try
                    {
                        string existing = File.ReadAllText(path);
                        using var doc = JsonDocument.Parse(existing);
                        if (doc.RootElement.TryGetProperty("samples", out var arr)
                            && arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var el in arr.EnumerateArray())
                                samplesRaw.Add(el.GetRawText());
                            if (samplesRaw.Count > 499)
                                samplesRaw = samplesRaw.GetRange(samplesRaw.Count - 499, 499);
                        }
                    }
                    catch { /* corrupt, start fresh */ }
                }

                // Thêm sample mới
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string newSample = $"{{\"t\":{nowMs},\"u\":{{\"fh\":{u.FiveHourPct:0.##},\"sd\":{u.SevenDayPct:0.##}}}}}";
                samplesRaw.Add(newSample);

                string json = $"{{\"samples\":[{string.Join(",", samplesRaw)}]}}";
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] WriteDesktopHistory error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _http.Dispose();
        }

        private sealed class ApiUsageResult
        {
            public double FiveHourPct      { get; set; }
            public double SevenDayPct      { get; set; }
            public DateTime FiveHourResetsAt { get; set; }
        }
    }
}
