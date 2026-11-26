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

        // Org-id được cache sau lần đầu gọi API thành công (gắn với sessionKey đã dùng để lấy nó).
        private string? _cachedOrgId;
        private string? _cachedOrgKey;

        // SyncOnceAsync bị gọi đồng thời từ vòng nền (thread pool), OnStartup, Settings và nút tiếp tục sync
        // → read-modify-write cli-usage.json / plan-usage-history.json chồng nhau làm mất sample. Tuần tự hoá.
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private volatile bool _paused;
        private volatile bool _useCurlFallback;
        private DateTime _lastAuthWarnAt;

        // Key lấy được từ Claude Desktop, giữ lại để không phải giải mã cookie mỗi vòng sync.
        private string? _lastGoodDesktopKey;
        // Lỗi auth chờ báo: gom lại trong một lượt sync, chỉ bắn toast nếu mọi key đều hỏng.
        private string? _pendingAuthMsg;

        private const string ChromeUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        public string? LastError { get; private set; }
        public event Action<string>? OnAuthError;
        // Phát ra khi lấy được key mới từ cookie Desktop, để App lưu vào config (sống qua restart).
        public event Action<string>? OnDesktopKeyObtained;

        public ClaudeApiSyncService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _http.DefaultRequestHeaders.Add("User-Agent", ChromeUserAgent);
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>Tạm dừng sync (không dừng timer, chỉ bỏ qua mỗi lần gọi).</summary>
        public bool Paused { get => _paused; set => _paused = value; }

        /// <summary>sessionKey hợp lệ: đúng tiền tố và chỉ gồm ký tự token an toàn.</summary>
        /// Chặn giá trị chứa nháy kép / xuống dòng / dấu ; — tránh chèn directive vào curl -K
        /// và tránh FormatException khi gắn header HttpClient.
        internal static bool IsValidKeyFormat(string? key)
            => key is not null && DesktopCookieReader.IsPlausibleSessionKey(key);

        /// <summary>Gọi một lần rồi trả về, không ném exception ra ngoài.</summary>
        public async Task SyncOnceAsync(AppConfig config)
        {
            if (Paused) return;

            // Lấy lock TRƯỚC khi đụng tới việc chọn/đọc key: việc này đọc-ghi _lastGoodDesktopKey
            // và có thể đọc file cookie + giải mã DPAPI → không được chạy đua trên nhiều thread.
            try { await _syncLock.WaitAsync(); }
            catch (ObjectDisposedException) { return; }

            string? keyToPersist = null;
            string? authMsg = null;
            try
            {
                if (Paused) return; // Paused có thể bật lên trong lúc chờ lock.
                (keyToPersist, authMsg) = await SyncInsideLockAsync(config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] Exception: {ex.Message}");
            }
            finally
            {
                try { _syncLock.Release(); } catch (ObjectDisposedException) { }
            }

            // Bắn event NGOÀI lock: handler có thể ghi disk / đụng UI thread / gọi ngược vào đây —
            // giữ lock khi gọi sẽ treo lock hoặc deadlock (SemaphoreSlim không reentrant).
            if (keyToPersist is not null)
            {
                try { OnDesktopKeyObtained?.Invoke(keyToPersist); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApiSync] OnDesktopKeyObtained: {ex.Message}"); }
            }
            if (authMsg is not null)
            {
                try { OnAuthError?.Invoke(authMsg); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApiSync] OnAuthError: {ex.Message}"); }
            }
        }

        // Trả về (key Desktop cần lưu lại, thông báo lỗi auth cần bắn) — việc bắn để lại cho caller
        // làm sau khi nhả lock.
        private async Task<(string? keyToPersist, string? authMsg)> SyncInsideLockAsync(AppConfig config)
        {
            _pendingAuthMsg = null;
            var tried = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, isDesktop) in EnumerateCandidateKeys(config))
            {
                if (!tried.Add(key)) continue;

                bool ok = await DoSyncAsync(key);
                if (ok)
                {
                    // Chỉ lưu key Desktop SAU khi nó đã sync thành công — không ghi key hết hạn vào config.
                    string? persist = null;
                    if (isDesktop && key != _lastGoodDesktopKey)
                    {
                        _lastGoodDesktopKey = key;
                        persist = key;
                    }
                    return (persist, null);
                }
            }

            // Mọi ứng viên đều hỏng. Chỉ coi là lỗi auth (và kích cooldown) khi THỰC SỰ có lỗi auth
            // và đang có người nghe — tránh nuốt cảnh báo đầu tiên khi UI chưa gắn handler.
            if (_pendingAuthMsg is { } msg && OnAuthError is not null &&
                (DateTime.UtcNow - _lastAuthWarnAt).TotalMinutes >= 60)
            {
                _lastAuthWarnAt = DateTime.UtcNow;
                return (null, msg);
            }
            return (null, null);
        }

        // Danh sách key theo thứ tự ưu tiên, KHÔNG side-effect (không gán _lastGoodDesktopKey ở đây —
        // chỉ PersistDesktopKey sau khi verify mới gán). Key thủ công trước (người dùng có thể cố ý dùng
        // tài khoản khác), rồi key Desktop (đang nhớ trong RAM, rồi cache config đã giải mã, rồi đọc mới).
        private IEnumerable<(string key, bool isDesktop)> EnumerateCandidateKeys(AppConfig config)
        {
            if (IsValidKeyFormat(config.SessionKey))
                yield return (config.SessionKey, false);

            if (!config.AutoSessionKeyFromDesktop)
                yield break;

            if (IsValidKeyFormat(_lastGoodDesktopKey))
                yield return (_lastGoodDesktopKey!, true);

            // Cache từ phiên trước (DPAPI) — giải mã; HashSet 'tried' ở caller tự loại nếu trùng key RAM.
            string cached = ConfigService.UnprotectSecret(config.CachedDesktopSessionKey);
            if (IsValidKeyFormat(cached))
                yield return (cached, true);

            // Đọc mới từ cookie (chỉ khi Desktop đã tắt — lúc chạy file bị khóa độc quyền).
            string? fresh = ReadDesktopCookieKey(config);
            if (IsValidKeyFormat(fresh))
                yield return (fresh!, true);
        }

        // Đọc key từ cookie Desktop. KHÔNG tự lưu/không bắn event.
        private string? ReadDesktopCookieKey(AppConfig config)
        {
            if (DesktopIsRunning())
                return null; // file cookie bị khóa khi Desktop chạy → đọc chắc chắn fail, khỏi thử.

            var r = DesktopCookieReader.TryGetSessionKey(
                string.IsNullOrWhiteSpace(config.ClaudeDesktopDirOverride) ? null : config.ClaudeDesktopDirOverride);
            if (!r.Success)
                System.Diagnostics.Debug.WriteLine($"[ApiSync] Auto-key từ Desktop thất bại: {r.Error}");
            return r.SessionKey;
        }

        // Dùng cho unit test: lấy ứng viên key đầu tiên (không side-effect).
        internal string? ResolveSessionKey(AppConfig config)
        {
            foreach (var (key, _) in EnumerateCandidateKeys(config))
                return key;
            return null;
        }

        // Tách ra để unit test ghi đè được (thật thì hỏi ProcessUtil).
        internal Func<bool>? DesktopRunningProbe { get; set; }
        private bool DesktopIsRunning() => DesktopRunningProbe?.Invoke() ?? ProcessUtil.IsClaudeRunning();

        /// <summary>Bắt đầu (hoặc khởi động lại) vòng lặp sync định kỳ (chạy ngầm).</summary>
        public void StartBackground(AppConfig config, bool syncImmediately = true)
        {
            // Huỷ vòng cũ trước — gọi lại từ Settings không được tạo 2 vòng song song / leak CTS.
            // Interlocked.Exchange: hai lần StartBackground song song không ghi đè _cts của nhau.
            var old = Interlocked.Exchange(ref _cts, null);
            try { old?.Cancel(); } catch (ObjectDisposedException) { }
            old?.Dispose();

            bool hasManual = IsValidKeyFormat(config.SessionKey);
            if (!hasManual && !config.AutoSessionKeyFromDesktop)
                return;

            int secs = config.ApiSyncIntervalSeconds;
            if (secs <= 0) return; // 0 = chỉ sync một lần khi khởi động, không lặp
            var interval = TimeSpan.FromSeconds(Math.Max(30, secs));

            var cts = new CancellationTokenSource();
            _cts = cts;
            var token = cts.Token;

            Task.Run(async () =>
            {
                // OnStartup đã await SyncOnceAsync trước đó → không sync trùng lần nữa.
                if (syncImmediately) await SyncOnceAsync(config);

                while (!token.IsCancellationRequested)
                {
                    // CTS có thể bị Dispose ngay sau Cancel (StartBackground gọi lại từ Settings):
                    // Delay khi đó ném ObjectDisposedException chứ không phải TaskCanceled.
                    try { await Task.Delay(interval, token); }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    if (token.IsCancellationRequested) break; // huỷ đúng lúc Delay vừa xong → đừng sync thêm.
                    await SyncOnceAsync(config);
                }
            }, token);
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }

        // ── Internals ────────────────────────────────────────────────────────

        /// <summary>Trả true nếu sync thành công (đã ghi dữ liệu usage).</summary>
        private async Task<bool> DoSyncAsync(string sessionKey)
        {
            // Đổi sessionKey (tài khoản khác) → org-id cũ không còn đúng, server trả 403 chứ không phải 401.
            if (_cachedOrgKey != sessionKey) _cachedOrgId = null;

            string? orgId = _cachedOrgId ?? await FetchOrgIdAsync(sessionKey);
            if (string.IsNullOrEmpty(orgId))
            {
                System.Diagnostics.Debug.WriteLine("[ApiSync] Không lấy được org_id — sessionKey hết hạn?");
                return false;
            }
            _cachedOrgId = orgId;
            _cachedOrgKey = sessionKey;

            var usage = await FetchUsageAsync(sessionKey, orgId);
            if (usage == null)
            {
                System.Diagnostics.Debug.WriteLine("[ApiSync] Không lấy được dữ liệu usage.");
                return false;
            }

            LastError = null;
            WriteCliUsage(usage);
            WriteDesktopHistory(usage);

            System.Diagnostics.Debug.WriteLine(
                $"[ApiSync] ✅ session={usage.FiveHourPct:0.0}% weekly={usage.SevenDayPct:0.0}% " +
                $"reset={usage.FiveHourResetsAt:HH:mm}");
            return true;
        }

        // Ghi lại lỗi auth nhưng CHƯA bắn toast — việc thông báo để SyncInsideLockAsync quyết định
        // sau khi đã thử hết các key ứng viên, tránh báo lỗi giả khi một key sau đó thành công.
        private void ReportAuthFailure(int statusCode)
        {
            // Sau khi curl fallback đã bật mà vẫn 403 thì gần như chắc là sessionKey hết hạn,
            // không phải Cloudflare chặn fingerprint .NET nữa — nói thẳng để người dùng khỏi đoán.
            string cause = _useCurlFallback
                ? "sessionKey đã hết hạn"
                : "sessionKey hết hạn hoặc bị Cloudflare chặn";
            string fix = DesktopIsRunning()
                ? "Mở lại claude.ai để Claude Desktop làm mới phiên, hoặc đóng Desktop một lát để widget tự lấy key mới."
                : "Đăng nhập lại Claude Desktop, hoặc dán sessionKey mới trong Cài đặt.";
            string msg = $"API sync bị từ chối (HTTP {statusCode}) — {cause}. {fix}";
            LastError = msg;
            _pendingAuthMsg = msg;
        }

        // ── HTTP calls ───────────────────────────────────────────────────────

        private async Task<(int StatusCode, string? Body)> SendGetAsync(string url, string sessionKey)
        {
            if (!_useCurlFallback)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                AddHeaders(req, sessionKey);
                using var res = await _http.SendAsync(req);
                int code = (int)res.StatusCode;
                if (res.IsSuccessStatusCode)
                    return (code, await res.Content.ReadAsStringAsync());

                // Cloudflare thường chặn TLS/HTTP2 fingerprint của .NET HttpClient bằng HTTP 403,
                // trong khi curl.exe tích hợp sẵn của Windows qua được. Tự động chuyển sang curl.exe.
                if (code == 403)
                    _useCurlFallback = true;
                else
                    return (code, null);
            }

            return await SendCurlGetAsync(url, sessionKey);
        }

        private static async Task<(int StatusCode, string? Body)> SendCurlGetAsync(string url, string sessionKey)
        {
            try
            {
                string curlPath = Path.Combine(Environment.SystemDirectory, "curl.exe");
                if (!File.Exists(curlPath)) curlPath = "curl.exe";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = curlPath,
                    Arguments = "-s -w \"\\n%{http_code}\" -K -",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) return (0, null);

                // Truyền URL và Cookie qua stdin (-K -) để sessionKey không lộ trên dòng lệnh process.
                string configInput =
                    $"url = \"{url}\"\n" +
                    $"header = \"Cookie: sessionKey={sessionKey}\"\n" +
                    $"header = \"Accept: application/json\"\n" +
                    $"header = \"Referer: https://claude.ai/\"\n" +
                    $"header = \"Origin: https://claude.ai\"\n" +
                    $"header = \"anthropic-client-platform: web_claude_ai\"\n" +
                    $"header = \"User-Agent: {ChromeUserAgent}\"\n";

                await proc.StandardInput.WriteAsync(configInput);
                await proc.StandardInput.FlushAsync();
                proc.StandardInput.Close();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                string output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);

                int lastNl = output.LastIndexOf('\n');
                if (lastNl < 0)
                    return int.TryParse(output.Trim(), out int onlyCode) ? (onlyCode, null) : (0, null);

                string body = output[..lastNl].TrimEnd('\r');
                string codeStr = output[(lastNl + 1)..].Trim();
                return int.TryParse(codeStr, out int statusCode) ? (statusCode, body) : (0, null);
            }
            catch
            {
                return (0, null);
            }
        }

        private async Task<string?> FetchOrgIdAsync(string sessionKey)
        {
            var (code, body) = await SendGetAsync("https://claude.ai/api/organizations", sessionKey);
            if (code is < 200 or >= 300 || string.IsNullOrEmpty(body))
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] /organizations → {code}");
                _cachedOrgId = null; // invalidate cache on auth failure
                if (code is 401 or 403) ReportAuthFailure(code);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var org = root[0];
            return org.TryGetProperty("uuid", out var u) ? u.GetString() :
                   org.TryGetProperty("id",   out var i) ? i.GetString() : null;
        }

        private async Task<ApiUsageResult?> FetchUsageAsync(string sessionKey, string orgId)
        {
            var (code, body) = await SendGetAsync($"https://claude.ai/api/organizations/{orgId}/usage", sessionKey);
            if (code is < 200 or >= 300 || string.IsNullOrEmpty(body))
            {
                if (code is 401 or 403 or 404) _cachedOrgId = null;
                if (code is 401 or 403) ReportAuthFailure(code);
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            return ParseUsage(doc.RootElement);
        }

        private static void AddHeaders(HttpRequestMessage req, string sessionKey)
        {
            req.Headers.Add("Cookie",   $"sessionKey={sessionKey}");
            req.Headers.Add("Accept",   "application/json");
            req.Headers.Add("Referer",  "https://claude.ai/");
            req.Headers.Add("Origin",   "https://claude.ai");
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
            DateTime? weeklyResetsAt = null;

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

            // seven_day.utilization & resets_at
            if (root.TryGetProperty("seven_day", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                if (sd.TryGetProperty("utilization", out var sdUtil) && sdUtil.ValueKind == JsonValueKind.Number)
                    sevenDayPct = sdUtil.GetDouble();
                if (sd.TryGetProperty("resets_at", out var sdRat) && sdRat.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(sdRat.GetString(), out var sdDto))
                    weeklyResetsAt = sdDto.UtcDateTime;
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
                    else if (kind.Contains("weekly"))
                    {
                        if (sevenDayPct == 0) sevenDayPct = pct;
                        if (weeklyResetsAt == null && item.TryGetProperty("resets_at", out var r3)
                            && r3.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(r3.GetString(), out var dto3))
                            weeklyResetsAt = dto3.UtcDateTime;
                    }
                }
            }

            if (fiveHourPct == 0 && sevenDayPct == 0 && resetsAt == null) return null;

            return new ApiUsageResult
            {
                FiveHourPct      = fiveHourPct,
                SevenDayPct      = sevenDayPct,
                FiveHourResetsAt = resetsAt?.ToLocalTime() ?? DateTime.Now.AddHours(5),
                SevenDayResetsAt = weeklyResetsAt?.ToLocalTime(),
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
                            // sevenDayResetsAt / costUsd do statusline ghi, API không có -> phải giữ.
                            if (prop.Name is "fiveHour" or "sevenDay" or "t" or "fiveHourResetsAt")
                                continue;
                            if (u.SevenDayResetsAt.HasValue && prop.Name == "sevenDayResetsAt")
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
                if (u.SevenDayResetsAt.HasValue)
                {
                    long weeklyResetS = new DateTimeOffset(u.SevenDayResetsAt.Value.ToUniversalTime()).ToUnixTimeSeconds();
                    w.WriteNumber("sevenDayResetsAt", weeklyResetS);
                }
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

                AtomicFile.WriteAllBytes(TrackerPaths.CliUsage, ms.ToArray());
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
                // InvariantCulture bắt buộc: culture vi-VN dùng dấu phẩy thập phân → "fh":8,5 làm hỏng JSON.
                string newSample = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{{\"t\":{nowMs},\"u\":{{\"fh\":{u.FiveHourPct:0.##},\"sd\":{u.SevenDayPct:0.##}}}}}");
                samplesRaw.Add(newSample);

                string json = $"{{\"samples\":[{string.Join(",", samplesRaw)}]}}";
                AtomicFile.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiSync] WriteDesktopHistory error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            var cts = _cts;
            _cts = null;
            try { cts?.Cancel(); } catch (ObjectDisposedException) { }
            cts?.Dispose();
            _http.Dispose();
        }

        private sealed class ApiUsageResult
        {
            public double FiveHourPct        { get; set; }
            public double SevenDayPct        { get; set; }
            public DateTime FiveHourResetsAt { get; set; }
            public DateTime? SevenDayResetsAt { get; set; }
        }
    }
}
