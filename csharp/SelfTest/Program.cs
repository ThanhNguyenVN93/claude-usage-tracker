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

    var off = new AlertConfig { Enabled = false };
    Check("tắt cảnh báo: im lặng", AlertEngine.Evaluate(In(99, ctx: 99), off, new AlertState()).Count == 0);
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
