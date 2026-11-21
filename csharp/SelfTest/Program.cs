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

Console.WriteLine();
Console.WriteLine($"Kết quả: {passed} pass, {failed} fail");
return failed == 0 ? 0 : 1;
