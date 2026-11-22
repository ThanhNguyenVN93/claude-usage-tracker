using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
        private Font? _menuFont;
        private string? _lastIconHex;
        private int _lastIconPct = -2;
        private GlobalHotkeyService? _hotkey;
        private GlobalHotkeyService? _hotkeyStats;
        private GlobalHotkeyService? _hotkeyQuickHaiku;
        private GlobalHotkeyService? _hotkeyQuickCompact;
        private ToolStripMenuItem? _hudMenuItem;
        private ToolStripMenuItem? _snoozeMenuItem;
        private ToolStripMenuItem? _syncPauseMenuItem;
        private CountdownWindow? _countdownWindow;
        private DateTime _snoozeUntil;
        private DateTime _lastClaudeSeenAt;
        private readonly HttpClient _webhookHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // Hai instance = hai tray icon, hai vòng sync cùng ghi đè cli-usage.json / plan-usage-history.json
        // (SemaphoreSlim chỉ tuần tự hoá trong một process), và hotkey của instance thứ hai không đăng ký được.
        private static Mutex? _singleInstance;

        private static bool TryAcquireSingleInstance()
        {
            try
            {
                _singleInstance = new Mutex(true, @"Local\ClaudeUsageTracker.SingleInstance", out bool isFirst);
                if (isFirst) return true;
                _singleInstance.Dispose();
                _singleInstance = null;
                return false;
            }
            catch
            {
                // Không tạo được mutex thì cứ chạy — thà hai instance còn hơn không chạy được.
                return true;
            }
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (!TryAcquireSingleInstance())
            {
                MessageBox.Show(
                    "Claude Usage Tracker đã chạy rồi.\nXem icon ở khay hệ thống (bấm mũi tên ^ nếu bị ẩn).",
                    "Claude Usage Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
                _isExiting = true;   // chặn ExitApplication đụng vào service chưa khởi tạo
                Shutdown();
                return;
            }

            AutoStartService.SyncPathIfEnabled();

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
            _hud = new MiniHudWindow(_lastUsage, FetchUsageAsync, OpenStats, OpenSettings, _config, SendTerminalCommandAsync);
            // Chưa gọi Show() — ForegroundAppWatcherService hoặc click tray sẽ quyết định khi nào hiện.
            UpdateTrayTooltip(_lastUsage);

            _hotkey = new GlobalHotkeyService(ToggleHud);
            _hotkeyStats = new GlobalHotkeyService(OpenStats, 0x9002);

            _hotkeyQuickHaiku = new GlobalHotkeyService(() => RunOnUi(async () =>
            {
                var (ok, msg) = await SendTerminalCommandAsync("/model haiku");
                ToastWindow.ShowToast(ok ? "⚡ Đã gửi /model haiku" : "⚠ Không gửi được lệnh", msg, ok ? "⚡" : "⚠️");
            }, "HotkeyQuickHaiku"), 0x9003);

            _hotkeyQuickCompact = new GlobalHotkeyService(() => RunOnUi(async () =>
            {
                var (ok, msg) = await SendTerminalCommandAsync("/compact");
                ToastWindow.ShowToast(ok ? "🧹 Đã gửi /compact" : "⚠ Không gửi được lệnh", msg, ok ? "🧹" : "⚠️");
            }, "HotkeyQuickCompact"), 0x9004);

            RegisterAllHotkeys(showConflictToast: true);

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

            // Privacy Mode = đang họp/chia sẻ màn hình: tiếng "ting" cũng lộ chuyện như toast,
            // nên phải tắt luôn chứ không chỉ ẩn HUD/toast.
            if (_config.EnableSoundAlert && !_config.PrivacyMode && level == AlertLevel.Critical)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            }

            if (!_config.PrivacyMode)
            {
                string icon = level == AlertLevel.Critical ? "🔴" : level == AlertLevel.Warn ? "🟡" : "🔔";
                ToastWindow.ShowToast(title, message, icon, onAction: ShowHudFromToast);
            }

            if (IsValidWebhook(_config.WebhookUrl))
                _ = PostWebhookAsync(_config.WebhookUrl, title, message);
        }

        // Click vào toast: _hud/_lastUsage có thể còn null nếu toast bắn ra trong lúc khởi động.
        private void ShowHudFromToast()
        {
            if (_hud == null || _lastUsage == null) return;
            _hud.ApplyData(_lastUsage);
            _hud.ShowByUser();
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
                if (_isMenuOpen || _notifyIcon?.ContextMenuStrip?.Visible == true) return;
                if (allowed && !_config.PrivacyMode)
                {
                    _lastUsage = await FetchUsageAsync();
                    _hud.ApplyData(_lastUsage);
                    UpdateTrayTooltip(_lastUsage);
                    _hud.ShowFromWatcher();   // tôn trọng việc người dùng đã bấm ✕
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

        private bool _isMenuOpen;

        private void SetupNotifyIcon()
        {
            string verStr = UpdateLogic.Normalize(CurrentVersion()).ToString(3);
            _notifyIcon = new NotifyIcon
            {
                Icon = CreateClaudeIcon(Color.FromArgb(217, 119, 6)),
                Text = $"Claude Usage Tracker v{verStr}",
                Visible = true
            };

            var contextMenu = new ContextMenuStrip();
            _notifyIcon.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    _isMenuOpen = true;
                    // Safety net: nếu vì lý do nào đó menu không mở ra (VD: bị hệ thống nuốt sự kiện),
                    // tự gỡ _isMenuOpen sau 600ms để không khoá cứng HUD & tooltip vĩnh viễn.
                    _ = Task.Delay(600).ContinueWith(_ =>
                    {
                        RunOnUi(() =>
                        {
                            if (_isMenuOpen && !contextMenu.Visible)
                                _isMenuOpen = false;
                            return Task.CompletedTask;
                        }, "ResetMenuFlag");
                    });
                }
            };

            contextMenu.Opening += (s, e) => _isMenuOpen = true;
            contextMenu.Opened += (s, e) => _isMenuOpen = true;
            contextMenu.Closed += (s, e) =>
            {
                _isMenuOpen = false;
                if (_lastUsage != null) UpdateTrayTooltip(_lastUsage);
            };

            var versionItem = new ToolStripMenuItem($"Claude Usage Tracker  v{verStr}") { Enabled = false };
            contextMenu.Items.Add(versionItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            _hudMenuItem = new ToolStripMenuItem($"📊 Bảng Usage (HUD)   [{FormatHotkey(_config.HotkeyModifiers, _config.HotkeyVirtualKey)}]");
            _hudMenuItem.Click += (s, e) => ToggleHud();
            contextMenu.Items.Add(_hudMenuItem);
            contextMenu.Items.Add("📈 Thống kê", null, (s, e) => OpenStats());
            contextMenu.Items.Add("🔄 Làm mới Usage", null, async (s, e) => await RefreshUsageAsync(showFeedback: true));
            _menuFont = new Font(System.Drawing.SystemFonts.MenuFont ?? System.Drawing.SystemFonts.DefaultFont, System.Drawing.FontStyle.Bold);
            _updateMenuItem = new ToolStripMenuItem("⬆ Cập nhật", null, (s, e) =>
            {
                if (_availableUpdate != null) HandleManualUpdateFound(_availableUpdate);
            })
            {
                Visible = false,
                Font = _menuFont
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

            var toolsMenu = new ToolStripMenuItem("🧰 Công cụ tối ưu Token");
            toolsMenu.DropDownItems.Add("🖼 Nén ảnh Clipboard (tiết kiệm Vision token)", null, (s, e) => CompressClipboardImageAction());
            toolsMenu.DropDownItems.Add("🔌 Kiểm tra 'Thuế' MCP Server", null, (s, e) => InspectMcpAction());
            toolsMenu.DropDownItems.Add("📁 Quét dự án (CLAUDE.md & .claudeignore)", null, (s, e) => AuditProjectAction());
            contextMenu.Items.Add(toolsMenu);

            var promptsMenu = new ToolStripMenuItem("📋 Prompt tiết kiệm token");
            promptsMenu.DropDownItems.Add("Chỉ trả về diff code cần sửa, không giải thích dài", null, (s, e) =>
                CopyPromptSnippet("Chỉ trả về code diff cần sửa, không giải thích dài dòng."));
            promptsMenu.DropDownItems.Add("Trả lời ngắn gọn tối đa 5 dòng", null, (s, e) =>
                CopyPromptSnippet("Trả lời ngắn gọn tối đa 5 dòng, đi thẳng vào giải pháp."));
            promptsMenu.DropDownItems.Add("Chỉ liệt kê file và số dòng lỗi", null, (s, e) =>
                CopyPromptSnippet("Chỉ liệt kê tên file và số dòng bị lỗi, chưa cần sửa."));
            contextMenu.Items.Add(promptsMenu);

            var privacyItem = new ToolStripMenuItem("👁 Chế độ riêng tư (Ẩn khi share màn hình)")
            {
                Checked = _config.PrivacyMode,
                CheckOnClick = true
            };
            privacyItem.Click += (s, e) =>
            {
                _config.PrivacyMode = privacyItem.Checked;
                ConfigService.Save(_config);
                if (_config.PrivacyMode) _hud?.Hide();
            };
            contextMenu.Items.Add(privacyItem);

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

        private bool IsContextMenuOpen => _isMenuOpen || _notifyIcon?.ContextMenuStrip?.Visible == true;

        private void ToggleHud()
        {
            RunOnUi(async () =>
            {
                if (_hud == null || IsContextMenuOpen) return;
                if (_hud.IsVisible)
                {
                    _hud.HideByUser();
                    return;
                }

                _lastUsage ??= await FetchUsageAsync();
                _hud.ApplyData(_lastUsage);
                _hud.ShowByUser();
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

        // Bốn nguồn cùng gọi refresh (tick 30s, file watcher, đổi foreground, bấm tay).
        // Không chặn thì lượt chậm hơn về sau ghi đè _lastUsage bằng số cũ hơn.
        private bool _refreshing;

        private async Task RefreshUsageAsync(bool showFeedback = false)
        {
            if (_refreshing) return;
            _refreshing = true;
            try { await RefreshUsageCoreAsync(showFeedback); }
            finally { _refreshing = false; }
        }

        private async Task RefreshUsageCoreAsync(bool showFeedback)
        {
            var previousSnapshot = _lastUsage?.LastRefreshedAt;

            // Auto-pause API sync khi không thấy Claude > 30 phút
            if (_lastClaudeSeenAt != default && !_apiSync.Paused)
            {
                // _lastClaudeSeenAt chỉ được cập nhật lúc Claude *khởi động* → nếu Claude mở liên tục > 30 phút
                // sync bị tạm dừng nhầm. Kiểm tra tiến trình còn chạy trước khi auto-pause.
                if ((DateTime.Now - _lastClaudeSeenAt).TotalMinutes > 30 && ProcessUtil.IsClaudeRunning())
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
                // Khi menu chuột phải đang mở, KHÔNG sửa Text hay Icon vì Shell_NotifyIcon(NIM_MODIFY)
                // trong Windows Shell sẽ ngay lập tức huỷ bỏ popup menu đang hiện.
                if (IsContextMenuOpen) return;

                string tip = $"Claude: {info.PercentUsed:0}% | {info.MessagesRemaining}";
                // NotifyIcon.Text giới hạn 63 ký tự. Cắt theo text element chứ không theo char:
                // Substring giữa một surrogate pair (emoji) sinh ký tự rác.
                if (tip.Length > 63) tip = Truncate(tip, 60) + "...";
                if (_notifyIcon.Text != tip) _notifyIcon.Text = tip;

                // Đẩy màu icon lên một mức khi có cảnh báo đang tồn tại (dự báo cạn quota, ngữ cảnh phình to).
                string hex = info.StatusColorHex;
                if (info.ConditionLevel == AlertLevel.Critical) hex = "#EF4444";
                else if (info.ConditionLevel == AlertLevel.Warn && hex == "#10B981") hex = "#F59E0B";

                int pct = (int)Math.Round(info.PercentUsed);
                if (hex != _lastIconHex || pct != _lastIconPct)
                {
                    _lastIconHex = hex;
                    _lastIconPct = pct;
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = CreateClaudeIcon(ColorFromHex(hex), pct);
                    oldIcon?.Dispose();
                }
            }
        }

        // Cắt ở ranh giới text element để không xé đôi surrogate pair hay cụm dấu kết hợp.
        private static string Truncate(string text, int maxChars)
        {
            if (text.Length <= maxChars) return text;

            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            int lastSafe = 0;
            while (e.MoveNext())
            {
                int end = e.ElementIndex + ((string)e.Current).Length;
                if (end > maxChars) break;
                lastSafe = end;
            }
            return text[..lastSafe];
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

        private bool _checkingUpdate;

        private async Task CheckForUpdateAsync(bool manual, TimeSpan? delay = null)
        {
            if (delay.HasValue) await Task.Delay(delay.Value);

            if (!manual)
            {
                if (!_config.EnableUpdateCheck) return;
                if (DateTime.UtcNow - _updateState.LastCheckUtc < TimeSpan.FromHours(6)) return;
            }

            if (_checkingUpdate) return;
            _checkingUpdate = true;

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
                            HandleManualUpdateFound(release);
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
            finally
            {
                _checkingUpdate = false;
            }
        }

        private void HandleManualUpdateFound(ReleaseInfo release)
        {
            string currentVer = UpdateLogic.Normalize(CurrentVersion()).ToString(3);
            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "" : $"\n\nNội dung mới:\n{release.Notes}";
            string? gitRoot = FindGitRepoRoot();

            if (gitRoot != null)
            {
                // Người dùng đang chạy từ bản clone git
                var ans = MessageBox.Show(
                    $"Có bản mới {release.Tag} (đang dùng v{currentVer}).{notes}\n\n" +
                    "Bạn có muốn mở cửa sổ Terminal (CMD) để tự động cập nhật ngay không?\n" +
                    "(Sẽ chạy 'git pull', build lại Release và khởi động lại, không cần tải từ web)",
                    "Kiểm tra bản mới - Claude Usage Tracker",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (ans == MessageBoxResult.Yes)
                {
                    RunGitUpdateTerminal(gitRoot);
                }
                else
                {
                    var webAns = MessageBox.Show("Bạn có muốn xem trang phát hành trên trình duyệt web không?",
                        "GitHub Releases", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (webAns == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                }
            }
            else
            {
                // Người dùng chạy bản cài đặt standalone
                var ans = MessageBox.Show(
                    $"Có bản mới {release.Tag} (đang dùng v{currentVer}).{notes}\n\n" +
                    "Bạn có muốn mở cửa sổ Terminal (PowerShell) để tự động tải, kiểm tra SHA-256 và cập nhật ngay không?\n" +
                    "(Cập nhật tự động trực tiếp trong terminal, không cần tải thủ công từ web)",
                    "Kiểm tra bản mới - Claude Usage Tracker",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (ans == MessageBoxResult.Yes)
                {
                    RunPowerShellUpdateTerminal(release);
                }
                else
                {
                    var webAns = MessageBox.Show("Bạn có muốn xem trang phát hành trên trình duyệt web không?",
                        "GitHub Releases", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (webAns == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                }
            }
        }

        private static string? FindGitRepoRoot()
        {
            try
            {
                string? current = AppContext.BaseDirectory;
                for (int i = 0; i < 5 && current != null; i++)
                {
                    if (Directory.Exists(Path.Combine(current, ".git")))
                        return current;
                    current = Path.GetDirectoryName(current);
                }
            }
            catch { }
            return null;
        }

        private void RunGitUpdateTerminal(string gitRoot)
        {
            try
            {
                string batPath = Path.Combine(gitRoot, "update.bat");
                if (!File.Exists(batPath))
                {
                    string script =
@"@echo off
title Cap nhat Claude Usage Tracker tu GitHub
setlocal enabledelayedexpansion
cd /d ""%~dp0""
echo ========================================================
echo   CAP NHAT CLAUDE USAGE TRACKER TU GITHUB
echo ========================================================
echo.
echo [1/3] Dong ung dung Claude Usage Tracker dang chay...
taskkill /f /im ClaudeUsageTracker.exe >nul 2>&1
timeout /t 1 >nul
echo.
echo [2/3] Dang lay ma nguon moi nhat tu Git (git pull)...
git pull
if errorlevel 1 (
    echo.
    echo [LOI] git pull that bai. Vui long kiem tra ket noi mang hoac xung dot git.
    pause
    exit /b 1
)
echo.
echo [3/3] Dang build lai ban Release...
cd /d ""%~dp0csharp""
dotnet build ClaudeUsageTracker.csproj -c Release
if errorlevel 1 (
    echo.
    echo [LOI] Build that bai.
    pause
    exit /b 1
)
echo.
echo [HOAN TAT] Dang khoi dong lai Claude Usage Tracker...
start """" ""%~dp0csharp\bin\Release\net11.0-windows\ClaudeUsageTracker.exe""
echo.
echo ========================================================
echo   CAP NHAT HOAN TAT THANH CONG!
echo ========================================================
timeout /t 3 >nul
exit /b 0";
                    File.WriteAllText(batPath, script, System.Text.Encoding.UTF8);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start \"Cap nhat Claude Usage Tracker tu Git\" \"{batPath}\"",
                    UseShellExecute = true
                });
                ExitApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không mở được terminal cập nhật:\n{ex.Message}", "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RunPowerShellUpdateTerminal(ReleaseInfo release)
        {
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string tempScript = Path.Combine(Path.GetTempPath(), "claude-tracker-update.ps1");
                string psCode =
$@"[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ErrorActionPreference = 'Stop'
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host '  DANG CAP NHAT CLAUDE USAGE TRACKER LEN {release.Tag}' -ForegroundColor Cyan
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host '[1/4] Cho ung dung Claude Usage Tracker dong hoan toan...' -ForegroundColor Yellow
Start-Sleep -Seconds 1
Stop-Process -Name 'ClaudeUsageTracker' -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$tempDir = Join-Path $env:TEMP 'ClaudeUsageTrackerUpdate'
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
$zipPath = Join-Path $tempDir '{release.ZipName}'
$sumsPath = Join-Path $tempDir 'SHA256SUMS.txt'

Write-Host '[2/4] Dang tai ban moi tu GitHub...' -ForegroundColor Yellow
Invoke-WebRequest -UseBasicParsing -Uri '{release.ZipUrl}' -OutFile $zipPath
Invoke-WebRequest -UseBasicParsing -Uri '{release.SumsUrl}' -OutFile $sumsPath

Write-Host '[3/4] Kiem tra ma bam SHA-256...' -ForegroundColor Yellow
$hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$sumsContent = Get-Content $sumsPath
$pattern = [regex]::Escape('{release.ZipName}')
$matchedLine = $sumsContent | Where-Object {{ $_ -match $pattern }} | Select-Object -First 1
$expected = if ($matchedLine) {{ (($matchedLine -split '\s+')[0]).ToLowerInvariant() }} else {{ '' }}

if ([string]::IsNullOrEmpty($expected) -or $hash -ne $expected) {{
    Write-Host '[LOI] SHA-256 khong khop! Huy cap nhat de dam bao an toan.' -ForegroundColor Red
    Read-Host 'Nhan Enter de thoat...'
    exit 1
}}
Write-Host '  [OK] SHA-256 hop le!' -ForegroundColor Green

Write-Host '[4/4] Giai nen vao thu muc phien ban moi...' -ForegroundColor Yellow
# KHONG giai nen de len thu muc dang chay: file .dll cua ban cu co the chua nha het
# handle kip (Stop-Process khong dam bao), Expand-Archive -Force giua chung se de lai
# ban cai da hong mot nua. Giai nen sang thu muc rieng ben canh roi khoi dong tu do,
# giong het co che cua nut ""Cap nhat"" trong app (PromptAndApplyUpdateAsync).
$oldDir = '{appDir.Replace("'", "''")}'
$parentDir = Split-Path $oldDir -Parent
$newDir = Join-Path $parentDir 'ClaudeUsageTracker-{release.Tag.Replace("'", "''")}'
if (Test-Path $newDir) {{ Remove-Item $newDir -Recurse -Force -ErrorAction SilentlyContinue }}
Expand-Archive -Path $zipPath -DestinationPath $newDir -Force
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

# Don cac ban cai cu khac (tru ban dang chay va ban vua cai) de khong phinh dia.
Get-ChildItem -Path $parentDir -Directory -Filter 'ClaudeUsageTracker-*' -ErrorAction SilentlyContinue |
    Where-Object {{ $_.FullName -ne $newDir -and $_.FullName -ne $oldDir }} |
    ForEach-Object {{ Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }}

Write-Host ''
Write-Host '========================================================' -ForegroundColor Green
Write-Host '  CAP NHAT HOAN TAT! DANG KHOI DONG LAI UNG DUNG...' -ForegroundColor Green
Write-Host '========================================================' -ForegroundColor Green
Start-Process (Join-Path $newDir 'ClaudeUsageTracker.exe')
Start-Sleep -Seconds 2
";
                File.WriteAllText(tempScript, psCode, System.Text.Encoding.UTF8);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                    UseShellExecute = true
                });
                ExitApplication();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không mở được terminal cập nhật:\n{ex.Message}", "Lỗi cập nhật", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                CopyDirectory(stagedDir, installDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Không copy được bản mới:\n{ex.Message}\n\nBản đang chạy không bị thay đổi.",
                    "Cập nhật thất bại", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try { Directory.Delete(Path.GetDirectoryName(stagedDir)!, true); } catch { /* dọn tạm */ }

            PruneOldInstalls(installDir);

            string newExe = Path.Combine(installDir, UpdateLogic.ExeName);
            Process.Start(new ProcessStartInfo(newExe) { UseShellExecute = true });
            ExitApplication();
        }

        // EnumerateFiles chỉ thấy tầng gốc; zip phát hành có thể kèm thư mục con
        // (satellite assembly, dữ liệu phụ) và bỏ sót chúng tạo ra bản cài thiếu file.
        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
                File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.EnumerateDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }

        // Mỗi bản cài self-contained chiếm hàng trăm MB; không dọn thì LOCALAPPDATA phình mãi.
        // Giữ lại bản vừa cài (bản đang chạy nằm ở chỗ khác và không bị đụng tới).
        private static void PruneOldInstalls(string keepDir)
        {
            try
            {
                string root = Path.GetDirectoryName(keepDir)!;
                string? running = Environment.ProcessPath is string p ? Path.GetDirectoryName(p) : null;

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (PathEquals(dir, keepDir) || (running != null && PathEquals(dir, running))) continue;
                    try { Directory.Delete(dir, true); } catch { /* đang bị khoá: lần sau dọn */ }
                }
            }
            catch { /* dọn dẹp thất bại không được chặn việc cập nhật */ }
        }

        private static bool PathEquals(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                          Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase);

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
                _hud?.ApplyOpacity(_config.HudOpacity);
                RegisterAllHotkeys(showConflictToast: true);
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

        // Số liệu usage là dữ liệu riêng tư — chỉ gửi qua https, không gửi plaintext qua http.
        private static bool IsValidWebhook(string? url) =>
            !string.IsNullOrWhiteSpace(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps;

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

        private void CompressClipboardImageAction()
        {
            var (ok, msg) = TokenToolkitService.CompressClipboardImage();
            ToastWindow.ShowToast(ok ? "🖼 Đã nén ảnh Clipboard" : "🖼 Nén ảnh Clipboard", msg, ok ? "✔" : "ℹ️");
        }

        private void InspectMcpAction()
        {
            var res = TokenToolkitService.InspectMcpServers();
            string list = res.ServerNames.Count > 0 ? "\n• " + string.Join("\n• ", res.ServerNames) : "\n(Không phát hiện MCP server nào)";
            MessageBox.Show(
                $"Tổng MCP Server đang bật: {res.TotalCount} (Desktop: {res.DesktopCount}, CLI: {res.CliCount})\n" +
                $"Ước tính 'thuế' schema mỗi lượt chat: ~{res.EstimatedTokensPerTurn:N0} tokens\n" +
                $"{list}\n\n" +
                "Mẹo: Tắt bớt MCP không dùng tới để giảm tốc độ đốt quota!",
                "Kiểm tra MCP Overhead", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void AuditProjectAction()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Chọn thư mục dự án muốn kiểm tra tối ưu Token",
                UseDescriptionForTitle = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            var (_, hasIgnore, summary) = TokenToolkitService.AuditProject(dlg.SelectedPath);
            if (!hasIgnore)
            {
                var ans = MessageBox.Show(
                    $"Kết quả kiểm tra {Path.GetFileName(dlg.SelectedPath)}:\n\n{summary}\n\nBạn có muốn tự động tạo file .claudeignore chuẩn ngay bây giờ không?",
                    "Quét dự án tối ưu Token", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans == MessageBoxResult.Yes)
                {
                    bool created = TokenToolkitService.CreateDefaultClaudeIgnore(dlg.SelectedPath);
                    ToastWindow.ShowToast(created ? "✔ Đã tạo .claudeignore" : "⚠ Không tạo được .claudeignore",
                        dlg.SelectedPath, created ? "📁" : "⚠️");
                }
            }
            else
            {
                MessageBox.Show($"Kết quả kiểm tra {Path.GetFileName(dlg.SelectedPath)}:\n\n{summary}",
                    "Quét dự án tối ưu Token", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CopyPromptSnippet(string prompt)
        {
            try
            {
                System.Windows.Clipboard.SetText(prompt);
                ToastWindow.ShowToast("📋 Đã copy Prompt mẫu", prompt, "📋");
            }
            catch { }
        }

        private bool _isExiting;
        private Task<(bool Ok, string Message)> SendTerminalCommandAsync(string command) =>
            TerminalCommandService.SendCommandAsync(_foregroundWatcher?.LastTargetHwnd ?? IntPtr.Zero, command);

        // Cả 4 hotkey đều do người dùng tự đặt trong Settings nên có thể trùng nhau bất kỳ lúc nào.
        // Windows chỉ cho MỘT nơi giữ mỗi tổ hợp — đăng ký lần lượt, bên trùng với bên đã giữ trước
        // sẽ bị bỏ qua có chủ đích (thay vì để RegisterHotKey tự fail âm thầm) và báo cho người dùng biết.
        private void RegisterAllHotkeys(bool showConflictToast)
        {
            _hotkey?.Unregister();
            _hotkeyStats?.Unregister();
            _hotkeyQuickHaiku?.Unregister();
            _hotkeyQuickCompact?.Unregister();

            var claimed = new List<(uint mod, uint vk)>();
            var conflicts = new List<string>();

            bool TryClaim(GlobalHotkeyService? svc, uint mod, uint vk, string label)
            {
                if (svc == null || mod == 0 || vk == 0) return false;
                if (claimed.Any(c => c.mod == mod && c.vk == vk))
                {
                    conflicts.Add(label);
                    return false;
                }
                bool ok;
                try { ok = svc.Register(mod, vk); } catch { ok = false; }
                if (ok) claimed.Add((mod, vk)); else conflicts.Add(label);
                return ok;
            }

            TryClaim(_hotkey, _config.HotkeyModifiers, _config.HotkeyVirtualKey, "📊 HUD");
            TryClaim(_hotkeyStats, _config.StatsHotkeyModifiers, _config.StatsHotkeyVirtualKey, "📈 Thống kê");
            TryClaim(_hotkeyQuickHaiku, _config.QuickHaikuModifiers, _config.QuickHaikuVirtualKey, "⚡ Haiku");
            TryClaim(_hotkeyQuickCompact, _config.QuickCompactModifiers, _config.QuickCompactVirtualKey, "🧹 Compact");

            if (showConflictToast && conflicts.Count > 0)
                ToastWindow.ShowToast("⚠ Phím tắt bị trùng",
                    $"{string.Join(", ", conflicts)} không đăng ký được vì trùng tổ hợp với phím tắt khác. Đổi trong Cài đặt.",
                    "⚠️");
        }

        private void ExitApplication()
        {
            if (_isExiting) return;
            _isExiting = true;
            _tick?.Stop();
            _updateTimer?.Stop();
            _hotkey?.Dispose();
            _hotkeyStats?.Dispose();
            _hotkeyQuickHaiku?.Dispose();
            _hotkeyQuickCompact?.Dispose();
            _apiSync?.Dispose();
            _updater?.Dispose();
            _webhookHttp.Dispose();
            // ?. bắt buộc: OnStartup có thể thoát sớm (đã có instance khác) trước khi dựng xong các service.
            _watcherService?.Dispose();
            _fileWatcherService?.Dispose();
            _foregroundWatcher?.Dispose();
            Views.ToastWindow.ShutdownAll();
            _statsWindow?.Close();
            _countdownWindow?.Close();
            if (_hud != null)
            {
                _hud.AllowRealClose = true;
                _hud.Close();
            }
            _singleInstance?.Dispose();
            _singleInstance = null;
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                // NotifyIcon.Dispose() không đụng tới ContextMenuStrip và Icon của nó.
                // Font phải dispose TRƯỚC: sau khi strip bị dispose, item mất Owner nên
                // ToolStripItem.Font trả về font dùng chung của hệ thống — dispose nhầm cái đó
                // là phá luôn giao diện của tiến trình.
                _menuFont?.Dispose();
                _menuFont = null;
                _notifyIcon.ContextMenuStrip?.Dispose();
                _notifyIcon.Icon?.Dispose();
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
