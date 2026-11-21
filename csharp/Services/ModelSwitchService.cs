using System;
using System.IO;
using System.Text.Json;
using ClaudeUsageTracker.Models;

namespace ClaudeUsageTracker.Services
{
    // Điều phối: đọc quota -> ModelSwitchEngine -> ghi model mặc định cho phiên MỚI.
    // Không đổi được model của phiên đang chạy (phải dùng /model trong phiên đó).
    public class ModelSwitchService
    {
        private readonly AppConfig _config;
        private readonly ClaudeSettingsFile _settings;
        private readonly string _statePath;
        private ModelSwitchState _state;

        public ModelSwitchService(AppConfig config, ClaudeSettingsFile? settings = null, string? statePath = null)
        {
            _config = config;
            _settings = settings ?? new ClaudeSettingsFile(TrackerPaths.ClaudeSettings);
            _statePath = statePath ?? TrackerPaths.ModelSwitch;
            _state = LoadState();
        }

        public Alert? Evaluate(UsageInfo info)
        {
            if (info.IsDemo || info.IsStale) return null;

            // Tắt tính năng khi đang áp dụng => trả lại model cũ để không để lại dấu vết.
            if (!_config.EnableAutoModel)
                return _state.Applied != null ? Restore("Đã tắt tính năng tự đổi model") : null;

            double quota = Math.Max(info.PercentUsed, info.WeeklyPercent);
            var options = new ModelSwitchOptions(_config.AutoModelSonnetAt, _config.AutoModelHaikuAt);
            var decision = ModelSwitchEngine.Decide(quota, options, _state, _settings.ReadModel());

            switch (decision.Action)
            {
                case ModelAction.Apply:
                    return Apply(decision.Target!, quota);
                case ModelAction.Restore:
                    return Restore("Quota đã hạ");
                case ModelAction.Forget:
                    _state = new ModelSwitchState();
                    SaveState();
                    return null;
                default:
                    return null;
            }
        }

        private Alert? Apply(string target, double quota)
        {
            string? current = _settings.ReadModel();
            string? original = _state.Applied != null ? _state.Original : current;

            if (!_settings.WriteModel(target)) return null;

            _state = new ModelSwitchState { Original = original, Applied = target, At = DateTime.Now };
            SaveState();
            return new Alert(
                $"Đã đặt model mặc định: {target}",
                $"Quota {quota:0}%. Chỉ áp dụng cho phiên MỚI — phiên đang chạy hãy dùng /model {target}.",
                AlertLevel.Info);
        }

        private Alert? Restore(string reason)
        {
            // Chỉ khôi phục nếu model trong file vẫn đúng là giá trị tool đã đặt.
            if (!string.Equals(_settings.ReadModel(), _state.Applied, StringComparison.OrdinalIgnoreCase))
            {
                _state = new ModelSwitchState();
                SaveState();
                return null;
            }

            string? original = _state.Original;
            if (!_settings.WriteModel(original)) return null;

            _state = new ModelSwitchState();
            SaveState();
            return new Alert(
                "Đã khôi phục model mặc định",
                $"{reason}. Model mặc định cho phiên mới: {original ?? "(mặc định của Claude Code)"}.",
                AlertLevel.Info);
        }

        private ModelSwitchState LoadState()
        {
            try
            {
                if (File.Exists(_statePath))
                    return JsonSerializer.Deserialize<ModelSwitchState>(File.ReadAllText(_statePath)) ?? new ModelSwitchState();
            }
            catch { /* file hỏng: bắt đầu lại */ }
            return new ModelSwitchState();
        }

        private void SaveState()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
            }
            catch { /* không lưu được thì lần sau tự suy ra lại */ }
        }
    }
}
