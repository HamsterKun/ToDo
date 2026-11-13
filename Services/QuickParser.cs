using System.Text.RegularExpressions;

namespace DesktopNote.Services;

/// <summary>
/// 把一行中文自然语言解析成"标题 + 时间"。
/// 支持：明天 / 后天 / 3天后 / 下周五 / 周五 9:30 / 明天9.30 / 2026-12-01 / 12月1日 / 今晚8点 …
/// 既没有日期也没有时间的输入返回 Due = null，归入"长期任务"。
/// </summary>
public static class QuickParser
{
    private const string Periods = "凌晨|早上|早晨|上午|中午|下午|傍晚|晚上|夜里|晚间";

    public static (string Title, DateTime? Due, bool AllDay) Parse(string raw, DateTime now)
    {
        var original = raw.Trim();
        if (original.Length == 0) return ("", null, false);

        var work = original;
        DateTime? date = null;
        var hasTime = false;
        var hh = 0;
        var mm = 0;
        var eveningHint = false;
        var nextDay = false;
        string? periodOnly = null;

        // ── 1. 完整日期 2026-12-01 / 2026年12月1日 ─────────────
        var m = Regex.Match(work, @"(?<!\d)(\d{4})\s*[-/.年]\s*(\d{1,2})\s*[-/.月]\s*(\d{1,2})(?!\d)\s*[日号]?");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out var y) &&
            int.TryParse(m.Groups[2].Value, out var mo) &&
            int.TryParse(m.Groups[3].Value, out var dd) &&
            IsValid(y, mo, dd))
        {
            date = new DateTime(y, mo, dd);
            work = Cut(work, m);
        }

        // ── 2. 相对日：今天 / 明天 / 后天 / 今晚 / 明晚 ─────────
        // 放在"月-日"之前，"明天9.30开会"里的"明天"才不会被 9.30 抢走
        if (date == null)
        {
            m = Regex.Match(work, @"(大后天|后天|明天|明日|明儿|今夜|今晚|明晚|今天|今日)");
            if (m.Success)
            {
                date = m.Groups[1].Value switch
                {
                    "大后天" => now.Date.AddDays(3),
                    "后天" => now.Date.AddDays(2),
                    "明天" or "明日" or "明儿" or "明晚" => now.Date.AddDays(1),
                    _ => now.Date
                };
                eveningHint = m.Groups[1].Value is "今晚" or "今夜" or "明晚";
                work = Cut(work, m);
            }
        }

        // ── 3. 月-日 12月1日 / 12-01 / 12/1 ────────────────────
        if (date == null)
        {
            var md = MatchMonthDay(work);
            if (md != null &&
                int.TryParse(md.Groups["mo"].Value, out var mo2) &&
                int.TryParse(md.Groups["d"].Value, out var dd2) &&
                IsValid(now.Year, mo2, dd2))
            {
                var cand = new DateTime(now.Year, mo2, dd2);
                if (cand.Date < now.Date) cand = cand.AddYears(1);
                date = cand;
                work = Cut(work, md);
            }
        }

        // ── 4. N天后 / N周后 ───────────────────────────────────
        if (date == null)
        {
            m = Regex.Match(work, @"(?<!\d)(\d{1,3})\s*(天|日)\s*[后後]");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n))
            {
                date = now.Date.AddDays(n);
                work = Cut(work, m);
            }
            else
            {
                m = Regex.Match(work, @"(?<!\d)(\d{1,3})\s*(周|星期|礼拜)\s*[后後]");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var w))
                {
                    date = now.Date.AddDays(w * 7);
                    work = Cut(work, m);
                }
            }
        }

        // ── 5. 周X / 下周X / 下下周X ────────────────────────────
        if (date == null)
        {
            m = Regex.Match(work, @"(下下|下|本|这)?\s*(?:周|星期|礼拜)\s*([一二三四五六日天1-7])");
            if (m.Success)
            {
                var dow = DayIndex(m.Groups[2].Value);
                if (dow > 0)
                {
                    var offset = m.Groups[1].Value switch { "下" => 1, "下下" => 2, _ => 0 };
                    date = NearestWeekday(now, dow, offset);
                    work = Cut(work, m);
                }
            }
        }

        // ── 6. 时刻：9:30 / 9点 / 下午3点半 / 明天9.30 ────────
        var period = "";
        m = Regex.Match(work, $@"({Periods})?\s*(?<![\d.])(\d{{1,2}})\s*[:：]\s*(\d{{2}})(?!\d)");
        if (!m.Success)
        {
            // "9.30" 只在已经有日期或时段词时才当作时刻，避免 "3.14元"、"v1.20" 误命中
            var dm = Regex.Match(work, $@"({Periods})?\s*(?<![\dA-Za-z.])(\d{{1,2}})\.(\d{{2}})(?![\d.%万千百元块])");
            if (dm.Success && (date != null || dm.Groups[1].Success)) m = dm;
        }
        if (m.Success && int.TryParse(m.Groups[2].Value, out var h1) && h1 <= 23 &&
            int.TryParse(m.Groups[3].Value, out var mn1) && mn1 <= 59)
        {
            hasTime = true;
            period = m.Groups[1].Value;
            (hh, nextDay) = ApplyPeriod(period, h1);
            mm = mn1;
            work = Cut(work, m);
        }
        else
        {
            m = Regex.Match(work, $@"({Periods})?\s*(?<!\d)(\d{{1,2}})\s*[点時时]\s*(半|(\d{{1,2}})\s*分?)?");
            if (m.Success && int.TryParse(m.Groups[2].Value, out var h2) && h2 <= 23)
            {
                hasTime = true;
                period = m.Groups[1].Value;
                (hh, nextDay) = ApplyPeriod(period, h2);
                mm = m.Groups[3].Value == "半" ? 30
                    : int.TryParse(m.Groups[4].Value, out var mn2) ? Math.Min(mn2, 59) : 0;
                work = Cut(work, m);
            }
        }

        // "今晚 8点" 里的 8 点是晚上八点；"今晚12点" 是次日 0 点
        if (hasTime && eveningHint && period.Length == 0)
        {
            if (hh == 12) { hh = 0; nextDay = true; }
            else if (hh < 12) hh += 12;
        }

        // ── 6.5 只有"上午/下午/晚上"没有具体点数 ──────────────
        if (!hasTime)
        {
            var pm = Regex.Match(work, $"({Periods})");
            if (pm.Success)
            {
                if (pm.Groups[1].Value is "晚上" or "夜里" or "晚间")
                {
                    // "晚上"本身就能定到 20:00
                    hasTime = true;
                    hh = 20;
                    mm = 0;
                    work = Cut(work, pm);
                }
                else
                {
                    // 其余时段不编造时刻，按全天处理，词保留在标题里不丢信息
                    periodOnly = pm.Groups[1].Value;
                }
            }
        }

        // ── 组装 ──────────────────────────────────────────────
        DateTime? due = null;
        var allDay = false;

        if (date.HasValue)
        {
            if (hasTime)
                due = date.Value.Date.AddDays(nextDay ? 1 : 0).AddHours(hh).AddMinutes(mm);
            else if (eveningHint)
                due = date.Value.Date.AddHours(20);
            else
            {
                due = date.Value.Date;
                allDay = true;
            }
        }
        else if (hasTime)
        {
            var cand = now.Date.AddDays(nextDay ? 1 : 0).AddHours(hh).AddMinutes(mm);
            due = cand <= now ? cand.AddDays(1) : cand;
        }
        else if (periodOnly != null)
        {
            // "下午开会"：今天的这个时段已经过了就算明天
            due = now.Hour >= PeriodEndHour(periodOnly) ? now.Date.AddDays(1) : now.Date;
            allDay = true;
        }
        // 否则：既没日期也没时间 → 长期任务（Due = null）

        var title = Regex.Replace(work, @"\s+", " ").Trim(' ', '，', ',', '、', '-', '—', ':', '：', '/');
        if (title.Length == 0) title = original;

        return (title, due, allDay);
    }

    /// <summary>
    /// 月-日。"12月1日" 随便用；"12-1"、"12/1" 两侧不能挨着数字、字母或小数点，
    /// 后面也不能跟单位（"3-5人"、"1/2杯" 不算日期）。不接受 "." 作分隔符（"9.30" 是时刻）。
    /// </summary>
    private static Match? MatchMonthDay(string s)
    {
        var m = Regex.Match(s, @"(?<![\d.])(?<mo>\d{1,2})\s*月\s*(?<d>\d{1,2})(?!\d)\s*[日号]?");
        if (m.Success) return m;

        m = Regex.Match(s, @"(?<![\dA-Za-z.\-/])(?<mo>\d{1,2})[-/](?<d>\d{1,2})(?![\dA-Za-z.%\-/万千百个人位次件元块杯斤公里米岁本页张台])");
        return m.Success ? m : null;
    }

    private static string Cut(string s, Match m) => s.Remove(m.Index, m.Length);

    private static bool IsValid(int y, int m, int d)
        => y >= 2000 && y <= 2100 && m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);

    /// <summary>按时段换算成 24 小时制。返回的 NextDay 表示"晚上12点"这类跨到次日 0 点的情况。</summary>
    private static (int Hour, bool NextDay) ApplyPeriod(string period, int h)
    {
        return period switch
        {
            "晚上" or "夜里" or "晚间" => h == 12 ? (0, true) : (h < 12 ? h + 12 : h, false),
            "下午" or "傍晚" => (h < 12 ? h + 12 : h, false),
            "中午" => (h < 11 ? h + 12 : h, false),
            "凌晨" => (h == 12 ? 0 : h, false),
            _ => (h, false)
        };
    }

    private static int PeriodEndHour(string period) => period switch
    {
        "凌晨" => 6,
        "早上" or "早晨" => 9,
        "上午" => 12,
        "中午" => 14,
        "下午" => 18,
        "傍晚" => 19,
        _ => 24
    };

    private static int DayIndex(string ch) => ch switch
    {
        "一" or "1" => 1,
        "二" or "2" => 2,
        "三" or "3" => 3,
        "四" or "4" => 4,
        "五" or "5" => 5,
        "六" or "6" => 6,
        "日" or "天" or "7" => 7,
        _ => 0
    };

    /// <summary>本周（offset=0，已过则顺延）/下周（1）/下下周（2）的某个星期几。</summary>
    private static DateTime NearestWeekday(DateTime now, int targetDow, int weekOffset)
    {
        var todayDow = ((int)now.DayOfWeek + 6) % 7; // 周一=0 … 周日=6
        var t = targetDow - 1;
        var diff = t - todayDow;
        if (weekOffset == 0 && diff < 0) diff += 7;
        if (weekOffset > 0) diff += 7 * weekOffset;
        return now.Date.AddDays(diff);
    }
}
