#!/usr/bin/env node
// Claude Code statusLine script: hiện model + usage 5h/7 ngày + ngữ cảnh, đồng thời ghi
// dữ liệu ra file cho Claude Usage Tracker (app tray) đọc. Không gọi thêm API nào —
// chỉ dùng dữ liệu Claude Code đã gửi kèm theo mỗi lần render.
//
// Cài đặt: copy file này vào ~/.claude/statusline.js và đăng ký trong settings.json:
//   "statusLine": { "type": "command", "command": "node \"<đường dẫn>/statusline.js\"",
//                   "padding": 1, "refreshInterval": 60 }

const fs = require('fs');
const os = require('os');
const path = require('path');

const HISTORY_MIN_GAP_MS = 60 * 1000;

let input = '';
process.stdin.on('data', (chunk) => { input += chunk; });
process.stdin.on('end', () => {
  let data = {};
  try { data = JSON.parse(input); } catch (e) { data = {}; }

  const rl = data.rate_limits || {};
  const fh = rl.five_hour || {};
  const sd = rl.seven_day || {};
  const sl = rl.spend_limit || {};
  const ctx = data.context_window || {};

  try {
    const outDir = process.env.CLAUDE_USAGE_TRACKER_DIR
      || path.join(process.env.APPDATA || os.homedir(), 'ClaudeUsageTracker');
    fs.mkdirSync(outDir, { recursive: true });
    const outFile = path.join(outDir, 'cli-usage.json');
    const historyFile = path.join(outDir, 'cli-history.jsonl');

    const payload = {
      t: Date.now(),
      model: (data.model && data.model.display_name) || null,
      fiveHour: fh.used_percentage ?? null,
      fiveHourResetsAt: fh.resets_at ?? null,
      sevenDay: sd.used_percentage ?? null,
      sevenDayResetsAt: sd.resets_at ?? null,
      extraUsagePercent: sl.used_percentage ?? null,
      extraUsageUsd: sl.used_usd ?? null,
      extraUsageLimitUsd: sl.limit_usd ?? null,
      contextPercent: ctx.used_percentage ?? null,
      sessionId: data.session_id ?? null,
      costUsd: (data.cost && data.cost.total_cost_usd) ?? null,
    };

    // Phiên vừa mở chưa có rate_limits (chưa có phản hồi API đầu tiên): giữ nguyên
    // file cũ thay vì ghi đè bằng null, kẻo mất giờ reset đang dùng.
    if (payload.fiveHour != null) {
      // Chỉ thêm vào lịch sử khi giá trị đổi (hoặc đã quá 60s), để file không phình
      // ra vì refreshInterval.
      let prev = null;
      try { prev = JSON.parse(fs.readFileSync(outFile, 'utf8')); } catch (e) { prev = null; }

      fs.writeFileSync(outFile, JSON.stringify(payload));

      const same = prev
        && prev.model === payload.model
        && prev.fiveHour === payload.fiveHour
        && prev.sevenDay === payload.sevenDay
        && prev.contextPercent === payload.contextPercent
        && prev.sessionId === payload.sessionId;
      const recent = prev && (payload.t - prev.t) < HISTORY_MIN_GAP_MS;

      if (!(same && recent)) {
        const line = JSON.stringify({
          t: payload.t,
          model: payload.model,
          fh: payload.fiveHour,
          sd: payload.sevenDay,
          ctx: payload.contextPercent,
          sid: payload.sessionId,
        });
        fs.appendFileSync(historyFile, line + '\n');
      }
    }
  } catch (e) {
    // không để lỗi ghi file làm hỏng statusline hiển thị
  }

  const modelName = (data.model && data.model.display_name) || 'Claude';
  const fhText = fh.used_percentage != null ? `${Math.round(fh.used_percentage)}%` : '--';
  const sdText = sd.used_percentage != null ? `${Math.round(sd.used_percentage)}%` : '--';
  const ctxText = ctx.used_percentage != null ? ` ctx:${Math.round(ctx.used_percentage)}%` : '';
  process.stdout.write(`${modelName} | 5h:${fhText} 7d:${sdText}${ctxText}`);
});
