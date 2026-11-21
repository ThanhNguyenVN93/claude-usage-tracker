using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using ClaudeUsageTracker.Models;
using ClaudeUsageTracker.Services;
using ClaudeUsageTracker.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Microsoft.Win32;

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
        private string? _lastIconHex;
        private int _lastIconPct = -2;
        private GlobalHotkeyService? _hotkey;
        private GlobalHotkeyService? _hotkeyStats;
        private ToolStripMenuItem? _hudMenuItem;
        private ToolStripMenuItem? _snoozeMenuItem;
        private ToolStripMenuItem? _syncPauseMenuItem;
        private CountdownWindow? _countdownWindow;
        private DateTime _snoozeUntil;
        private DateTime _lastClaudeSeenAt;
        private readonly HttpClient _webhookHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private static void EnsureStartupEntry()
        {
            const string name = "ClaudeUsageTracker";
            string? self = Environment.ProcessPath;
            if (self == null) return;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;
                string expected = $"\"{self}\"";
                if (key.GetValue(name) as string != expected)
                    key.SetValue(name, expected);
            }
            catch { /* không crash nếu registry bị deny */ }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            EnsureStartupEntry();

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
            // Người dùng có thể bấm "Thoát" trên tray trong lúc đang await sync (tối đa ~10s).
            if (_isExiting) return;
            _apiSync.StartBackground(_config, syncImmediately: false);

            _lastUsage = await FetchUsageAsync();
            if (_isExiting) return;
            _hud = new MiniHudWindow(_lastUsage, FetchUsageAsync, OpenStats, _config);
            // Chưa gọi Show() — ForegroundAppWatcherService hoặc click tray sẽ quyết định khi nào hiện.
            UpdateTrayTooltip(_lastUsage);

            _hotkey = new GlobalHotkeyService(ToggleHud);
            try { _hotkey.Register(_config); }
            catch { /* hotkey bị app khác chiếm: bỏ qua, không crash */ }

            _hotkeyStats = new GlobalHotkeyService(OpenStats, 0x9002);
            try { _hotkeyStats.Register(_config.StatsHotkeyModifiers, _config.StatsHotkeyVirtualKey); }
            catch { }

            _watcherService.OnClaudeLaunched += OnClaudeLaunched;
            _watcherService.Start();

            _fileWatcherService.OnUsageFileChanged += OnUsageFileChanged;
            _fileWatcherService.Start();

            _foregroundWatcher.ForegroundAllowedChanged += OnForegroundAllowedChanged;
            _foregroundWatcher.Start();

            // Làm mới định kỳ để tuổi dữ liệu, cảnh báo "cũ" và ước tính tự cập nhật
            // ngay cả khi file nguồn ngừng đổi (watcher chỉ chạy khi file thay đổi).
            _tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _tick.Tick += async (s, ev) =>
            {
                // Reset snooze menu text sau khi hết hạn
                if (_snoozeUntil != default && DateTime.Now >= _snoozeUntil && _snoozeMenuItem != null
                    && _snoozeMenuItem.Text != "🔕 Tắt thông báo 1 giờ")
                {
                    _snoozeMenuItem.Text = "🔕 Tắt thông báo 1 giờ";
                }
                await RefreshUsageAsync();
            };
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
            if (DateTime.Now < _snoozeUntil) return;

            var level = AlertEngine.MaxLevel(alerts);
            string title = alerts.Count == 1 ? alerts[0].Title : $"{alerts.Count} cảnh báo usage";
            string message = string.Join("\n", alerts.Select(a => alerts.Count == 1 ? a.Message : $"• {a.Title}"));

            string icon = level == AlertLevel.Critical ? "🔴" : level == AlertLevel.Warn ? "🟡" : "🔔";
            ToastWindow.ShowToast(title, message, icon, onAction: ShowHudFromToast);

            if (!string.IsNullOrWhiteSpace(_config.WebhookUrl))
                _ = PostWebhookAsync(_config.WebhookUrl, title, message);
        }

        // Click vào toast: _hud/_lastUsage có thể còn null nếu toast bắn ra trong lúc khởi động.
        private void ShowHudFromToast()
        {
            if (_hud == null || _lastUsage == null) return;
            _hud.ApplyData(_lastUsage);
            _hud.Show();
            _hud.Activate();
        }

        // Dispatcher.Invoke(async () => ...) chọn overload Func<Task> và vứt Task đi → exception bị nuốt im lặng
        // (chỉ lộ ra ở UnobservedTaskException khi GC chạy). Helper này chạy trên UI thread và log lỗi ngay.
        private void RunOnUi(Func<Task> work, string context)
        {
            if (_isExiting) return;
            Dispatcher.InvokeAsync(async () =>
            {
                if (_isExiting) return;
                try { await work(); }
                catch (Exception ex) { ErrorLog.Write(context, ex); }
            });
        }

        private void OnForegroundAllowedChanged(bool allowed)
        {
            RunOnUi(async () =>
            {
                if (_hud == null) return;
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
            }, nameof(OnForegroundAllowedChanged));
        }

        private void OnUsageFileChanged()
        {
            RunOnUi(() => RefreshUsageAsync(), nameof(OnUsageFileChanged));
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
            _snoozeMenuItem = new ToolStripMenuItem("🔕 Tắt thông báo 1 giờ");
            _snoozeMenuItem.Click += (s, e) => SnoozeAlerts();
            contextMenu.Items.Add(_snoozeMenuItem);
            var countdownItem = new ToolStripMenuItem("⏱ Đếm ngược reset");
            countdownItem.Click += (s, e) => ToggleCountdown();
            contextMenu.Items.Add(countdownItem);
            _syncPauseMenuItem = new ToolStripMenuItem("⏸ Tạm dừng sync");
            _syncPauseMenuItem.Click += (s, e) => ToggleSyncPause();
            contextMenu.Items.Add(_syncPauseMenuItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add("⚙ Cài đặt", null, (s, e) => OpenSettings());
            contextMenu.Items.Add("🐛 Báo lỗi", null, (s, e) =>
                Process.Start(new ProcessStartInfo("https://github.com/ThanhNguyenVN93/claude-usage-tracker/issues") { UseShellExecute = true }));
            contextMenu.Items.Add("❌ Thoát", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ToggleHud();
            };
        }

        private Icon CreateClaudeIcon(Color accentColor, int pct = -1)
        {
            try
            {
                using var bmp = new Bitmap(32, 32);
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 30, 46));
                g.FillEllipse(bgBrush, 1, 1, 30, 30);

                using var arcPen = new Pen(accentColor, 3.5f);
                arcPen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                arcPen.EndCap   = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(arcPen, 5, 5, 22, 22, -225f, 270f);

                using var textBrush = new SolidBrush(Color.FromArgb(220, 205, 214, 244));
                string text;
                float fontSize;
                if (pct < 0 || pct > 999)
                {
                    text = "%";
                    fontSize = 10f;
                }
                else if (pct >= 100)
                {
                    text = $"{pct}";
                    fontSize = 7f;
                }
                else if (pct >= 10)
                {
                    text = $"{pct}";
                    fontSize = 10f;
                }
                else
                {
                    text = $"{pct}";
                    fontSize = 12f;
                }

                using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(text, font, textBrush, new RectangleF(0, 0, 32, 32), sf);

                var hIcon = bmp.GetHicon();
                try
                {
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
                // Clone: UpdateTrayTooltip sẽ Dispose() icon cũ — không được dispose instance dùng chung của SystemIcons.
                return (Icon)SystemIcons.Application.Clone();
            }
        }

        private static Color ColorFromHex(string hex) =>
            (Color)System.Drawing.ColorTranslator.FromHtml(hex);

        private void OnClaudeLaunched()
        {
            RunOnUi(async () =>
            {
                _lastClaudeSeenAt = DateTime.Now;
                _apiSync.Paused = false;
                if (_syncPauseMenuItem != null) _syncPauseMenuItem.Text = "⏸ Tạm dừng sync";

                _lastUsage = await FetchUsageAsync();
                UpdateTrayTooltip(_lastUsage);

                ToastWindow.ShowToast(
                    $"Claude khởi động · {_lastUsage.PercentUsed:0}% quota",
                    _lastUsage.Note,
                    "🚀",
                    onAction: ShowHudFromToast);
            }, nameof(OnClaudeLaunched));
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
            RunOnUi(async () =>
            {
                // Tray icon đã hiện trước khi _hud được tạo (OnStartup còn đang await sync) → click sớm sẽ NRE.
                if (_hud == null) return;
                if (_hud.IsVisible)
                {
                    _hud.Hide();
                    return;
                }

                _lastUsage ??= await FetchUsageAsync();
                _hud.ApplyData(_lastUsage);
                _hud.Show();
                _hud.Activate();
            }, nameof(ToggleHud));
        }

        private void OpenStats()
        {
            RunOnUi(async () =>
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
            }, nameof(OpenStats));
        }

        private static bool IsClaudeProcessRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName("Claude");
                foreach (var p in procs) p.Dispose();
                return procs.Length > 0;
            }
            catch { return false; }
        }

        private async Task RefreshUsageAsync(bool showFeedback = false)
        {
            var previousSnapshot = _lastUsage?.LastRefreshedAt;

            // Auto-pause API sync khi không thấy Claude > 30 phút
            if (_lastClaudeSeenAt != default && !_apiSync.Paused)
            {
                // _lastClaudeSeenAt chỉ được cập nhật lúc Claude *khởi động* → nếu Claude mở liên tục > 30 phút
                // sync bị tạm dừng nhầm. Kiểm tra tiến trình còn chạy trước khi auto-pause.
                if ((DateTime.Now - _lastClaudeSeenAt).TotalMinutes > 30 && IsClaudeProcessRunning())
                    _lastClaudeSeenAt = DateTime.Now;

                if ((DateTime.Now - _lastClaudeSeenAt).TotalMinutes > 30)
                {
                    _apiSync.Paused = true;
                    if (_syncPauseMenuItem != null) _syncPauseMenuItem.Text = "▶ Tiếp tục sync";
                }
            }

            _lastUsage = await FetchUsageAsync();
            UpdateTrayTooltip(_lastUsage);
            if (_hud != null && _hud.IsVisible) _hud.ApplyData(_lastUsage);
            _countdownWindow?.ApplyData(_lastUsage);

            if (showFeedback)
            {
                bool gotNewer = previousSnapshot == null || _lastUsage.LastRefreshedAt > previousSnapshot;
                string title = gotNewer
                    ? $"Có số mới · {_lastUsage.PercentUsed:0}%"
                    : $"Chưa có số mới · vẫn {_lastUsage.PercentUsed:0}%";
                ToastWindow.ShowToast(title, _lastUsage.Note, gotNewer ? "🔄" : "ℹ️");
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

                int pct = (int)Math.Round(info.PercentUsed);
                if ((hex != _lastIconHex || pct != _lastIconPct) && _notifyIcon.ContextMenuStrip?.Visible != true)
                {
                    _lastIconHex = hex;
                    _lastIconPct = pct;
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = CreateClaudeIcon(ColorFromHex(hex), pct);
                    oldIcon?.Dispose();
                }
            }
        }

        private static Version CurrentVersion() =>
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

        private void ShowBalloon(string title, string text, ToolTipIcon icon)
        {
            if (_notifyIcon == null) return;
            string toastIcon = icon == ToolTipIcon.Error ? "🔴"
                             : icon == ToolTipIcon.Warning ? "🟡" : "ℹ️";
            ToastWindow.ShowToast(title, text, toastIcon);
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
                        if (manual)
                        {
                            // Manual: chỉ MessageBox (rõ hơn toast)
                            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\n{release.Notes}";
                            var ans = MessageBox.Show(
                                $"Có bản mới {release.Tag} (đang dùng v{UpdateLogic.Normalize(CurrentVersion())}).{notes}\n\nMở trang tải về trên GitHub?",
                                "Kiểm tra bản mới", MessageBoxButton.YesNo, MessageBoxImage.Information);
                            if (ans == MessageBoxResult.Yes)
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                        }
                        else
                        {
                            ShowBalloon($"Có bản mới {release.Tag}",
                                "Menu tray › Cập nhật để xem và cài đặt.",
                                ToolTipIcon.Info);
                        }
                    }
                }
                else if (manual)
                {
                    MessageBox.Show($"Đang dùng bản mới nhất.\nPhiên bản hiện tại: v{UpdateLogic.Normalize(CurrentVersion())}",
                        "Kiểm tra bản mới", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                _updateState.Save();
            }
            catch (Exception ex)
            {
                if (manual)
                    MessageBox.Show($"Không kiểm tra được bản mới:\n{ex.Message}", "Kiểm tra bản mới", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private SettingsWindow? _settingsWindow;
        private void OpenSettings()
        {
            // Menu tray vẫn bấm được khi dialog đang mở (WinForms message vẫn được pump) → tránh mở 2 dialog.
            if (_settingsWindow != null) { _settingsWindow.Activate(); return; }

            var settings = new SettingsWindow(_config);
            _settingsWindow = settings;
            bool? result;
            try { result = settings.ShowDialog(); }
            finally { _settingsWindow = null; }

            if (result == true)
            {
                ConfigService.Save(_config);
                bool hudOk = true, statsOk = true;
                try { hudOk = _hotkey?.Reregister(_config.HotkeyModifiers, _config.HotkeyVirtualKey) ?? true; } catch { hudOk = false; }
                try { statsOk = _hotkeyStats?.Reregister(_config.StatsHotkeyModifiers, _config.StatsHotkeyVirtualKey) ?? true; } catch { statsOk = false; }
                if (!hudOk || !statsOk)
                    ToastWindow.ShowToast("Không đăng ký được phím tắt",
                        "Tổ hợp phím đang bị ứng dụng khác chiếm. Hãy chọn tổ hợp khác trong Cài đặt.", "🟡");
                if (_hudMenuItem != null)
                    _hudMenuItem.Text = $"📊 Bảng Usage (HUD)   [{FormatHotkey(_config.HotkeyModifiers, _config.HotkeyVirtualKey)}]";
                // Khởi động lại vòng sync nền: lúc khởi động nếu chưa có SessionKey thì vòng lặp không bao giờ chạy,
                // và đổi ApiSyncIntervalSeconds cũng không có hiệu lực nếu không restart. StartBackground sync ngay 1 lần.
                _apiSync.StartBackground(_config);
                if (_config.ApiSyncIntervalSeconds <= 0 && !string.IsNullOrWhiteSpace(_config.SessionKey))
                    _ = _apiSync.SyncOnceAsync(_config);
                _ = RefreshUsageAsync();
            }
        }

        private void SnoozeAlerts()
        {
            _snoozeUntil = DateTime.Now.AddHours(1);
            if (_snoozeMenuItem != null) _snoozeMenuItem.Text = "🔕 Tắt thông báo (đến " + _snoozeUntil.ToString("HH:mm") + ")";
            ToastWindow.ShowToast("Tắt thông báo 1 giờ", $"Cảnh báo sẽ bị tạm dừng đến {_snoozeUntil:HH:mm}.", "🔕");
        }

        private void ToggleCountdown()
        {
            Dispatcher.Invoke(() =>
            {
                if (_countdownWindow != null && _countdownWindow.IsVisible)
                {
                    _countdownWindow.Hide();
                    return;
                }
                if (_countdownWindow == null)
                {
                    _countdownWindow = new CountdownWindow();
                    _countdownWindow.Closed += (s, e) => _countdownWindow = null;
                }
                _countdownWindow.ApplyData(_lastUsage);
                _countdownWindow.Show();
                _countdownWindow.Activate();
            });
        }

        private void ToggleSyncPause()
        {
            _apiSync.Paused = !_apiSync.Paused;
            if (_syncPauseMenuItem != null)
                _syncPauseMenuItem.Text = _apiSync.Paused ? "▶ Tiếp tục sync" : "⏸ Tạm dừng sync";
            if (!_apiSync.Paused)
            {
                _lastClaudeSeenAt = DateTime.Now;
                _ = _apiSync.SyncOnceAsync(_config);
            }
        }

        private async Task PostWebhookAsync(string url, string title, string message)
        {
            try
            {
                // Discord webhook: {"content": "..."} ; generic: same format works for most
                // Dùng JsonSerializer.Serialize để escape title/message an toàn (tránh " hay \ làm vỡ JSON)
                string combined = $"**{title}**\n{message}";
                string escaped = System.Text.Json.JsonSerializer.Serialize(combined);
                string payload = $"{{\"content\":{escaped}}}";
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                await _webhookHttp.PostAsync(url, content);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Webhook] POST failed: {ex.Message}");
            }
        }

        private bool _isExiting;
        private void ExitApplication()
        {
            if (_isExiting) return;
            _isExiting = true;
            _tick?.Stop();
            _updateTimer?.Stop();
            _hotkey?.Dispose();
            _hotkeyStats?.Dispose();
            _apiSync?.Dispose();
            _webhookHttp.Dispose();
            _watcherService.Dispose();
            _fileWatcherService.Dispose();
            _foregroundWatcher.Dispose();
            _statsWindow?.Close();
            _countdownWindow?.Close();
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
