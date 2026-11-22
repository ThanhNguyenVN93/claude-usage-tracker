using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTracker.Services
{
    // Đọc/ghi khoá "model" trong settings.json của Claude Code, giữ nguyên mọi khoá khác.
    // Không bao giờ ghi đè khi file không phải JSON hợp lệ.
    public class ClaudeSettingsFile
    {
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private readonly string _path;

        // ReadModel() được gọi mỗi lần đánh giá quota (30 giây/lần khi bật auto-model).
        // Cache theo mtime+size để không đọc đĩa của app khác liên tục.
        private (DateTime stamp, long length, string? model) _cache;
        private bool _cacheValid;

        public ClaudeSettingsFile(string path) => _path = path;

        // null = chưa đặt model (hoặc không đọc được file).
        public string? ReadModel()
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && _cacheValid
                    && _cache.stamp == info.LastWriteTimeUtc && _cache.length == info.Length)
                    return _cache.model;

                string? model = ReadModelUncached();
                if (info.Exists)
                {
                    _cache = (info.LastWriteTimeUtc, info.Length, model);
                    _cacheValid = true;
                }
                return model;
            }
            catch { return ReadModelUncached(); }
        }

        private string? ReadModelUncached()
        {
            var root = TryLoad();
            return root != null && root.TryGetPropertyValue("model", out var node) && node is JsonValue v
                && v.TryGetValue<string>(out var model) ? model : null;
        }

        // model == null nghĩa là xoá khoá "model". Trả về false nếu không ghi được.
        public bool WriteModel(string? model)
        {
            var root = TryLoad();
            if (root == null) return false;

            try
            {
                BackupOnce();
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

                if (model == null) root.Remove("model");
                else root["model"] = model;

                string temp = _path + ".tracker.tmp";
                File.WriteAllText(temp, root.ToJsonString(WriteOptions));
                File.Move(temp, _path, overwrite: true);
                _cacheValid = false;   // ta vừa ghi: cache cũ không còn đúng
                return true;
            }
            catch
            {
                return false;
            }
        }

        private JsonObject? TryLoad()
        {
            try
            {
                // File chưa tồn tại → trả object rỗng để WriteModel() có thể tạo mới.
                if (!File.Exists(_path)) return new JsonObject();
                return JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            }
            catch
            {
                return null; // JSON lỗi: không ghi đè
            }
        }

        private string BackupPath => _path + ".tracker.bak";

        private void BackupOnce()
        {
            if (!File.Exists(_path)) return; // không backup nếu chưa có file
            if (!File.Exists(BackupPath)) File.Copy(_path, BackupPath);
        }

        // Tool đã trả model về như cũ thì bản sao lưu không còn lý do tồn tại —
        // trước đây .tracker.bak nằm lại trong ~/.claude vĩnh viễn.
        public void DiscardBackup()
        {
            try { File.Delete(BackupPath); } catch { /* còn bị khoá: để lần sau */ }
        }
    }
}
