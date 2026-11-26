using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeUsageTracker.Services
{
    /// <summary>
    /// Tự lấy sessionKey của claude.ai từ cookie store của Claude Desktop (app Electron),
    /// để người dùng không phải F12 → copy cookie thủ công mỗi khi phiên hết hạn.
    ///
    /// Claude Desktop tự gia hạn cookie này mỗi lần dùng, nên chừng nào còn đăng nhập
    /// Desktop thì widget luôn có sessionKey tươi.
    ///
    /// Cơ chế mã hoá (giống Chromium trên Windows):
    ///   1. %APPDATA%\Claude\Local State  → os_crypt.encrypted_key (base64)
    ///      → bỏ 5 byte tiền tố "DPAPI" → ProtectedData.Unprotect (CurrentUser) → khoá AES-256.
    ///   2. %APPDATA%\Claude\Network\Cookies (SQLite) → bản ghi cookies của claude.ai.
    ///   3. encrypted_value: "v10"/"v11" + nonce(12) + ciphertext + tag(16) → AES-GCM.
    ///   4. Chromium schema v24+ (Chromium 130+/Electron mới) chèn 32 byte SHA256(host_key)
    ///      vào ĐẦU plaintext trước khi mã hoá — phải cắt bỏ sau khi giải mã.
    ///
    /// Khoá DPAPI chỉ giải được dưới đúng tài khoản Windows đã tạo nó, nên cookie không
    /// mang sang máy khác được — reader này chỉ đọc phiên của chính người dùng, trên máy họ.
    /// </summary>
    public static class DesktopCookieReader
    {
        private const string TargetHost = "claude.ai";
        private const string CookieName = "sessionKey";
        private const int MaxLocalStateBytes = 5 * 1024 * 1024;
        private const int MaxCookieBlobBytes = 64 * 1024;
        private static readonly byte[] DpapiPrefix = Encoding.ASCII.GetBytes("DPAPI");

        public sealed record Result(bool Success, string? SessionKey, string? Error);

        /// <summary>Đường dẫn gốc của dữ liệu Claude Desktop (%APPDATA%\Claude).</summary>
        public static string? DefaultClaudeDir
        {
            get
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return string.IsNullOrEmpty(appData) ? null : Path.Combine(appData, "Claude");
            }
        }

        /// <summary>
        /// Thử lấy sessionKey. Không ném ngoại lệ ra ngoài — mọi lỗi gói trong Result.Error
        /// để caller chỉ coi đây là một nguồn "cố lấy được thì tốt".
        /// </summary>
        public static Result TryGetSessionKey(string? claudeDirOverride = null)
        {
            try
            {
                string? baseDir = string.IsNullOrWhiteSpace(claudeDirOverride) ? DefaultClaudeDir : claudeDirOverride;
                if (string.IsNullOrWhiteSpace(baseDir))
                    return new Result(false, null, "Không xác định được thư mục dữ liệu Claude Desktop.");

                // Bắt buộc đường dẫn tuyệt đối, chặn UNC/device để không vô tình đọc qua SMB (lộ NTLM)
                // hay đọc nhầm thư mục làm việc hiện tại.
                baseDir = Path.GetFullPath(baseDir);
                if (!Path.IsPathFullyQualified(baseDir) || baseDir.StartsWith(@"\\") || baseDir.StartsWith("//"))
                    return new Result(false, null, $"Thư mục Claude Desktop không hợp lệ: {baseDir}");

                string localStatePath = Path.Combine(baseDir, "Local State");
                string cookiesPath = Path.Combine(baseDir, "Network", "Cookies");

                if (!File.Exists(localStatePath))
                    return new Result(false, null, $"Không thấy Local State: {localStatePath}");
                if (!File.Exists(cookiesPath))
                    return new Result(false, null, $"Không thấy Cookies DB: {cookiesPath}");

                byte[] aesKey = LoadMasterKey(localStatePath);
                var cookie = ReadCookieBlob(cookiesPath);
                if (cookie is null)
                    return new Result(false, null, "Không tìm thấy cookie sessionKey của claude.ai trong Claude Desktop.");

                string plain = DecryptCookie(cookie.Value.Blob, aesKey, cookie.Value.HostKey);
                if (string.IsNullOrWhiteSpace(plain))
                    return new Result(false, null, "Giải mã sessionKey ra rỗng.");
                if (!IsPlausibleSessionKey(plain))
                    return new Result(false, null, "Giá trị giải mã không giống sessionKey hợp lệ.");

                return new Result(true, plain, null);
            }
            catch (Exception ex)
            {
                return new Result(false, null, $"Lỗi đọc cookie Desktop: {ex.Message}");
            }
        }

        /// <summary>
        /// sessionKey hợp lệ: đúng tiền tố, độ dài hợp lý, và CHỈ gồm ký tự base64url (A-Z a-z 0-9 _ -).
        /// Dùng allowlist chặt để giá trị cookie độc hại (nháy kép, xuống dòng, metachar shell) không
        /// lọt vào header curl -K / HttpClient.
        /// </summary>
        internal static bool IsPlausibleSessionKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length is < 20 or > 512) return false;
            if (!value.StartsWith("sk-ant-", StringComparison.Ordinal)) return false;
            foreach (char c in value)
            {
                bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>Đọc & giải mã khoá AES-256 chủ từ Local State.</summary>
        internal static byte[] LoadMasterKey(string localStatePath)
        {
            var info = new FileInfo(localStatePath);
            if (info.Length > MaxLocalStateBytes)
                throw new InvalidDataException("Local State lớn bất thường — từ chối đọc.");

            string json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt) ||
                osCrypt.ValueKind != JsonValueKind.Object ||
                !osCrypt.TryGetProperty("encrypted_key", out var keyEl) ||
                keyEl.ValueKind != JsonValueKind.String ||
                keyEl.GetString() is not { Length: > 0 } b64)
            {
                throw new InvalidDataException("Local State không có os_crypt.encrypted_key.");
            }

            byte[] blob = Convert.FromBase64String(b64);
            if (blob.Length <= DpapiPrefix.Length || !blob.AsSpan(0, DpapiPrefix.Length).SequenceEqual(DpapiPrefix))
                throw new InvalidDataException("encrypted_key không có tiền tố DPAPI như mong đợi.");

            byte[] dpapiCipher = blob.AsSpan(DpapiPrefix.Length).ToArray();
            byte[] key = ProtectedData.Unprotect(dpapiCipher, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            if (key.Length != 32)
                throw new InvalidDataException($"Khoá AES giải ra sai độ dài ({key.Length} byte, cần 32).");
            return key;
        }

        /// <summary>
        /// Lấy encrypted_value + host_key của cookie sessionKey cho đúng claude.ai (loại subdomain,
        /// loại cookie đã hết hạn, ưu tiên cookie secure). File gốc khoá khi Desktop chạy nên sao ra temp.
        /// </summary>
        private static (byte[] Blob, string HostKey)? ReadCookieBlob(string cookiesPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"cut-cookies-{Guid.NewGuid():N}.db");
            try
            {
                CopyLockedFile(cookiesPath, tmp);
                // WAL/SHM nếu có — để tránh đọc thiếu cookie vừa ghi (khi Desktop vừa đóng chưa checkpoint).
                TryCopySidecar(cookiesPath + "-wal", tmp + "-wal");
                TryCopySidecar(cookiesPath + "-shm", tmp + "-shm");

                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = tmp,
                    Mode = SqliteOpenMode.ReadOnly,
                    // Pooling mặc định true giữ handle tới file tạm sau Dispose → File.Delete thất bại,
                    // bỏ lại bản sao nguyên vẹn của cookie DB trong %TEMP%. Tắt để dọn sạch được.
                    Pooling = false
                };
                using var conn = new SqliteConnection(csb.ToString());
                conn.Open();

                using var cmd = conn.CreateCommand();
                // Chỉ khớp ĐÚNG claude.ai / .claude.ai (Chromium không gửi cookie subdomain tới host gốc).
                // Loại cookie đã hết hạn: expires_utc = 0 là session cookie (còn hiệu lực), >0 phải lớn hơn now.
                // Ưu tiên cookie secure và còn hạn muộn nhất.
                cmd.CommandText =
                    "SELECT encrypted_value, host_key FROM cookies " +
                    "WHERE name = $name AND host_key IN ($host, $dotHost) " +
                    "AND (expires_utc = 0 OR expires_utc > $now) " +
                    "ORDER BY is_secure DESC, expires_utc DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$name", CookieName);
                cmd.Parameters.AddWithValue("$host", TargetHost);
                cmd.Parameters.AddWithValue("$dotHost", "." + TargetHost);
                cmd.Parameters.AddWithValue("$now", WebkitNowMicros());

                using var reader = cmd.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0))
                    return null;

                using var stream = reader.GetStream(0);
                using var ms = new MemoryStream();
                byte[] buf = new byte[8192];
                int n, total = 0;
                while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > MaxCookieBlobBytes)
                        throw new InvalidDataException("encrypted_value lớn bất thường — từ chối đọc.");
                    ms.Write(buf, 0, n);
                }
                byte[] blob = ms.ToArray();
                string hostKey = reader.IsDBNull(1) ? "" : reader.GetString(1);
                return blob.Length == 0 ? null : (blob, hostKey);
            }
            finally
            {
                TryDelete(tmp);
                TryDelete(tmp + "-wal");
                TryDelete(tmp + "-shm");
            }
        }

        // Claude Desktop giữ lock trên Cookies DB. FileMode.CreateNew: không đi theo symlink sẵn có
        // trong %TEMP% (tên GUID nên gần như chắc chắn chưa tồn tại). FileShare.Delete để không chặn Desktop.
        private static void CopyLockedFile(string src, string dst)
        {
            using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var d = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            s.CopyTo(d);
        }

        private static void TryCopySidecar(string src, string dst)
        {
            try { if (File.Exists(src)) CopyLockedFile(src, dst); } catch { /* không có WAL/SHM cũng không sao */ }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* dọn được lúc nào hay lúc đó */ }
        }

        // Thời điểm hiện tại theo mốc WebKit: micro-giây kể từ 1601-01-01 UTC.
        private static long WebkitNowMicros()
        {
            var epoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (long)((DateTime.UtcNow - epoch).TotalMilliseconds * 1000);
        }

        /// <summary>
        /// Giải mã blob cookie Chromium (v10/v11 = AES-256-GCM). Với schema v24+ Chromium gắn
        /// 32 byte SHA256(host_key) vào đầu plaintext — cắt bỏ nếu khớp. Tách riêng để unit test
        /// được mà không cần SQLite hay DPAPI thật.
        /// </summary>
        internal static string DecryptCookie(byte[] encrypted, byte[] aesKey, string? hostKey = null)
        {
            if (encrypted.Length < 3)
                throw new InvalidDataException("Blob cookie quá ngắn.");

            byte v0 = encrypted[0], v1 = encrypted[1], v2 = encrypted[2];
            bool isGcm = v0 == (byte)'v' && v1 == (byte)'1' && (v2 == (byte)'0' || v2 == (byte)'1');
            if (!isGcm)
            {
                // v20 = App-Bound Encryption (khoá gắn với tiến trình Chromium) — KHÔNG giải được bằng
                // khoá DPAPI của user, báo lỗi rõ thay vì rơi vào DPAPI fallback mù.
                if (v0 == (byte)'v' && v1 == (byte)'2' && v2 == (byte)'0')
                    throw new InvalidDataException("Cookie dùng App-Bound Encryption (v20) — không hỗ trợ.");

                // Build rất cũ: DPAPI thẳng trên giá trị cookie.
                try
                {
                    byte[] dp = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dp);
                }
                catch
                {
                    throw new InvalidDataException("Blob cookie không phải v10/v11 và cũng không giải được bằng DPAPI.");
                }
            }

            const int prefixLen = 3, nonceLen = 12, tagLen = 16;
            if (encrypted.Length < prefixLen + nonceLen + tagLen)
                throw new InvalidDataException("Blob cookie v10/v11 thiếu nonce/tag.");

            ReadOnlySpan<byte> all = encrypted;
            ReadOnlySpan<byte> nonce = all.Slice(prefixLen, nonceLen);
            ReadOnlySpan<byte> tag = all.Slice(encrypted.Length - tagLen, tagLen);
            ReadOnlySpan<byte> cipher = all.Slice(prefixLen + nonceLen, encrypted.Length - prefixLen - nonceLen - tagLen);

            byte[] plain = new byte[cipher.Length];
            using (var gcm = new AesGcm(aesKey, tagLen))
            {
                gcm.Decrypt(nonce, cipher, tag, plain);
            }

            // Schema v24+: 32 byte đầu là SHA256(host_key). Cắt nếu khớp (version-agnostic: schema cũ
            // không có prefix nên hash gần như không bao giờ trùng 32 byte đầu).
            ReadOnlySpan<byte> body = plain;
            if (!string.IsNullOrEmpty(hostKey) && plain.Length >= 32)
            {
                byte[] hostHash = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey));
                if (CryptographicOperations.FixedTimeEquals(plain.AsSpan(0, 32), hostHash))
                    body = plain.AsSpan(32);
            }
            return Encoding.UTF8.GetString(body);
        }
    }
}
