using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageTracker.Services
{
    public record ReleaseInfo(string Tag, Version Version, string HtmlUrl, string ZipName, string ZipUrl, string SumsUrl, string? Notes);

    public record UpdateOptions(string ApiBase, string AllowedAssetPrefix)
    {
        public const string DefaultApiBase = "https://api.github.com/repos/ThanhNguyenVN93/claude-usage-tracker";
        public const string DefaultAssetPrefix = "https://github.com/ThanhNguyenVN93/claude-usage-tracker/releases/download/";

        // CLAUDE_USAGE_TRACKER_UPDATE_API chỉ để kiểm thử và chỉ nhận máy cục bộ,
        // nên không thể dùng nó để trỏ app sang nguồn tải bên ngoài.
        public static UpdateOptions FromEnvironment()
        {
            string? over = Environment.GetEnvironmentVariable("CLAUDE_USAGE_TRACKER_UPDATE_API");
            if (!string.IsNullOrWhiteSpace(over)
                && (over.StartsWith("http://127.0.0.1", StringComparison.Ordinal) || over.StartsWith("http://localhost", StringComparison.Ordinal)))
            {
                string origin = over.TrimEnd('/') + "/";
                return new UpdateOptions(over.TrimEnd('/'), origin);
            }
            return new UpdateOptions(DefaultApiBase, DefaultAssetPrefix);
        }
    }

    // Logic thuần/IO nhỏ, kiểm thử được mà không cần mạng.
    public static class UpdateLogic
    {
        public const string ExeName = "ClaudeUsageTracker.exe";
        public const string SumsName = "SHA256SUMS.txt";
        public const long MaxZipBytes  = 300L * 1024 * 1024;  // folder zip có thể ~200MB
        public const long MaxExeBytes  = 300L * 1024 * 1024;

        private static readonly Regex TagRx = new(@"^v?(\d+)\.(\d+)\.(\d+)$", RegexOptions.Compiled);
        private static readonly Regex ZipRx = new(@"^ClaudeUsageTracker-v(\d+\.\d+\.\d+)-win-x64\.zip$", RegexOptions.Compiled);
        private static readonly Regex HashRx = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

        public static Version? ParseVersion(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            var m = TagRx.Match(tag.Trim());
            return m.Success
                ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value))
                : null;
        }

        // Assembly version có 4 phần (0.3.0.0); chuẩn hoá về 3 phần để so với tag.
        public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

        public static bool IsNewer(Version current, Version latest) => latest > Normalize(current);

        public static ReleaseInfo? ParseRelease(string json, string allowedAssetPrefix)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
                if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;

                string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var version = ParseVersion(tag);
                if (version == null || !root.TryGetProperty("assets", out var assets)) return null;

                string? zipName = null, zipUrl = null, sumsUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!url.StartsWith(allowedAssetPrefix, StringComparison.Ordinal)) continue; // chỉ tin URL của repo này

                    var zm = ZipRx.Match(name);
                    if (zm.Success && zm.Groups[1].Value == version.ToString(3)) { zipName = name; zipUrl = url; }
                    else if (name == SumsName) sumsUrl = url;
                }

                if (zipName == null || zipUrl == null || sumsUrl == null) return null;

                string html = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
                string? body = root.TryGetProperty("body", out var b) ? b.GetString() : null;
                if (body != null && body.Length > 800) body = body[..800] + "…";

                return new ReleaseInfo(tag!, version, html, zipName, zipUrl, sumsUrl, body);
            }
            catch
            {
                return null;
            }
        }

        // Dòng dạng "<sha256>  <tên file>" (chịu được CRLF/LF).
        public static string? ParseSha256(string sums, string fileName)
        {
            foreach (var raw in sums.Split('\n'))
            {
                var line = raw.Trim();
                int sp = line.IndexOf(' ');
                if (sp != 64) continue;
                string hash = line[..64];
                string name = line[64..].Trim().TrimStart('*');
                if (name == fileName && HashRx.IsMatch(hash)) return hash.ToLowerInvariant();
            }
            return null;
        }

        public static bool VerifySha256(string filePath, string expectedHex)
        {
            using var fs = File.OpenRead(filePath);
            string actual = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            return string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase);
        }

        // Extract toàn bộ nội dung zip vào destDir, GIỮ NGUYÊN cây thư mục (với bảo vệ path traversal).
        // Trả về đường dẫn tới ClaudeUsageTracker.exe ở gốc destDir.
        // Trước đây chỉ lấy file tầng gốc và bỏ im lặng mọi thư mục con → bản cài qua nút "Cập nhật"
        // thiếu file so với bản tải thủ công, mà không báo lỗi gì.
        public static string ExtractExe(string zipPath, string destDir)
        {
            Directory.CreateDirectory(destDir);
            string destFull = Path.GetFullPath(destDir);

            using var zip = ZipFile.OpenRead(zipPath);

            bool exeFound = false;
            long totalExtracted = 0;
            foreach (var entry in zip.Entries)
            {
                // Bỏ qua mục thư mục (tên rỗng)
                if (string.IsNullOrEmpty(entry.Name)) continue;

                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);

                // Bảo vệ path traversal: so khớp sau khi đã chuẩn hoá đường dẫn tuyệt đối.
                string dest = Path.GetFullPath(Path.Combine(destFull, relative));
                if (!dest.StartsWith(destFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Path traversal bị chặn: {entry.FullName}");

                if (entry.Length > MaxExeBytes) throw new InvalidDataException($"File quá lớn: {entry.Name}");

                // Giới hạn từng file chưa đủ: zip nhiều file vẫn có thể làm đầy ổ đĩa.
                totalExtracted += entry.Length;
                if (totalExtracted > MaxZipBytes)
                    throw new InvalidDataException("Tổng dung lượng giải nén vượt giới hạn");

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using (var input = entry.Open())
                using (var output = File.Create(dest))
                    input.CopyTo(output);

                // exe phải nằm ở gốc: bản cài chạy ClaudeUsageTracker.exe ngay trong thư mục đó.
                if (!relative.Contains(Path.DirectorySeparatorChar)
                    && entry.Name.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    exeFound = true;
            }

            if (!exeFound) throw new InvalidDataException("Zip không chứa ClaudeUsageTracker.exe ở gốc");

            string exePath = Path.Combine(destFull, ExeName);
            using var fs = File.OpenRead(exePath);
            if (fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new InvalidDataException("File không phải exe Windows");
            return exePath;
        }

        public static void CleanupOld(string currentPath)
        {
            try { File.Delete(currentPath + ".old"); } catch { /* chưa xoá được thì lần sau */ }
        }
    }

    public class UpdateService : IDisposable
    {
        private readonly Version _current;
        private readonly UpdateOptions _options;
        private readonly HttpClient _http;

        public UpdateService(Version currentVersion, UpdateOptions? options = null, HttpMessageHandler? handler = null)
        {
            _current = currentVersion;
            _options = options ?? UpdateOptions.FromEnvironment();
            _http = handler != null ? new HttpClient(handler) : new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ClaudeUsageTracker/{UpdateLogic.Normalize(currentVersion)}");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        // Trả về bản phát hành mới hơn bản đang chạy, hoặc null nếu đã mới nhất / không kiểm tra được.
        public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync($"{_options.ApiBase}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return null;

            string json = await resp.Content.ReadAsStringAsync(ct);
            var release = UpdateLogic.ParseRelease(json, _options.AllowedAssetPrefix);
            return release != null && UpdateLogic.IsNewer(_current, release.Version) ? release : null;
        }

        // Tải zip + SHA256SUMS, kiểm tra băm, giải nén đúng file exe. Trả về đường dẫn exe mới.
        public async Task<string> DownloadAndStageAsync(ReleaseInfo release, CancellationToken ct = default)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ClaudeUsageTracker-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);

            try
            {
                string sums = await DownloadTextAsync(release.SumsUrl, 8 * 1024, ct);
                string? expected = UpdateLogic.ParseSha256(sums, release.ZipName);
                if (expected == null) throw new InvalidDataException($"{UpdateLogic.SumsName} không có băm cho {release.ZipName}");

                string zipPath = Path.Combine(dir, release.ZipName);
                await DownloadFileAsync(release.ZipUrl, zipPath, UpdateLogic.MaxZipBytes, ct);

                if (!UpdateLogic.VerifySha256(zipPath, expected))
                    throw new InvalidDataException("SHA-256 không khớp — đã huỷ cập nhật");

                return UpdateLogic.ExtractExe(zipPath, Path.Combine(dir, "extracted"));
            }
            catch
            {
                try { Directory.Delete(dir, true); } catch { /* dọn tạm thất bại cũng không sao */ }
                throw;
            }
        }

        private async Task<string> DownloadTextAsync(string url, long maxBytes, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("File quá lớn");

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            await CopyLimitedAsync(stream, ms, maxBytes, ct);
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        private async Task DownloadFileAsync(string url, string path, long maxBytes, CancellationToken ct)
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength > maxBytes) throw new InvalidDataException("File quá lớn");

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var fs = File.Create(path);
            await CopyLimitedAsync(stream, fs, maxBytes, ct);
        }

        public void Dispose() => _http.Dispose();

        private static async Task CopyLimitedAsync(Stream input, Stream output, long maxBytes, CancellationToken ct)
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException("File vượt giới hạn kích thước");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
    }
}
