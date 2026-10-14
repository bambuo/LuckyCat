using LuckyCat.Contracts;

namespace LuckyCat.Services;

/// <summary>
/// 启用指标的服务端计算（消除「模型自算」的近似误差）：按标准预热（EMA 以 SMA 播种、RSI/ATR 用 Wilder），
/// 只用**已收盘** K 线（进行中的最后一根剔除，与「行情判读 · 收盘 K 线口径」一致）；
/// 根数不足以计算某个周期时如实输出「n/a（根数不足）」，不臆造数值。
/// </summary>
public static class IndicatorCalculator
{
    /// <summary>计算所需的最少 K 线根数（含预热；含小幅余量），供拉取时加取历史数据用。</summary>
    public static int WarmupBars(IndicatorSettings settings)
    {
        var max = 0;
        if (settings.Ema.Enabled) max = Math.Max(max, settings.Ema.Periods.DefaultIfEmpty(20).Max());
        if (settings.Macd.Enabled) max = Math.Max(max, MacdWarmup(settings.Macd.Periods));
        if (settings.Rsi.Enabled) max = Math.Max(max, settings.Rsi.Periods.DefaultIfEmpty(14).Max() + 1);
        if (settings.Atr.Enabled) max = Math.Max(max, settings.Atr.Periods.DefaultIfEmpty(14).Max() + 1);
        if (settings.Boll.Enabled) max = Math.Max(max, settings.Boll.Periods.DefaultIfEmpty(20).Max());
        if (settings.Vwap.Enabled) max = Math.Max(max, 20);
        return Math.Clamp(max + 5, 20, 300);
    }

    /// <summary>单条时间框架的指标行（Label/Value 供跨周期表格对齐）；未启用任何指标时返回 null。</summary>
    public static IReadOnlyList<IndicatorRow>? Render(IndicatorSettings settings, IReadOnlyList<MarketKline> allBars, string timeframe, DateTime? asOfUtc = null)
    {
        var bars = ClosedBars(allBars, timeframe, asOfUtc);
        if (bars.Count < 2) return null;

        var closes = bars.Select(k => k.Close).ToArray();
        // 指标值精度跟随 K 线的实际小数位（交易所精度），避免出现比数据本身还多的小数噪音
        var decimals = DetectDecimals(bars);
        string P(decimal value) => $"{Math.Round(value, decimals):0.########}";
        var parts = new List<IndicatorRow>();

        if (settings.Ema.Enabled)
        {
            foreach (var period in settings.Ema.Periods.Where(p => p > 0).Distinct())
            {
                var value = Ema(closes, period);
                if (value is { } v)
                {
                    parts.Add(new($"EMA{period}", $"{P(v)}{Direction(closes.Length > 1 ? Ema(closes[..^1], period) : null, v)}"));
                }
                else
                {
                    parts.Add(new($"EMA{period}", Insufficient(period, bars.Count)));
                }
            }
        }

        if (settings.Macd.Enabled)
        {
            var (fast, slow, signal) = MacdPeriods(settings.Macd.Periods);
            var macd = Macd(closes, fast, slow, signal);
            if (macd is { } m)
            {
                var histogram = m.Dif - m.Dea;
                var trend = "";
                if (closes.Length > 1 && Macd(closes[..^1], fast, slow, signal) is { } previous)
                {
                    var previousHistogram = previous.Dif - previous.Dea;
                    trend = Math.Abs(histogram) < Math.Abs(previousHistogram) ? "（收窄）"
                        : Math.Abs(histogram) > Math.Abs(previousHistogram) ? "（放大）"
                        : "（持平）";
                }
                parts.Add(new("MACD", $"DIF {P(m.Dif)} / DEA {P(m.Dea)} / 柱 {P(histogram)}{trend}"));
            }
            else
            {
                parts.Add(new("MACD", Insufficient(MacdWarmup(settings.Macd.Periods), bars.Count)));
            }
        }

        if (settings.Rsi.Enabled)
        {
            foreach (var period in settings.Rsi.Periods.Where(p => p > 1).Distinct())
            {
                var value = Rsi(closes, period);
                parts.Add(new($"RSI{period}", value is { } v ? $"{v:0.#}" : Insufficient(period + 1, bars.Count)));
            }
        }

        if (settings.Atr.Enabled)
        {
            foreach (var period in settings.Atr.Periods.Where(p => p > 0).Distinct())
            {
                var value = Atr(bars, period);
                parts.Add(new($"ATR{period}", value is { } v ? P(v) : Insufficient(period + 1, bars.Count)));
            }
        }

        if (settings.Boll.Enabled)
        {
            foreach (var period in settings.Boll.Periods.Where(p => p > 1).Distinct())
            {
                var boll = Boll(closes, period);
                parts.Add(new($"BOLL{period}", boll is { } b
                    ? $"中 {P(b.Middle)} / 上 {P(b.Upper)} / 下 {P(b.Lower)}"
                    : Insufficient(period, bars.Count)));
            }
        }

        if (settings.Vwap.Enabled)
        {
            var vwap = WindowVwap(bars);
            parts.Add(new("VWAP（窗口）", vwap is { } v ? P(v) : "n/a"));
        }

        return parts.Count > 0 ? parts : null;
    }

    public sealed record IndicatorRow(string Label, string Value);

    public sealed record SignalValues(IReadOnlyList<MarketKline> Bars, decimal? EmaFast, decimal? EmaSlow,
        decimal? Histogram, decimal? Rsi, decimal? Atr);

    public static SignalValues CalculateSignalValues(IndicatorSettings settings, IReadOnlyList<MarketKline> allBars,
        string timeframe, DateTime asOfUtc)
    {
        var bars = ClosedBars(allBars, timeframe, asOfUtc);
        var closes = bars.Select(b => b.Close).ToArray();
        var ema = settings.Ema.Periods.Where(p => p > 0).Distinct().Order().ToList();
        var (fast, slow, signal) = MacdPeriods(settings.Macd.Periods);
        var macd = settings.Macd.Enabled ? Macd(closes, fast, slow, signal) : null;
        var rsiPeriod = settings.Rsi.Periods.Where(p => p > 1).DefaultIfEmpty(0).Min();
        var atrPeriod = settings.Atr.Periods.Where(p => p > 0).DefaultIfEmpty(0).Min();
        return new(bars,
            settings.Ema.Enabled && ema.Count >= 2 ? Ema(closes, ema[0]) : null,
            settings.Ema.Enabled && ema.Count >= 2 ? Ema(closes, ema[^1]) : null,
            macd is { } m ? m.Dif - m.Dea : null,
            settings.Rsi.Enabled && rsiPeriod > 0 ? Rsi(closes, rsiPeriod) : null,
            settings.Atr.Enabled && atrPeriod > 0 ? Atr(bars, atrPeriod) : null);
    }

    private static string Insufficient(int need, int have) => $"n/a（需 {need} 根/现 {have} 根）";

    // 较前一根的变化方向（↑/↓/→）；前值不可得时为空
    private static string Direction(decimal? previous, decimal current)
    {
        if (previous is not { } p) return "";
        if (current > p) return " ↑";
        if (current < p) return " ↓";
        return " →";
    }

    // 交易所精度：取可见 K 线的最大小数位（decimal 尾数标度），夹在 2–10 之间
    private static int DetectDecimals(IReadOnlyList<MarketKline> bars)
    {
        var max = 2;
        foreach (var bar in bars)
        {
            max = Math.Max(max, Math.Max(ScaleOf(bar.Close), Math.Max(ScaleOf(bar.High), ScaleOf(bar.Low))));
        }
        return Math.Clamp(max, 2, 10);
    }

    private static int ScaleOf(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0xFF;

    // 剔除进行中的最后一根（开盘时间 + 周期 > 当前时间）
    public static List<MarketKline> ClosedBars(IReadOnlyList<MarketKline> bars, string timeframe, DateTime? asOfUtc = null)
    {
        var list = bars.ToList();
        if (list.Count == 0) return list;
        var interval = TryParseInterval(timeframe);
        return interval is { } span ? list.Where(k => k.OpenTime + span <= (asOfUtc ?? DateTime.UtcNow)).ToList() : [];
    }

    public static TimeSpan? TryParseInterval(string timeframe)
    {
        if (string.IsNullOrWhiteSpace(timeframe)) return null;
        var unit = timeframe[^1];
        if (!int.TryParse(timeframe[..^1], out var n) || n <= 0) return null;
        return unit switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'D' or 'd' => TimeSpan.FromDays(n),
            'W' or 'w' => TimeSpan.FromDays(n * 7),
            _ => null,
        };
    }

    // EMA：以首个 n 根的 SMA 播种；根数不足返回 null
    private static decimal? Ema(decimal[] values, int period)
    {
        if (period <= 0 || values.Length < period) return null;
        var alpha = 2m / (period + 1);
        var ema = values.Take(period).Average();
        for (var i = period; i < values.Length; i++)
        {
            ema = alpha * values[i] + (1 - alpha) * ema;
        }
        return ema;
    }

    private static (int Fast, int Slow, int Signal) MacdPeriods(IReadOnlyList<int> configured) =>
        configured.Count >= 3
            ? (configured[0], configured[1], configured[2])
            : (12, 26, 9);

    private static int MacdWarmup(IReadOnlyList<int> configured)
    {
        var (fast, slow, signal) = MacdPeriods(configured);
        return slow + signal - 1;
    }

    // MACD：DIF = EMA(fast) − EMA(slow)，DEA = EMA(signal) of DIF；缓存逐根 EMA 序列以便对 DIF 序列再做 EMA
    private static (decimal Dif, decimal Dea)? Macd(decimal[] closes, int fast, int slow, int signal)
    {
        var need = slow + signal - 1;
        if (closes.Length < need) return null;
        var fastSeries = EmaSeries(closes, fast);
        var slowSeries = EmaSeries(closes, slow);
        var dif = new decimal[closes.Length];
        for (var i = 0; i < closes.Length; i++)
        {
            dif[i] = fastSeries[i] - slowSeries[i];
        }
        // DIF 的 EMA(signal)：DIF 自 slow−1 起有效，把无效段剔除后再做 EMA（避免用 0 播种）
        var validDif = dif.Skip(slow - 1).ToArray();
        var dea = Ema(validDif, signal);
        if (validDif.Length < signal || dea is null) return null;
        return (dif[^1], dea.Value);
    }

    // 逐根 EMA 序列：前 period−1 根用截至目前（不足播种数时以可用均值）近似，避免中间断裂
    private static decimal[] EmaSeries(decimal[] values, int period)
    {
        var result = new decimal[values.Length];
        var alpha = 2m / (period + 1);
        var seedCount = Math.Min(period, values.Length);
        var ema = values.Take(seedCount).Average();
        for (var i = 0; i < seedCount; i++) result[i] = ema;
        for (var i = seedCount; i < values.Length; i++)
        {
            ema = alpha * values[i] + (1 - alpha) * ema;
            result[i] = ema;
        }
        return result;
    }

    // RSI（Wilder）：需 period+1 根收盘价
    private static decimal? Rsi(decimal[] closes, int period)
    {
        if (closes.Length < period + 1) return null;
        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var delta = closes[i] - closes[i - 1];
            if (delta >= 0) gain += delta; else loss -= delta;
        }
        var avgGain = gain / period;
        var avgLoss = loss / period;
        for (var i = period + 1; i < closes.Length; i++)
        {
            var delta = closes[i] - closes[i - 1];
            var up = delta > 0 ? delta : 0m;
            var down = delta < 0 ? -delta : 0m;
            avgGain = (avgGain * (period - 1) + up) / period;
            avgLoss = (avgLoss * (period - 1) + down) / period;
        }
        if (avgLoss == 0) return 100m;
        var rs = avgGain / avgLoss;
        return 100m - 100m / (1 + rs);
    }

    // ATR（Wilder）：TR = max(H−L, |H−prevC|, |L−prevC|)
    private static decimal? Atr(IReadOnlyList<MarketKline> bars, int period)
    {
        if (bars.Count < period + 1) return null;
        var trs = new List<decimal>();
        for (var i = 1; i < bars.Count; i++)
        {
            var high = bars[i].High;
            var low = bars[i].Low;
            var prevClose = bars[i - 1].Close;
            trs.Add(Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose))));
        }
        var atr = trs.Take(period).Average();
        for (var i = period; i < trs.Count; i++)
        {
            atr = (atr * (period - 1) + trs[i]) / period;
        }
        return atr;
    }

    // BOLL：中轨 SMA，上下轨 ±2 倍总体标准差
    private static (decimal Middle, decimal Upper, decimal Lower)? Boll(decimal[] closes, int period)
    {
        if (closes.Length < period) return null;
        var window = closes.TakeLast(period).ToArray();
        var middle = window.Average();
        var variance = window.Select(c => (c - middle) * (c - middle)).Average();
        var sigma = (decimal)Math.Sqrt((double)variance);
        return (middle, middle + 2 * sigma, middle - 2 * sigma);
    }

    // VWAP（窗口口径）：以可见已收盘 K 线的典型价加权（非交易时段累计口径，如实标注）
    private static decimal? WindowVwap(IReadOnlyList<MarketKline> bars)
    {
        decimal volSum = 0, pvSum = 0;
        foreach (var bar in bars)
        {
            var typical = (bar.High + bar.Low + bar.Close) / 3m;
            volSum += bar.Volume;
            pvSum += typical * bar.Volume;
        }
        return volSum > 0 ? pvSum / volSum : null;
    }
}
