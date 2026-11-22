using ClaudeUsageTracker.Models;
using ClaudeUsageTracker.Services;

int failed = 0;
int passed = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { passed++; Console.WriteLine($"  PASS  {name}"); }
    else { failed++; Console.WriteLine($"  FAIL  {name} {detail}"); }
}

var now = new DateTime(2026, 11, 21, 15, 0, 0);
UsageSample S(int minutesAgo, double fh, double sd = 0, string? model = null, double? ctx = null, string? sid = null)
    => new(now.AddMinutes(-minutesAgo), fh, sd, model, ctx, sid);

Console.WriteLine("StatsService.ComputeModelBurn");
{
    var samples = new List<UsageSample>
    {
        S(30, 10, 0, "Opus 5"),
        S(25, 16, 0, "Opus 5"),   // +6 Opus
        S(20, 18, 0, "Sonnet 5"), // +2 Sonnet
        S(15, 18, 0, "Sonnet 5"), // +0
        S(5,  1,  0, "Sonnet 5"), // giảm = reset, bỏ qua
        S(2,  4,  0, "Sonnet 5"), // +3 Sonnet
    };
    var r = StatsService.ComputeModelBurn(samples, now, TimeSpan.FromHours(24));
    Check("tổng Opus = 6", r.Items.FirstOrDefault(i => i.Model == "Opus 5")?.Percent == 6);
    Check("tổng Sonnet = 5 (reset bị bỏ)", r.Items.FirstOrDefault(i => i.Model == "Sonnet 5")?.Percent == 5);
    Check("sắp xếp giảm dần", r.Items[0].Model == "Opus 5");
    Check("tỉ lệ cộng lại = 1", Math.Abs(r.Items.Sum(i => i.Share) - 1) < 1e-9);

    var gap = new List<UsageSample> { S(60, 10, 0, "Opus 5"), S(30, 40, 0, "Opus 5") }; // cách 30 phút
    Check("khoảng nghỉ >10 phút bị bỏ", !StatsService.ComputeModelBurn(gap, now, TimeSpan.FromHours(24)).HasData);

    var desktopOnly = new List<UsageSample> { S(10, 10), S(5, 20) };
    Check("mẫu Desktop (không model) không bị gán", !StatsService.ComputeModelBurn(desktopOnly, now, TimeSpan.FromHours(24)).HasData);

    var old = new List<UsageSample> { S(60 * 30, 10, 0, "Opus 5"), S(60 * 30 - 5, 20, 0, "Opus 5") };
    Check("ngoài cửa sổ 24h bị bỏ", !StatsService.ComputeModelBurn(old, now, TimeSpan.FromHours(24)).HasData);
}

Console.WriteLine("StatsService.ComputeProjection");
{
    var reset = now.AddHours(2.5);

    // 10% -> 30% trong 40 phút = 30%/giờ; còn 70% => ~2h20m nữa chạm 100%
    var steep = new List<UsageSample> { S(40, 10), S(20, 20), S(0, 30) };
    var p = StatsService.ComputeProjection(steep, now, reset);
    Check("dốc dương: có dự báo", p.CanProject);
    Check("nhịp ≈ 30%/giờ", Math.Abs(p.RatePerHour - 30) < 0.5, $"(rate={p.RatePerHour})");
    Check("chạm trần sau ≈ 2h20m", p.HitTime.HasValue && Math.Abs((p.HitTime.Value - now).TotalMinutes - 140) < 2);
    Check("reset sau 2h30m => chạm TRƯỚC reset", p.HitsBeforeReset);
    Check("có biên an toàn ≈ 10 phút", p.MarginBeforeReset.HasValue && Math.Abs(p.MarginBeforeReset.Value.TotalMinutes - 10) < 2);

    var p2 = StatsService.ComputeProjection(steep, now, now.AddHours(1));
    Check("reset sớm hơn thì an toàn", p2.CanProject && !p2.HitsBeforeReset);

    var flat = new List<UsageSample> { S(40, 20), S(20, 20), S(0, 20) };
    var pf = StatsService.ComputeProjection(flat, now, reset);
    Check("phẳng: không báo nguy hiểm", !pf.HitsBeforeReset && !pf.CanProject);

    var tooFew = new List<UsageSample> { S(3, 10), S(0, 12) };
    Check("quá ít dữ liệu (<10 phút): không dự báo", !StatsService.ComputeProjection(tooFew, now, reset).CanProject);

    var crossReset = new List<UsageSample> { S(50, 90), S(40, 95), S(30, 2), S(10, 8), S(0, 14) };
    var pr = StatsService.ComputeProjection(crossReset, now, reset);
    // Đoạn sau reset: 2% -> 8% -> 14% trong 30 phút ≈ 23%/giờ (nếu lẫn đoạn trước reset sẽ ra sai hẳn)
    Check("chỉ dùng đoạn sau lần reset (≈ 23%/giờ)", pr.CanProject && Math.Abs(pr.RatePerHour - 23) < 1.5, $"(rate={pr.RatePerHour})");

    // 2 nguồn lệch nhau ±1% (zigzag) không được coi là reset, và vẫn ra nhịp ≈ 16%/giờ
    var noisy = new List<UsageSample> { S(50, 20), S(40, 19), S(30, 26), S(20, 25), S(10, 32), S(0, 31) };
    var pn = StatsService.ComputeProjection(noisy, now, reset);
    Check("nhiễu ±1% không bị coi là reset (≈ 16%/giờ)", pn.CanProject && Math.Abs(pn.RatePerHour - 16) < 1.5, $"(rate={pn.RatePerHour})");

    var full = new List<UsageSample> { S(20, 98), S(0, 100) };
    Check("đã 100%: báo đã chạm trần", StatsService.ComputeProjection(full, now, reset).Text.Contains("chạm trần"));
}

Console.WriteLine("StatsService.ProjectBurn + WeeklyBudget + PromptsEst");
{
    var projSamples = new List<UsageSample>
    {
        new(now.AddMinutes(-20), 10, 10, "Sonnet", Project: "repo-a"),
        new(now.AddMinutes(-15), 22, 10, "Sonnet", Project: "repo-a"), // +12 repo-a
        new(now.AddMinutes(-10), 27, 10, "Sonnet", Project: "repo-b"), // +5 repo-b
    };
    var pb = StatsService.ComputeProjectBurn(projSamples, now, TimeSpan.FromHours(1));
    Check("project burn: repo-a = 12", pb.Items.Count == 2 && pb.Items[0].Model == "repo-a" && Math.Abs(pb.Items[0].Percent - 12) < 0.01);
    Check("project burn: repo-b = 5", Math.Abs(pb.Items[1].Percent - 5) < 0.01);

    var wb = StatsService.ComputeWeeklyBudgetNote(40, now.AddDays(3), now);
    Check("weekly budget: còn 60% / 3.0 ngày (~20%/ngày)", wb != null && wb.Contains("60%") && wb.Contains("20%/ngày"));
    Check("weekly budget null khi quá hạn", StatsService.ComputeWeeklyBudgetNote(40, now.AddHours(-1), now) == null);

    var pe = StatsService.EstimateRemainingPromptsNote(50);
    Check("prompts est 50%: có Opus/Sonnet/Haiku", pe != null && pe.Contains("Opus") && pe.Contains("Sonnet") && pe.Contains("Haiku"));
    Check("prompts est 100%: null", StatsService.EstimateRemainingPromptsNote(100) == null);

    var loopSamples = new List<UsageSample>
    {
        new(now.AddMinutes(-4), 20, 10, "Opus", SessionId: "s-loop"),
        new(now.AddMinutes(-3), 24, 10, "Opus", SessionId: "s-loop"),
        new(now.AddMinutes(-2), 28, 10, "Opus", SessionId: "s-loop"),
        new(now.AddMinutes(-1), 33, 10, "Opus", SessionId: "s-loop"),
    };
    var loopDelta = StatsService.DetectInfiniteLoop(loopSamples, now, "s-loop");
    Check("loop detector: +13% trong 4 phút => phát hiện", loopDelta.HasValue && Math.Abs(loopDelta.Value - 13) < 0.01);
    Check("loop detector: session khác => null", StatsService.DetectInfiniteLoop(loopSamples, now, "s-other") == null);
}

Console.WriteLine("StatsService.EstimateNow");
{
    var reset = now.AddHours(2);

    // 5% mỗi 15 phút = 20%/giờ, mẫu cuối cũ 10 phút => +3.3 điểm
    var steady = new List<UsageSample> { S(40, 10), S(25, 15), S(10, 20) };
    var e = StatsService.EstimateNow(steady, now, reset);
    Check("nhịp đều: có ước tính ≈ 23.3%", e != null && Math.Abs(e.Value - 23.33) < 0.5, $"(got {e?.Value})");
    Check("delta ≈ 3.3 điểm", e != null && Math.Abs(e.Delta - 3.33) < 0.5);

    var fresh = new List<UsageSample> { S(41, 10), S(26, 15), S(1, 20) };
    Check("mẫu mới (<2 phút): không cần ước tính", StatsService.EstimateNow(fresh, now, reset) == null);

    var stale = new List<UsageSample> { S(70, 10), S(55, 15), S(30, 20) };
    Check("mẫu quá cũ (>20 phút): không ước tính", StatsService.EstimateNow(stale, now, reset) == null);

    var idle = new List<UsageSample> { S(60, 10), S(45, 20), S(30, 30), S(15, 30) };
    Check("vừa nghỉ sau đợt dùng mạnh: không ước tính", StatsService.EstimateNow(idle, now, reset) == null);

    var fast = new List<UsageSample> { S(45, 10), S(30, 40), S(15, 70) }; // 120%/giờ
    var ef = StatsService.EstimateNow(fast, now, reset);
    Check("nhịp rất nhanh: chặn trần +10 điểm", ef != null && Math.Abs(ef.Delta - 10) < 1e-9 && Math.Abs(ef.Value - 80) < 1e-9, $"(got {ef?.Delta})");

    Check("đã qua giờ reset: không ước tính", StatsService.EstimateNow(steady, now, now.AddMinutes(-1)) == null);
    Check("đã 100%: không ước tính", StatsService.EstimateNow(new List<UsageSample> { S(30, 98), S(12, 100) }, now, reset) == null);
    Check("quá ít mẫu: không ước tính", StatsService.EstimateNow(new List<UsageSample> { S(10, 20) }, now, reset) == null);

    var nearCap = new List<UsageSample> { S(45, 60), S(30, 75), S(10, 92) }; // ~ 40%/giờ
    var en = StatsService.EstimateNow(nearCap, now, reset);
    Check("không bao giờ vượt 100%", en == null || en.Value <= 100);
}

Console.WriteLine("AlertEngine");
{
    var cfg = new AlertConfig();
    var state = new AlertState();
    AlertInput In(double fh, double? sd = 0, double? ctx = null, string? sid = "s1", Projection? p = null, int minutes = 0)
        => new(fh, sd, ctx, sid, p, now.AddMinutes(minutes));

    Check("dưới ngưỡng: không báo", AlertEngine.Evaluate(In(50), cfg, state).Count == 0);

    var a80 = AlertEngine.Evaluate(In(82), cfg, state);
    Check("qua 80%: báo 1 lần mức Warn", a80.Count == 1 && a80[0].Level == AlertLevel.Warn);
    Check("vẫn 83%: không báo lặp", AlertEngine.Evaluate(In(83), cfg, state).Count == 0);

    var a95 = AlertEngine.Evaluate(In(96), cfg, state);
    Check("qua 95%: báo mức Critical", a95.Count == 1 && a95[0].Level == AlertLevel.Critical);

    var jump = new AlertState();
    var aj = AlertEngine.Evaluate(In(97), cfg, jump);
    Check("nhảy qua 2 mốc cùng lúc: chỉ 1 cảnh báo", aj.Count == 1 && aj[0].Level == AlertLevel.Critical);

    AlertEngine.Evaluate(In(10), cfg, state); // sang chu kỳ mới
    Check("sang chu kỳ mới rồi vượt lại: báo lại", AlertEngine.Evaluate(In(85), cfg, state).Count == 1);

    // lưu/đọc trạng thái (mô phỏng restart)
    string tmp = Path.Combine(Path.GetTempPath(), $"alert_state_{Guid.NewGuid():N}.json");
    var s2 = new AlertState();
    AlertEngine.Evaluate(In(85), cfg, s2);
    AlertStateStore.Save(tmp, s2);
    var reloaded = AlertStateStore.Load(tmp);
    File.Delete(tmp);
    Check("restart: không báo lặp mốc đã báo", AlertEngine.Evaluate(In(86), cfg, reloaded).Count == 0);

    var s3 = new AlertState();
    var ctxA = AlertEngine.Evaluate(In(10, ctx: 75), cfg, s3);
    Check("ngữ cảnh ≥70%: báo", ctxA.Count == 1 && ctxA[0].Title.Contains("Ngữ cảnh"));
    Check("ngữ cảnh: không báo lặp", AlertEngine.Evaluate(In(10, ctx: 80), cfg, s3).Count == 0);
    AlertEngine.Evaluate(In(10, ctx: 20), cfg, s3); // /compact
    Check("sau /compact ngữ cảnh phình lại: báo lại", AlertEngine.Evaluate(In(10, ctx: 75), cfg, s3).Count == 1);
    Check("session khác: báo riêng", AlertEngine.Evaluate(In(10, ctx: 75, sid: "s2"), cfg, s3).Count == 1);

    var s4 = new AlertState();
    var risky = new Projection(true, now.AddMinutes(30), 40, true, TimeSpan.FromMinutes(20), "chạm trần sớm");
    Check("dự báo chạm trần trước reset: báo", AlertEngine.Evaluate(In(60, p: risky), cfg, s4).Count == 1);
    Check("dự báo: cooldown 30 phút", AlertEngine.Evaluate(In(61, p: risky, minutes: 10), cfg, s4).Count == 0);
    Check("dự báo: hết cooldown thì báo lại", AlertEngine.Evaluate(In(62, p: risky, minutes: 45), cfg, s4).Count == 1);

    // nhắc khi đã reset sau lúc chạm trần
    var rs = new AlertState();
    Check("chưa từng chạm trần, % thấp: không nhắc reset", AlertEngine.Evaluate(In(30), cfg, rs).All(a => !a.Title.Contains("reset")));
    AlertEngine.Evaluate(In(97), cfg, rs); // chạm trần => ghi cờ
    var after = AlertEngine.Evaluate(In(12, minutes: 120), cfg, rs);
    Check("chạm trần rồi reset: nhắc đúng 1 lần mức Info",
        after.Count(a => a.Title.Contains("reset")) == 1 && after.First(a => a.Title.Contains("reset")).Level == AlertLevel.Info);
    Check("nhắc xong: không nhắc lặp", AlertEngine.Evaluate(In(15, minutes: 125), cfg, rs).All(a => !a.Title.Contains("reset")));

    var rs2 = new AlertState();
    AlertEngine.Evaluate(In(96), cfg, rs2);
    Check("còn cao (chưa reset): chưa nhắc", AlertEngine.Evaluate(In(80, minutes: 10), cfg, rs2).All(a => !a.Title.Contains("reset")));

    var rs3 = new AlertState();
    AlertEngine.Evaluate(In(96), cfg, rs3);
    var noRemind = new AlertConfig { ResetReminder = false };
    Check("tắt nhắc reset: im lặng", AlertEngine.Evaluate(In(5, minutes: 200), noRemind, rs3).All(a => !a.Title.Contains("reset")));

    var rs4 = new AlertState();
    AlertEngine.Evaluate(In(10, sd: 97), cfg, rs4);
    Check("tuần chạm trần rồi reset: nhắc riêng cho tuần",
        AlertEngine.Evaluate(In(10, sd: 3, minutes: 60), cfg, rs4).Any(a => a.Title.Contains("tuần") && a.Title.Contains("reset")));

    var off = new AlertConfig { Enabled = false };
    Check("tắt cảnh báo: im lặng", AlertEngine.Evaluate(In(99, ctx: 99), off, new AlertState()).Count == 0);
}

Console.WriteLine("ModelSwitchEngine");
{
    var o = new ModelSwitchOptions(85, 95);
    var none = new ModelSwitchState();
    ModelSwitchState Ours(string applied, string? original) => new() { Applied = applied, Original = original };

    Check("quota thấp: không đổi", ModelSwitchEngine.Decide(50, o, none, "opus").Action == ModelAction.None);

    var d1 = ModelSwitchEngine.Decide(86, o, none, "opus");
    Check("≥85% đang opus: đặt sonnet", d1.Action == ModelAction.Apply && d1.Target == "sonnet");

    var d2 = ModelSwitchEngine.Decide(96, o, none, "opus");
    Check("≥95% đang opus: đặt haiku", d2.Action == ModelAction.Apply && d2.Target == "haiku");

    Check("đang haiku rồi: không đổi", ModelSwitchEngine.Decide(90, o, none, "haiku").Action == ModelAction.None);
    Check("không bao giờ nâng model (đang sonnet, muốn sonnet)", ModelSwitchEngine.Decide(90, o, none, "claude-sonnet-4-6").Action == ModelAction.None);
    Check("chưa đặt model (mặc định): đặt sonnet", ModelSwitchEngine.Decide(90, o, none, null).Action == ModelAction.Apply);

    Check("đã đặt sonnet, quota 80%: chờ (trễ)", ModelSwitchEngine.Decide(80, o, Ours("sonnet", "opus"), "sonnet").Action == ModelAction.None);

    var r = ModelSwitchEngine.Decide(70, o, Ours("sonnet", "opus"), "sonnet");
    Check("quota hạ <75%: khôi phục model gốc", r.Action == ModelAction.Restore && r.Target == "opus");

    var up = ModelSwitchEngine.Decide(96, o, Ours("sonnet", "opus"), "sonnet");
    Check("đang sonnet, quota ≥95%: hạ tiếp xuống haiku", up.Action == ModelAction.Apply && up.Target == "haiku");

    Check("haiku, quota hạ còn 88%: nới lại sonnet", ModelSwitchEngine.Decide(88, o, Ours("haiku", "opus"), "haiku").Target == "sonnet");
    Check("haiku, quota 92%: giữ haiku (trễ)", ModelSwitchEngine.Decide(92, o, Ours("haiku", "opus"), "haiku").Action == ModelAction.None);

    Check("người dùng tự đổi model: không đè, bỏ theo dõi",
        ModelSwitchEngine.Decide(90, o, Ours("sonnet", "opus"), "opus").Action == ModelAction.Forget);

    Check("model gốc null được khôi phục thành null", ModelSwitchEngine.Decide(10, o, Ours("sonnet", null), "sonnet").Target == null);
}

Console.WriteLine("ModelSwitchService + ClaudeSettingsFile");
{
    string dir = Path.Combine(Path.GetTempPath(), $"modelsw_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    string settings = Path.Combine(dir, "settings.json");
    string state = Path.Combine(dir, "model-switch.json");

    const string original =
        "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"command\": \"node \\\"C:\\\\x\\\\statusline.js\\\"\"\n  },\n  \"note\": \"tiếng Việt có dấu\",\n  \"effortLevel\": \"medium\"\n}";
    File.WriteAllText(settings, original);

    var cfg = new AppConfig { EnableAutoModel = true };
    UsageInfo Info(double fh, double sd = 0, bool stale = false, bool demo = false)
        => new() { PercentUsed = fh, WeeklyPercent = sd, IsStale = stale, IsDemo = demo };

    var svc = new ModelSwitchService(cfg, new ClaudeSettingsFile(settings), state);

    var a = svc.Evaluate(Info(88, 10));
    var sf = new ClaudeSettingsFile(settings);
    Check("áp dụng: trả cảnh báo mức Info", a != null && a.Level == AlertLevel.Info && a.Title.Contains("sonnet"));
    Check("ghi model = sonnet", sf.ReadModel() == "sonnet");
    string after = File.ReadAllText(settings);
    Check("giữ nguyên các khoá khác + tiếng Việt", after.Contains("statusLine") && after.Contains("effortLevel") && after.Contains("tiếng Việt có dấu"));
    Check("có bản sao lưu gốc (model=opus)", File.Exists(settings + ".tracker.bak") && File.ReadAllText(settings + ".tracker.bak").Contains("\"opus\""));
    Check("có file trạng thái", File.Exists(state));
    Check("đánh giá lại cùng quota: không ghi lặp", svc.Evaluate(Info(89, 10)) == null);

    var restored = svc.Evaluate(Info(50, 10));
    Check("quota hạ: khôi phục opus + báo", restored != null && sf.ReadModel() == "opus");
    Check("sau khôi phục: trạng thái sạch", new ModelSwitchService(cfg, new ClaudeSettingsFile(settings), state).Evaluate(Info(50)) == null);

    // người dùng tự đổi model giữa chừng
    svc.Evaluate(Info(88));
    File.WriteAllText(settings, File.ReadAllText(settings).Replace("\"model\": \"sonnet\"", "\"model\": \"haiku\""));
    Check("người dùng tự đổi sang haiku: tool không đè", svc.Evaluate(Info(90)) == null && sf.ReadModel() == "haiku");
    Check("bị bỏ theo dõi: quota hạ cũng không khôi phục", svc.Evaluate(Info(10)) == null && sf.ReadModel() == "haiku");

    // tắt tính năng
    File.WriteAllText(settings, original);
    File.Delete(state);
    var off = new ModelSwitchService(new AppConfig { EnableAutoModel = false }, new ClaudeSettingsFile(settings), state);
    Check("tắt tính năng: không đụng settings", off.Evaluate(Info(99)) == null && File.ReadAllText(settings) == original);

    // dữ liệu cũ / demo
    var on = new ModelSwitchService(cfg, new ClaudeSettingsFile(settings), state);
    Check("dữ liệu cũ: không hành động", on.Evaluate(Info(99, stale: true)) == null && File.ReadAllText(settings) == original);
    Check("dữ liệu demo: không hành động", on.Evaluate(Info(99, demo: true)) == null && File.ReadAllText(settings) == original);

    // settings hỏng: tuyệt đối không ghi đè
    string broken = "{ \"model\": \"opus\", đây không phải json hợp lệ";
    File.WriteAllText(settings, broken);
    var onBroken = new ModelSwitchService(cfg, new ClaudeSettingsFile(settings), Path.Combine(dir, "s2.json"));
    Check("settings không hợp lệ: không ghi đè", onBroken.Evaluate(Info(99)) == null && File.ReadAllText(settings) == broken);

    // chưa có khoá model: khôi phục phải xoá khoá chứ không để lại
    File.WriteAllText(settings, "{\n  \"effortLevel\": \"medium\"\n}");
    var noModel = new ModelSwitchService(cfg, new ClaudeSettingsFile(settings), Path.Combine(dir, "s3.json"));
    noModel.Evaluate(Info(90));
    Check("không có model gốc: đặt sonnet", new ClaudeSettingsFile(settings).ReadModel() == "sonnet");
    noModel.Evaluate(Info(10));
    Check("khôi phục => xoá khoá model, giữ khoá khác",
        new ClaudeSettingsFile(settings).ReadModel() == null && File.ReadAllText(settings).Contains("effortLevel") && !File.ReadAllText(settings).Contains("\"model\""));

    Directory.Delete(dir, true);
}

Console.WriteLine("UpdateLogic");
{
    Check("parse 'v0.3.0'", UpdateLogic.ParseVersion("v0.3.0") == new Version(0, 3, 0));
    Check("parse '1.2.3' (không có v)", UpdateLogic.ParseVersion("1.2.3") == new Version(1, 2, 3));
    Check("tag lạ bị từ chối", UpdateLogic.ParseVersion("v1.2") == null && UpdateLogic.ParseVersion("nightly") == null && UpdateLogic.ParseVersion("v1.2.3-beta") == null && UpdateLogic.ParseVersion(null) == null);
    Check("0.10.0 > 0.9.0 (so số, không so chuỗi)", UpdateLogic.IsNewer(new Version(0, 9, 0, 0), new Version(0, 10, 0)));
    Check("cùng bản: không coi là mới", !UpdateLogic.IsNewer(new Version(0, 3, 0, 0), new Version(0, 3, 0)));
    Check("bản cũ hơn: không coi là mới", !UpdateLogic.IsNewer(new Version(0, 3, 0, 0), new Version(0, 2, 9)));

    const string prefix = "https://github.com/o/r/releases/download/";
    string Json(string tag, string zipUrl, string sumsUrl, bool draft = false, bool pre = false, string? zipName = null)
        => $"{{\"tag_name\":\"{tag}\",\"draft\":{draft.ToString().ToLower()},\"prerelease\":{pre.ToString().ToLower()},\"html_url\":\"https://github.com/o/r/releases/tag/{tag}\",\"body\":\"ghi chú\",\"assets\":[" +
           $"{{\"name\":\"{zipName ?? $"ClaudeUsageTracker-{tag}-win-x64.zip"}\",\"browser_download_url\":\"{zipUrl}\"}}," +
           $"{{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"{sumsUrl}\"}}]}}";

    var ok = UpdateLogic.ParseRelease(Json("v0.3.0", prefix + "v0.3.0/ClaudeUsageTracker-v0.3.0-win-x64.zip", prefix + "v0.3.0/SHA256SUMS.txt"), prefix);
    Check("parse release hợp lệ", ok != null && ok.Version == new Version(0, 3, 0) && ok.ZipName == "ClaudeUsageTracker-v0.3.0-win-x64.zip" && ok.Notes == "ghi chú");
    Check("asset trỏ ra domain lạ: từ chối",
        UpdateLogic.ParseRelease(Json("v0.3.0", "https://evil.example/x.zip", prefix + "v0.3.0/SHA256SUMS.txt"), prefix) == null);
    Check("draft bị bỏ qua", UpdateLogic.ParseRelease(Json("v0.3.0", prefix + "a.zip", prefix + "b", draft: true), prefix) == null);
    Check("prerelease bị bỏ qua", UpdateLogic.ParseRelease(Json("v0.3.0", prefix + "a.zip", prefix + "b", pre: true), prefix) == null);
    Check("tên zip không khớp phiên bản tag: từ chối",
        UpdateLogic.ParseRelease(Json("v0.3.0", prefix + "a.zip", prefix + "b", zipName: "ClaudeUsageTracker-v0.2.0-win-x64.zip"), prefix) == null);
    Check("JSON hỏng không làm sập", UpdateLogic.ParseRelease("{ không phải json", prefix) == null);

    string h = new string('a', 64);
    Check("SHA256SUMS dạng LF", UpdateLogic.ParseSha256($"{h}  f.zip\n", "f.zip") == h);
    Check("SHA256SUMS dạng CRLF", UpdateLogic.ParseSha256($"{h}  f.zip\r\n", "f.zip") == h);
    Check("SHA256SUMS thiếu tên file: null", UpdateLogic.ParseSha256($"{h}  khac.zip\n", "f.zip") == null);
    Check("băm không đủ 64 ký tự: null", UpdateLogic.ParseSha256("abc  f.zip\n", "f.zip") == null);

    string tmp = Path.Combine(Path.GetTempPath(), $"upd_{Guid.NewGuid():N}");
    Directory.CreateDirectory(tmp);

    byte[] fakeExe = new byte[2048]; fakeExe[0] = (byte)'M'; fakeExe[1] = (byte)'Z'; new Random(1).NextBytes(fakeExe.AsSpan(2));
    string MakeZip(string name, params (string entry, byte[] data)[] entries)
    {
        string path = Path.Combine(tmp, name);
        using var fs = File.Create(path);
        using var za = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (entry, data) in entries)
        {
            var e = za.CreateEntry(entry);
            using var s = e.Open();
            s.Write(data);
        }
        return path;
    }

    string good = MakeZip("good.zip", ("ClaudeUsageTracker.exe", fakeExe), ("README.md", new byte[] { 1, 2, 3 }));
    string extracted = UpdateLogic.ExtractExe(good, Path.Combine(tmp, "out1"));
    Check("giải nén đúng file exe", File.ReadAllBytes(extracted).SequenceEqual(fakeExe) && Path.GetFileName(extracted) == "ClaudeUsageTracker.exe");

    bool Throws(Action a) { try { a(); return false; } catch (InvalidDataException) { return true; } }
    Check("zip không có exe: từ chối", Throws(() => UpdateLogic.ExtractExe(MakeZip("noexe.zip", ("README.md", new byte[] { 1 })), Path.Combine(tmp, "out2"))));
    Check("zip có exe nằm trong thư mục con (path lạ): từ chối",
        Throws(() => UpdateLogic.ExtractExe(MakeZip("nested.zip", ("sub/ClaudeUsageTracker.exe", fakeExe)), Path.Combine(tmp, "out3"))));
    Check("zip path traversal ../: không ghi ra ngoài",
        Throws(() => UpdateLogic.ExtractExe(MakeZip("trav.zip", ("../ClaudeUsageTracker.exe", fakeExe)), Path.Combine(tmp, "out4"))) && !File.Exists(Path.Combine(tmp, "ClaudeUsageTracker.exe")));
    Check("file 'exe' không phải MZ: từ chối",
        Throws(() => UpdateLogic.ExtractExe(MakeZip("notmz.zip", ("ClaudeUsageTracker.exe", new byte[] { 1, 2, 3, 4 })), Path.Combine(tmp, "out5"))));

    string hashZip = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(good))).ToLower();
    Check("verify SHA-256 đúng", UpdateLogic.VerifySha256(good, hashZip));
    Check("verify SHA-256 sai", !UpdateLogic.VerifySha256(good, new string('0', 64)));

    // Dọn .old còn sót lại của bản trước (cài đặt giờ copy sang thư mục theo version, không swap tại chỗ)
    string cur = Path.Combine(tmp, "app.exe");
    File.WriteAllText(cur, "NEW");
    File.WriteAllText(cur + ".old", "OLD");
    UpdateLogic.CleanupOld(cur);
    Check("dọn .old", !File.Exists(cur + ".old"));

    // ===== Luồng đầy đủ qua HTTP cục bộ =====
    Console.WriteLine("UpdateService (server HTTP giả ở 127.0.0.1)");
    int port;
    { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
    string origin = $"http://127.0.0.1:{port}";

    string zipName = "ClaudeUsageTracker-v0.3.0-win-x64.zip";
    string serveSums = $"{hashZip}  {zipName}\n";
    byte[] zipBytes = File.ReadAllBytes(good);

    var server = new System.Net.HttpListener();
    server.Prefixes.Add(origin + "/");
    server.Start();
    var cts = new CancellationTokenSource();
    var loop = Task.Run(async () =>
    {
        while (!cts.IsCancellationRequested)
        {
            System.Net.HttpListenerContext ctx;
            try { ctx = await server.GetContextAsync(); } catch { break; }
            string p = ctx.Request.Url!.AbsolutePath;
            byte[] body; string type = "application/octet-stream";
            if (p == "/releases/latest")
            {
                type = "application/json";
                body = System.Text.Encoding.UTF8.GetBytes(Json("v0.3.0", $"{origin}/dl/{zipName}", $"{origin}/dl/SHA256SUMS.txt"));
            }
            else if (p == $"/dl/{zipName}") body = zipBytes;
            else if (p == "/dl/SHA256SUMS.txt") body = System.Text.Encoding.UTF8.GetBytes(serveSums);
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
            ctx.Response.ContentType = type;
            ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }
    });

    var opts = new UpdateOptions(origin, origin + "/");
    var svc = new UpdateService(new Version(0, 2, 0, 0), opts);
    var rel = svc.CheckAsync().GetAwaiter().GetResult();
    Check("CheckAsync: thấy v0.3.0 mới hơn 0.2.0", rel != null && rel.Tag == "v0.3.0");

    var stagedExe = svc.DownloadAndStageAsync(rel!).GetAwaiter().GetResult();
    Check("DownloadAndStage: tải, kiểm băm, giải nén đúng", File.Exists(stagedExe) && File.ReadAllBytes(stagedExe).SequenceEqual(fakeExe));
    try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(stagedExe))!, true); } catch { }

    Check("CheckAsync: đang ở 0.3.0 thì không có bản mới", new UpdateService(new Version(0, 3, 0, 0), opts).CheckAsync().GetAwaiter().GetResult() == null);

    serveSums = $"{new string('0', 64)}  {zipName}\n"; // server trả băm sai => phải từ chối
    bool tamper = false;
    try { svc.DownloadAndStageAsync(rel!).GetAwaiter().GetResult(); } catch (InvalidDataException) { tamper = true; }
    Check("băm sai (file bị tráo): từ chối, không giải nén", tamper);

    serveSums = "không có dòng nào hợp lệ\n";
    bool nohash = false;
    try { svc.DownloadAndStageAsync(rel!).GetAwaiter().GetResult(); } catch (InvalidDataException) { nohash = true; }
    Check("SHA256SUMS không có băm cho file: từ chối", nohash);

    Check("UpdateOptions: biến môi trường chỉ nhận localhost",
        UpdateOptions.FromEnvironment().ApiBase == UpdateOptions.DefaultApiBase);
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_UPDATE_API", "https://evil.example/api");
    Check("biến môi trường trỏ ra ngoài bị bỏ qua", UpdateOptions.FromEnvironment().ApiBase == UpdateOptions.DefaultApiBase);
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_UPDATE_API", null);

    cts.Cancel();
    server.Stop();
    Directory.Delete(tmp, true);
}

Console.WriteLine("ErrorLog");
{
    string dir = Path.Combine(Path.GetTempPath(), $"errlog_{Guid.NewGuid():N}");
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR", dir);

    ErrorLog.Write("ngữ cảnh thử", new InvalidOperationException("lỗi thử nghiệm"));
    string text = File.Exists(ErrorLog.PathOnDisk) ? File.ReadAllText(ErrorLog.PathOnDisk) : "";
    Check("ghi lỗi ra file kèm ngữ cảnh và stack", text.Contains("ngữ cảnh thử") && text.Contains("lỗi thử nghiệm"));

    File.WriteAllText(ErrorLog.PathOnDisk, new string('x', 300 * 1024));
    ErrorLog.Write("sau khi phình", new Exception("lần ghi mới"));
    Check("file quá lớn thì xoay vòng sang .old", File.Exists(ErrorLog.PathOnDisk + ".old") && new FileInfo(ErrorLog.PathOnDisk).Length < 10 * 1024);

    // thư mục đích không ghi được: không được ném ngoại lệ
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR", Path.Combine(dir, "x", "NUL:"));
    bool threw = false;
    try { ErrorLog.Write("không ghi được", new Exception("x")); } catch { threw = true; }
    Check("ghi log thất bại: không ném tiếp", !threw);

    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR", null);
    try { Directory.Delete(dir, true); } catch { }
}

Console.WriteLine("HistoryService");
{
    string dir = Path.Combine(Path.GetTempPath(), $"hist_test_{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    string desktop = Path.Combine(dir, "plan-usage-history.json");
    string cli = Path.Combine(dir, "cli-history.jsonl");

    long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
    File.WriteAllText(desktop,
        $"{{\"version\":2,\"samples\":[{{\"t\":{Ms(now.AddMinutes(-30))},\"org\":\"x\",\"u\":{{\"fh\":5,\"sd\":2}}}}]}}");
    File.WriteAllText(cli,
        $"{{\"t\":{Ms(now.AddMinutes(-20))},\"model\":\"Opus 5\",\"fh\":8,\"sd\":2,\"ctx\":40,\"sid\":\"s1\"}}\n" +
        "dòng hỏng không phải json\n" +
        $"{{\"t\":{Ms(now.AddMinutes(-10))},\"model\":null,\"fh\":null,\"sd\":2}}\n" +
        $"{{\"t\":{Ms(now.AddMinutes(-5))},\"model\":\"Sonnet 5\",\"fh\":9,\"sd\":3,\"ctx\":null,\"sid\":\"s1\"}}\n");

    var h = new HistoryService(desktop, cli);
    var all = h.Load(now.AddHours(-1));
    Check("gộp 2 nguồn, bỏ dòng hỏng/thiếu fh (3 mẫu)", all.Count == 3, $"(got {all.Count})");
    Check("sắp xếp theo thời gian", all.SequenceEqual(all.OrderBy(s => s.Time)));
    Check("mẫu Desktop không có model", all[0].Model == null && all[0].FiveHour == 5);
    Check("mẫu CLI có model + ctx + sid", all[1].Model == "Opus 5" && all[1].ContextPct == 40 && all[1].SessionId == "s1");
    Check("lọc theo mốc 'since'", h.Load(now.AddMinutes(-15)).Count == 1);

    File.AppendAllText(cli, $"{{\"t\":{Ms(now.AddMinutes(-1))},\"model\":\"Haiku 5.5\",\"fh\":10,\"sd\":3}}\n");
    Check("file đổi thì cache làm mới", h.Load(now.AddHours(-1)).Count == 4);

    Check("file không tồn tại: trả rỗng", new HistoryService(Path.Combine(dir, "nope1"), Path.Combine(dir, "nope2")).Load(now.AddDays(-1)).Count == 0);
    Directory.Delete(dir, true);
}


// ===== ClaudeUsageService: đọc cli-usage.json =====
Console.WriteLine("ClaudeUsageService");
{
    string dir = Path.Combine(Path.GetTempPath(), "cut-usage-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string prev = Environment.GetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR") ?? "";
    string prevDesktop = Environment.GetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY") ?? "";
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR", dir);
    // trỏ nguồn Desktop vào file không tồn tại để chỉ kiểm nhánh CLI
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY", Path.Combine(dir, "khong-co.json"));

    long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    long Secs(DateTime t) => new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();

    var svc = new ClaudeUsageService();
    var cfg = new AppConfig { StaleThresholdMinutes = 60 };
    var fhReset = DateTime.Now.AddHours(3);
    var sdReset = DateTime.Now.AddDays(2);

    File.WriteAllText(Path.Combine(dir, "cli-usage.json"),
        "{" +
        $"\"t\":{NowMs()}," +
        "\"fiveHour\":41,\"sevenDay\":13," +
        $"\"fiveHourResetsAt\":{Secs(fhReset)}," +
        $"\"sevenDayResetsAt\":{Secs(sdReset)}," +
        "\"costUsd\":1.25,\"model\":\"Opus 5\",\"sessionId\":\"s9\",\"contextPercent\":33}");

    var info = svc.GetUsageAsync(cfg).GetAwaiter().GetResult();
    Check("đọc được % từ CLI", info.PercentUsed == 41 && info.WeeklyPercent == 13, $"(got {info.PercentUsed}/{info.WeeklyPercent})");
    Check("đọc sevenDayResetsAt (trước đây bị bỏ không dùng)",
        info.WeeklyResetTime.HasValue && Math.Abs((info.WeeklyResetTime.Value - sdReset).TotalSeconds) < 2,
        $"(got {info.WeeklyResetTime})");
    Check("đọc costUsd (trước đây bị bỏ không dùng)", info.SessionCostUsd == 1.25, $"(got {info.SessionCostUsd})");
    Check("giờ reset 5h đúng", Math.Abs((info.ResetTime - fhReset).TotalSeconds) < 2, $"(got {info.ResetTime})");

    // Khi Desktop mới hơn CLI, ApplyCliResetTimes vẫn phải mượn được cả sevenDayResetsAt
    string deskPath = Path.Combine(dir, "plan-usage-history.json");
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY", deskPath);
    File.WriteAllText(deskPath,
        $"{{\"samples\":[{{\"t\":{NowMs() + 5000},\"u\":{{\"fh\":55,\"sd\":20}}}}]}}");
    var fromDesk = svc.GetUsageAsync(cfg).GetAwaiter().GetResult();
    Check("Desktop mới hơn vẫn mượn sevenDayResetsAt từ CLI",
        fromDesk.PercentUsed == 55 && fromDesk.WeeklyResetTime.HasValue && Math.Abs((fromDesk.WeeklyResetTime.Value - sdReset).TotalSeconds) < 2,
        $"(pct={fromDesk.PercentUsed} wr={fromDesk.WeeklyResetTime})");
    File.Delete(deskPath);

    // Không có nguồn nào: ResetTime phải là default chứ không phải Now (nếu không đồng hồ
    // đếm ngược hiện "đã reset" ngay khi mở).
    File.Delete(Path.Combine(dir, "cli-usage.json"));
    var empty = svc.GetUsageAsync(cfg).GetAwaiter().GetResult();
    Check("không có dữ liệu: IsDemo và ResetTime = default", empty.IsDemo && empty.ResetTime == default,
        $"(demo={empty.IsDemo} reset={empty.ResetTime})");
    Check("không có dữ liệu: % = 0 chứ không phải số demo 42", empty.PercentUsed == 0, $"(got {empty.PercentUsed})");

    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DIR", prev.Length == 0 ? null : prev);
    Environment.SetEnvironmentVariable("CLAUDE_USAGE_TRACKER_DESKTOP_HISTORY", prevDesktop.Length == 0 ? null : prevDesktop);
    Directory.Delete(dir, true);
}

Console.WriteLine();
Console.WriteLine($"Kết quả: {passed} pass, {failed} fail");
return failed == 0 ? 0 : 1;
