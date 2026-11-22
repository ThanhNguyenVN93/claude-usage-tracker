using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ClaudeUsageTracker.Services
{
    public record McpInspectionResult(int DesktopCount, int CliCount, List<string> ServerNames)
    {
        public int TotalCount => DesktopCount + CliCount;
        public int EstimatedTokensPerTurn => TotalCount * 1800;
    }

    public static class TokenToolkitService
    {
        // Đọc danh sách MCP server từ Claude Desktop và Claude Code CLI để ước lượng lượng token "thuế" mỗi lượt chat.
        public static McpInspectionResult InspectMcpServers()
        {
            var names = new List<string>();
            int desktop = CountMcpInFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"),
                "Desktop", names);
            int cli = CountMcpInFile(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json"),
                "CLI", names);
            return new McpInspectionResult(desktop, cli, names);
        }

        private static int CountMcpInFile(string path, string sourceTag, List<string> names)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                int count = 0;

                if (doc.RootElement.TryGetProperty("mcpServers", out var mcp) && mcp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in mcp.EnumerateObject())
                    {
                        count++;
                        names.Add($"{prop.Name} ({sourceTag})");
                    }
                }

                // Claude Code CLI cũng lưu mcpServers riêng cho từng thư mục trong "projects"
                if (doc.RootElement.TryGetProperty("projects", out var projs) && projs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var proj in projs.EnumerateObject())
                    {
                        if (proj.Value.ValueKind == JsonValueKind.Object
                            && proj.Value.TryGetProperty("mcpServers", out var pMcp)
                            && pMcp.ValueKind == JsonValueKind.Object)
                        {
                            string dirName = Path.GetFileName(proj.Name.TrimEnd('\\', '/'));
                            foreach (var prop in pMcp.EnumerateObject())
                            {
                                count++;
                                names.Add($"{prop.Name} ({sourceTag}: {dirName})");
                            }
                        }
                    }
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        // Kiểm tra CLAUDE.md và .claudeignore trong thư mục dự án.
        public static (int ClaudeMdLines, bool HasClaudeIgnore, string Summary) AuditProject(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return (0, false, "Thư mục không tồn tại.");

            int lines = 0;
            string claudeMd = Path.Combine(folderPath, "CLAUDE.md");
            if (File.Exists(claudeMd))
            {
                try { lines = File.ReadAllLines(claudeMd).Length; } catch { }
            }

            bool hasIgnore = File.Exists(Path.Combine(folderPath, ".claudeignore"));
            var notes = new List<string>();
            if (lines > 300)
                notes.Add($"⚠ CLAUDE.md khá dài ({lines} dòng) — dễ tốn context.");
            else if (lines > 0)
                notes.Add($"✔ CLAUDE.md gọn gàng ({lines} dòng).");
            else
                notes.Add("ℹ Không có CLAUDE.md.");

            notes.Add(hasIgnore
                ? "✔ Đã có .claudeignore."
                : "⚠ Chưa có .claudeignore (Claude có thể đọc nhầm node_modules / bin / obj!).");

            return (lines, hasIgnore, string.Join(Environment.NewLine, notes));
        }

        public static bool CreateDefaultClaudeIgnore(string folderPath)
        {
            try
            {
                string target = Path.Combine(folderPath, ".claudeignore");
                if (File.Exists(target)) return true;

                string content =
@"# Tự động tạo bởi Claude Usage Tracker để tránh tốn token vào file build/rác
node_modules/
bin/
obj/
dist/
build/
out/
.git/
*.log
*.zip
*.exe
*.dll
";
                File.WriteAllText(target, content);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Nén ảnh đang có trong Clipboard về chiều ngang tối đa 1280px để tiết kiệm 50-70% Vision token.
        public static (bool Ok, string Message) CompressClipboardImage(int maxWidth = 1280)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage())
                    return (false, "Clipboard hiện không chứa hình ảnh nào.");

                BitmapSource? src = System.Windows.Clipboard.GetImage();
                if (src == null) return (false, "Không đọc được ảnh từ Clipboard.");

                if (src.PixelWidth <= maxWidth)
                    return (false, $"Ảnh hiện tại ({src.PixelWidth}×{src.PixelHeight}) đã đủ nhỏ, không cần nén.");

                double scale = (double)maxWidth / src.PixelWidth;
                int newW = maxWidth;
                int newH = Math.Max(1, (int)Math.Round(src.PixelHeight * scale));

                var transformed = new TransformedBitmap(src, new System.Windows.Media.ScaleTransform(scale, scale));
                // Tạo bản sao pixel độc lập (WriteableBitmap) trước khi Clipboard.SetImage xoá dữ liệu clipboard cũ
                var independent = new WriteableBitmap(transformed);
                independent.Freeze();
                System.Windows.Clipboard.SetImage(independent);

                return (true, $"Đã nén ảnh từ {src.PixelWidth}×{src.PixelHeight} xuống {newW}×{newH} (tiết kiệm ~{(1 - scale * scale) * 100:0}% Vision token).");
            }
            catch (Exception ex)
            {
                return (false, $"Không nén được ảnh: {ex.Message}");
            }
        }
    }
}
