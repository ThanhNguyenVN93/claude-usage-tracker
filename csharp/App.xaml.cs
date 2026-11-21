using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using ClaudeUsageTracker.Models;
using ClaudeUsageTracker.Services;
using ClaudeUsageTracker.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ClaudeUsageTracker
{
    public partial class App : Application
    {
        private NotifyIcon? _notifyIcon;
        private AppConfig _config = null!;
        private ClaudeUsageService _usageService = null!;
        private ClaudeApiSyncService _apiSync = null!;
        private InsightsService _insights = null!;
        private ModelSwitchService _modelSwitch = null!;
        private ProcessWatcherService _watcherService = null!;
        private UsageFileWatcherService _fileWatcherService = null!;
        private ForegroundAppWatcherService _foregroundWatcher = null!;
        private MiniHudWindow _hud = null!;
        private StatsWindow? _statsWindow;
        private UsageInfo? _lastUsage;
        private System.Windows.Threading.DispatcherTimer? _tick;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;
        private UpdateService _updater = null!;
        private UpdateState _updateState = null!;
        private ReleaseInfo? _availableUpdate;
        private ToolStripMenuItem? _updateMenuItem;
        private bool _balloonIsUpdate;
        private string? _lastIconHex;
        private GlobalHotkeyService? _hotkey;
        private ToolStripMenuItem? _hudMenuItem;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Lưới an toàn: lỗi giao diện/async không lường trước chỉ được ghi log chứ không giết cả tool nền.
            DispatcherUnhandledException += (s, ev) =>
            {
                ErrorLog.Write("DispatcherUnhandledException", ev.Exception);
                ev.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
                ErrorLog.Write("AppDomain.UnhandledException", ev.ExceptionObject as Exception ?? new Exception(ev.ExceptionObject?.ToString()));
            TaskScheduler.UnobservedTaskException += (s, ev) =>
            {
                ErrorLog.Write("UnobservedTaskException", ev.Exception);
                ev.SetObserved();
            };

            _config = ConfigService.Load();
            _usageService = new ClaudeUsageService();
            _apiSync = new ClaudeApiSyncService();
            _insights = new InsightsService(new HistoryService(), _config);
            _insights.PruneHistory();
            _modelSwitch = new ModelSwitchService(_config);
            _updater = new UpdateService(CurrentVersion());
            _updateState = UpdateState.Load();
            if (Environment.ProcessPath is string selfPath) UpdateLogic.CleanupOld(selfPath);
            _watcherService = new ProcessWatcherService(_config.CheckIntervalSeconds);
            _fileWatcherService = new UsageFileWatcherService();
            _foregroundWatcher = new ForegroundAppWatcherService();

            SetupNotifyIcon();

            // Sync quota từ Claude.ai API ngay khi khởi động (nếu có sessionKey),
            // rồi chạy ngầm định kỳ theo ApiSyncIntervalSeconds.
            await _apiSync.SyncOnceAsync(_config);
            _apiSync.StartBackground(_config);

            _lastUsage = await FetchUsageAsync();
            _hud = new MiniHudWindow(_lastUsage, FetchUsageAsync, OpenStats, _config);
            // Chưa gọi Show() — ForegroundAppWatcherService hoặc click tray sẽ quyết định khi nào hiện.
            UpdateTrayTooltip(_lastUsage);

            _hotkey = new GlobalHotkeyService(ToggleHud);
            try { _hotkey.Register(_config); }
            catch { /* hotkey bị app khác chiếm: bỏ qua, không crash */ }

            _watcherService.OnClaudeLaunched += OnClaudeLaunched;
            _watcherService.Start();

            _fileWatcherService.OnUsageFileChanged += OnUsageFileChanged;
            _fileWatcherService.Start();

            _foregroundWatcher.ForegroundAllowedChanged += OnForegroundAllowedChanged;
            _foregroundWatcher.Start();

            // Làm mới định kỳ để tuổi dữ liệu, cảnh báo "cũ" và ước tính tự cập nhật
            // ngay cả khi file nguồn ngừng đổi (watcher chỉ chạy khi file thay đổi).
            _tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _tick.Tick += async (s, ev) => await RefreshUsageAsync();
            _tick.Start();

            // Kiểm tra bản mới: lúc khởi động (sau 20 giây) rồi mỗi 6 giờ. Chỉ thông báo; tải về phải hỏi.
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += async (s, ev) => await CheckForUpdateAsync(manual: false);
            _updateTimer.Start();
            _ = CheckForUpdateAsync(manual: false, delay: TimeSpan.FromSeconds(20));

            // `ClaudeUsageTracker.exe --update`: kiểm tra ngay và hỏi cập nhật (dùng được từ script).
            if (e.Args.Contains("--update")) _ = RunUpdateFromCommandLineAsync();
        }

        // Đọc usage, gắn dự báo/mức cảnh báo, và bắn cảnh báo mới (nếu có).
        private async Task<UsageInfo> FetchUsageAsync()
        {
            var info = await _usageService.GetUsageAsync(_config);
            var alerts = _insights.Process(info);
            var switched = _modelSwitch.Evaluate(info);
            if (switched != null) alerts.Add(switched);
            if (alerts.Count > 0) NotifyAlerts(alerts);
            return info;
        }

        private void NotifyAlerts(List<Alert> alerts)
        {
            if (_notifyIcon == null) return;

            var level = AlertEngine.MaxLevel(alerts);
            string title = alerts.Count == 1 ? alerts[0].Title : $"{alerts.Count} cảnh báo usage";
            string message = string.Join("\n", alerts.Select(a => alerts.Count == 1 ? a.Message : $"• {a.Title}"));

            _notifyIcon.ShowBalloonTip(
                8000, title, message,
                level == AlertLevel.Critical ? ToolTipIcon.Error
                : level == AlertLevel.Warn ? ToolTipIcon.Warning
                : ToolTipIcon.Info);
        }

        private void OnForegroundAllowedChanged(bool allowed)
        {
            Dispatcher.Invoke(async () =>
            {
                if (allowed)
                {
                    _lastUsage = await FetchUsageAsync();
                    _hud.ApplyData(_lastUsage);
                    UpdateTrayTooltip(_lastUsage);
                    _hud.Show();
                }
                else
                {
                    _hud.Hide();
                }
            });
        }

        private void OnUsageFileChanged()
        {
            Dispatcher.Invoke(async () => await RefreshUsageAsync());
        }

        private void SetupNotifyIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateClaudeIcon(Color.FromArgb(217, 119, 6)),
                Text = "Claude Usage Tracker (Đang theo dõi)",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            _hudMenuItem = new ToolStripMenuItem($"📊 Bảng Usage (HUD)   [{FormatHotkey(_config.HotkeyModifiers, _config.HotkeyVirtualKey)}]");
            _hudMenuItem.Click += (s, e) => ToggleHud();
            contextMenu.Items.Add(_hudMenuItem);
            contextMenu.Items.Add("📈 Thống kê", null, (s, e) => OpenStats());
            contextMenu.Items.Add("🔄 Làm mới Usage", null, async (s, e) => await RefreshUsageAsync(showFeedback: true));
            _updateMenuItem = new ToolStripMenuItem("⬆ Cập nhật", null, async (s, e) => await PromptAndApplyUpdateAsync())
            {
                Visible = false,
                Font = new Font(System.Drawing.SystemFonts.MenuFont ?? System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold)
            };
            contextMenu.Items.Add(_updateMenuItem);
            contextMenu.Items.Add("🔍 Kiểm tra bản mới", null, async (s, e) => await CheckForUpdateAsync(manual: true));
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("⚙ Cài đặt", null, (s, e) => OpenSettings());
            contextMenu.Items.Add("❌ Thoát", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.BalloonTipClicked += async (s, e) =>
            {
                if (_balloonIsUpdate && _availableUpdate != null) await PromptAndApplyUpdateAsync();
            };
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ToggleHud();
            };
        }

        private Icon CreateClaudeIcon(Color accentColor)
        {
            try
            {
                using var bmp = new Bitmap(32, 32);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                // Dark background circle
                using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 30, 46));
                g.FillEllipse(bgBrush, 1, 1, 30, 30);

                // Gauge arc 270° from bottom-left, color = usage level
                using var arcPen = new Pen(accentColor, 3.5f);
                arcPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                arcPen.EndCap   = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(arcPen, 5, 5, 22, 22, -225f, 270f);

                // "%" in Catppuccin text color
                using var textBrush = new SolidBrush(Color.FromArgb(220, 205, 214, 244));
                using var font = new Font("Segoe UI", 10f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("%", font, textBrush, new RectangleF(0, 0, 32, 32), sf);

                var hIcon = bmp.GetHicon();
                try
                {
                    // Clone rồi huỷ HICON gốc, nếu không mỗi lần đổi icon sẽ rò rỉ một GDI handle.
                    using var tmp = Icon.FromHandle(hIcon);
                    return (Icon)tmp.Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
            catch
            {
                return SystemIcons.Application;
            }
        }

        private static Color ColorFromHex(string hex) =>
            (Color)System.Drawing.ColorTranslator.FromHtml(hex);

        private void OnClaudeLaunched()
        {
            Dispatcher.Invoke(async () =>
            {
                _lastUsage = await FetchUsageAsync();
                UpdateTrayTooltip(_lastUsage);

                // Step 1: Toast Notification 5s
                var toast = new ToastNotificationWindow(_lastUsage, () =>
                {
                    // Step 2: Show Mini HUD Window
                    _hud.ApplyData(_lastUsage);
                    _hud.Show();
                    _hud.Activate();
                });
                toast.Show();
            });
        }

        private static string FormatHotkey(uint mod, uint vk)
        {
            if (mod == 0 || vk == 0) return "?";
            var parts = new System.Collections.Generic.List<string>();
            if ((mod & 0x0002) != 0) parts.Add("Ctrl");
            if ((mod & 0x0004) != 0) parts.Add("Shift");
            if ((mod & 0x0001) != 0) parts.Add("Alt");
            if ((mod & 0x0008) != 0) parts.Add("Win");
            parts.Add(System.Windows.Input.KeyInterop.KeyFromVirtualKey((int)vk).ToString());
            return string.Join("+", parts);
        }

        private void ToggleHud()
        {
            Dispatcher.Invoke(async () =>
            {
                if (_hud.IsVisible)
                {
                    _hud.Hide();
                    return;
                }

                _lastUsage ??= await FetchUsageAsync();
                _hud.ApplyData(_lastUsage);
                _hud.Show();
                _hud.Activate();
            });
        }

        private void OpenStats()
        {
            Dispatcher.Invoke(async () =>
            {
                if (_statsWindow != null)
                {
                    _statsWindow.Activate();
                    return;
                }

                _lastUsage ??= await FetchUsageAsync();
                _statsWindow = new StatsWindow(_insights.BuildSnapshot(_lastUsage), _lastUsage);
                _statsWindow.Closed += (s, e) => _statsWindow = null;
                _statsWindow.Show();
                _statsWindow.Activate();
            });
        }

        private async Task RefreshUsageAsync(bool showFeedback = false)
        {
            var previousSnapshot = _lastUsage?.LastRefreshedAt;

            _lastUsage = await FetchUsageAsync();
            UpdateTrayTooltip(_lastUsage);
            if (_hud.IsVisible)
            {
                _hud.ApplyData(_lastUsage);
            }

            if (showFeedback && _notifyIcon != null)
            {
                // Làm mới chỉ đọc lại file; nói rõ có ảnh chụp mới hay chưa.
                bool gotNewer = previousSnapshot == null || _lastUsage.LastRefreshedAt > previousSnapshot;
                string title = gotNewer
                    ? $"Có số mới · {_lastUsage.PercentUsed:0}%"
                    : $"Chưa có số mới · vẫn {_lastUsage.PercentUsed:0}%";
                _notifyIcon.ShowBalloonTip(2500, title, _lastUsage.Note, ToolTipIcon.Info);
            }
        }

        private void UpdateTrayTooltip(UsageInfo info)
        {
            if (_notifyIcon != null)
            {
                string tip = $"Claude: {info.PercentUsed:0}% | {info.MessagesRemaining}";
                if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
                _notifyIcon.Text = tip;

                // Đẩy màu icon lên một mức khi có cảnh báo đang tồn tại (dự báo cạn quota, ngữ cảnh phình to).
                string hex = info.StatusColorHex;
                if (info.ConditionLevel == AlertLevel.Critical) hex = "#EF4444";
                else if (info.ConditionLevel == AlertLevel.Warn && hex == "#10B981") hex = "#F59E0B";

                if (hex != _lastIconHex && _notifyIcon.ContextMenuStrip?.Visible != true)
                {
                    _lastIconHex = hex;
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = CreateClaudeIcon(ColorFromHex(hex));
                    oldIcon?.Dispose();
                }
            }
        }

        private static Version CurrentVersion() =>
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

        private void ShowBalloon(string title, string text, ToolTipIcon icon, bool isUpdate = false)
        {
            if (_notifyIcon == null) return;
            _balloonIsUpdate = isUpdate;
            _notifyIcon.ShowBalloonTip(6000, title, text, icon);
        }

        private async Task CheckForUpdateAsync(bool manual, TimeSpan? delay = null)
        {
            if (delay.HasValue) await Task.Delay(delay.Value);

            if (!manual)
            {
                if (!_config.EnableUpdateCheck) return;
                if (DateTime.UtcNow - _updateState.LastCheckUtc < TimeSpan.FromHours(6)) return;
            }

            try
            {
                var release = await _updater.CheckAsync();
                _updateState.LastCheckUtc = DateTime.UtcNow;
                _availableUpdate = release;

                if (_updateMenuItem != null)
                {
                    _updateMenuItem.Visible = release != null;
                    if (release != null) _updateMenuItem.Text = $"⬆ Cập nhật lên {release.Tag}";
                }

                if (release != null)
                {
                    if (manual || _updateState.NotifiedTag != release.Tag)
                    {
                        _updateState.NotifiedTag = release.Tag;
                        ShowBalloon($"Có bản mới {release.Tag}",
                            "Bấm vào đây (hoặc menu tray › Cập nhật) để xem và cài đặt. Sẽ hỏi lại trước khi tải.",
                            ToolTipIcon.Info, isUpdate: true);
                        if (manual)
                        {
                            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\n{release.Notes}";
                            var ans = MessageBox.Show(
                                $"Có bản mới {release.Tag} (đang dùng v{UpdateLogic.Normalize(CurrentVersion())}).{notes}\n\nMở trang tải về trên GitHub?",
                                "Kiểm tra bản mới", MessageBoxButton.YesNo, MessageBoxImage.Information);
                            if (ans == MessageBoxResult.Yes)
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                        }
                    }
                }
                else if (manual)
                {
                    ShowBalloon("Đang dùng bản mới nhất", $"Phiên bản hiện tại: v{UpdateLogic.Normalize(CurrentVersion())}", ToolTipIcon.Info);
                    MessageBox.Show($"Đang dùng bản mới nhất.\nPhiên bản hiện tại: v{UpdateLogic.Normalize(CurrentVersion())}",
                        "Kiểm tra bản mới", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _updateState.Save();
            }
            catch (Exception ex)
            {
                if (manual)
                {
                    ShowBalloon("Không kiểm tra được bản mới", ex.Message, ToolTipIcon.Warning);
                    MessageBox.Show($"Không kiểm tra được bản mới:\n{ex.Message}", "Kiểm tra bản mới", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private async Task RunUpdateFromCommandLineAsync()
        {
            await CheckForUpdateAsync(manual: true);
            if (_availableUpdate != null) await PromptAndApplyUpdateAsync();
        }

        private async Task PromptAndApplyUpdateAsync()
        {
            var release = _availableUpdate;
            if (release == null) return;

            string? exePath = Environment.ProcessPath;
            if (exePath == null || !string.Equals(Path.GetFileName(exePath), UpdateLogic.ExeName, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show($"Chỉ tự cập nhật được khi chạy từ {UpdateLogic.ExeName}.\nHãy tải bản mới tại:\n{release.HtmlUrl}",
                    "Claude Usage Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string notes = string.IsNullOrWhiteSpace(release.Notes) ? string.Empty : $"\n\n{release.Notes}";
            var answer = MessageBox.Show(
                $"Có bản mới {release.Tag} (đang dùng v{UpdateLogic.Normalize(CurrentVersion())}).{notes}\n\n" +
                "Tải từ GitHub, kiểm tra SHA-256 rồi khởi động lại tool?",
                "Cập nhật Claude Usage Tracker", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            ShowBalloon("Đang tải bản mới", $"{release.Tag} — vui lòng đợi…", ToolTipIcon.Info);

            string stagedExe;
            try
            {
                stagedExe = await _updater.DownloadAndStageAsync(release);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không tải/kiểm tra được bản mới:\n{ex.Message}\n\nBản đang chạy không bị thay đổi.",
                    "Cập nhật thất bại", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // DLL của self-contained app bị lock bởi process đang chạy — không thể overwrite in-place.
            // Thay vào đó: copy toàn bộ file sang folder mới, restart từ đó.
            string stagedDir = Path.GetDirectoryName(stagedExe)!;
            string installDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeUsageTracker",
                $"app-{release.Tag}");
            try
            {
                if (Directory.Exists(installDir)) Directory.Delete(installDir, true);
                Directory.CreateDirectory(installDir);
                foreach (var file in Directory.EnumerateFiles(stagedDir))
                    File.Copy(file, Path.Combine(installDir, Path.GetFileName(file)), overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không copy được bản mới:\n{ex.Message}\n\nBản đang chạy không bị thay đổi.",
                    "Cập nhật thất bại", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try { Directory.Delete(Path.GetDirectoryName(stagedDir)!, true); } catch { /* dọn tạm */ }

            string newExe = Path.Combine(installDir, UpdateLogic.ExeName);
            Process.Start(new ProcessStartInfo(newExe) { UseShellExecute = true });
            ExitApplication();
        }

        private void OpenSettings()
        {
            var settings = new SettingsWindow(_config);
            if (settings.ShowDialog() == true)
            {
                ConfigService.Save(_config);
                try { _hotkey?.Reregister(_config.HotkeyModifiers, _config.HotkeyVirtualKey); } catch { }
                if (_hudMenuItem != null)
                    _hudMenuItem.Text = $"📊 Bảng Usage (HUD)   [{FormatHotkey(_config.HotkeyModifiers, _config.HotkeyVirtualKey)}]";
                // Nếu vừa cấu hình SessionKey, sync API ngay lập tức
                if (!string.IsNullOrWhiteSpace(_config.SessionKey))
                    _ = _apiSync.SyncOnceAsync(_config);
                _ = RefreshUsageAsync();
            }
        }

        private void ExitApplication()
        {
            _tick?.Stop();
            _updateTimer?.Stop();
            _hotkey?.Dispose();
            _apiSync?.Dispose();
            _watcherService.Dispose();
            _fileWatcherService.Dispose();
            _foregroundWatcher.Dispose();
            _statsWindow?.Close();
            if (_hud != null)
            {
                _hud.AllowRealClose = true;
                _hud.Close();
            }
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
            }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ExitApplication();
            base.OnExit(e);
        }
    }
}
