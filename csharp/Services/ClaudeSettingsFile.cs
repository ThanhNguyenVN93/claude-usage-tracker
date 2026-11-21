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

        public ClaudeSettingsFile(string path) => _path = path;

        // null = chưa đặt model (hoặc không đọc được file).
        public string? ReadModel()
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

        private void BackupOnce()
        {
            if (!File.Exists(_path)) return; // không backup nếu chưa có file
            string backup = _path + ".tracker.bak";
            if (!File.Exists(backup)) File.Copy(_path, backup);
        }
    }
}
