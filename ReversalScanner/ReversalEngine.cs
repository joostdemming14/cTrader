using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Reversal setup direction returned by the engine.
    /// </summary>
    public enum ReversalDirection
    {
        None = 0,
        Long = 1,
        Short = 2
    }

    /// <summary>
    /// Permitted scan directions for the Reversal / Continuation scanner cBots.
    /// </summary>
    public enum ReversalScanDirection
    {
        Both,
        LongOnly,
        ShortOnly
    }

    /// <summary>
    /// Automatic scan scheduling modes for the scanners.
    /// </summary>
    public enum ReversalScheduleMode
    {
        /// <summary>Scans once per day, 2 seconds after the configured daily close hour (ET).</summary>
        DailyAfterClose,

        /// <summary>Scans at the top of every hour, 2 seconds after the hour (XX:00:02 UTC).</summary>
        Hourly,

        /// <summary>Scans at every 15-minute mark, 2 seconds after the mark.</summary>
        Every15Minutes,

        /// <summary>Manual only: scans on startup and when the on-chart button is clicked.</summary>
        ManualOnly,

        /// <summary>Scans using a custom interval defined by ScanIntervalSeconds.</summary>
        CustomInterval
    }

    /// <summary>
    /// Result of a Reversal setup evaluation, optionally confirmed by the next closed bar.
    /// </summary>
    public readonly struct ReversalSetupResult
    {
        /// <summary>True when the reversal signal and optional next-bar confirmation passed.</summary>
        public readonly bool IsTriggered;

        /// <summary>Long, Short, or None.</summary>
        public readonly ReversalDirection Direction;

        /// <summary>Legacy structural index, unused when no structural lookback is configured.</summary>
        public readonly int ExtremeIndex;

        /// <summary>Legacy structural level, unused when no structural lookback is configured.</summary>
        public readonly double Level;

        /// <summary>Legacy retest index retained for result compatibility.</summary>
        public readonly int RetestIndex;

        /// <summary>Index of the trigger bar (Step 3) being evaluated.</summary>
        public readonly int TriggerIndex;

        // Trigger-bar snapshot values (for alerting / HUD).
        public readonly double Close;
        public readonly double High;
        public readonly double Low;
        public readonly double Clv;

        /// <summary>TSI on the divergence bar (the bar that made the fresh price extreme).</summary>
        public readonly double Tsi;

        /// <summary>TSI signal line on the signal bar (display only, not part of the trigger).</summary>
        public readonly double TsiSig;

        /// <summary>TSI on the reference extreme bar (the prior swing used for the divergence).</summary>
        public readonly double RefTsi;

        /// <summary>EMA(21) on the signal bar, reference level for the HUD only.</summary>
        public readonly double Ema21;
        public readonly double Atr;

        /// <summary>Legacy structural distance, unused when no structural lookback is configured.</summary>
        public readonly double DistanceAtr;

        /// <summary>Human-readable reason when the setup did not trigger.</summary>
        public readonly string RejectReason;

        public ReversalSetupResult(bool isTriggered, ReversalDirection direction,
            int extremeIndex, double level, int retestIndex, int triggerIndex,
            double close, double high, double low, double clv, double tsi, double tsiSig,
            double refTsi, double ema21, double atr, double distanceAtr, string rejectReason)
        {
            IsTriggered = isTriggered;
            Direction = direction;
            ExtremeIndex = extremeIndex;
            Level = level;
            RetestIndex = retestIndex;
            TriggerIndex = triggerIndex;
            Close = close;
            High = high;
            Low = low;
            Clv = clv;
            Tsi = tsi;
            TsiSig = tsiSig;
            RefTsi = refTsi;
            Ema21 = ema21;
            Atr = atr;
            DistanceAtr = distanceAtr;
            RejectReason = rejectReason ?? "";
        }

        /// <summary>Constructs a non-triggered result with a reject reason.</summary>
        public static ReversalSetupResult Reject(ReversalDirection direction, string reason) =>
            new ReversalSetupResult(false, direction, -1, double.NaN, -1, -1,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, reason);
    }

    /// <summary>
    /// Pure C# engine for the Reversal scanner strategy. Zero dependencies on cAlgo APIs —
    /// 100% unit testable, reproducible, and deterministic.
    ///
    /// Final trigger logic used by this scanner (TSI divergence, no pivot waiting):
    ///   TSI    = 100 * EMA(short, EMA(long, PC)) / EMA(short, EMA(long, |PC|))   (Blau; defaults 25/13)
    ///   CLV    = (2*Close - High - Low) / (High - Low)   range -1..+1
    ///   Lookback = divergence window (parameter; default 20)
    ///
    /// Short Reversal = two steps, both derived from trailing windows (deterministic,
    /// full-recalc safe):
    ///   1. DIVERGENCE DETECTION: some bar d in the last `triggerWindow` bars (including the
    ///      signal bar) made the highest High of its full lookback window, with
    ///      TSI[d] <= TSI[reference] - minTsiDrop. The reference is the highest High of the prior
    ///      window, at least minGap bars back, and the REFERENCE TSI must exceed +tsiLevel (the
    ///      prior move was genuinely strong; the divergence bar's own TSI is unconstrained).
    ///      No higher High is allowed between d and the signal bar (otherwise stale).
    ///   2. TRIGGER: the signal bar closes weakly (CLV <= clvShortMax). The divergence bar and
    ///      the trigger bar may be the same bar. Trend filter and gates still apply.
    ///
    /// Long Reversal is the mirror: lowest Low of the full lookback window with
    /// TSI[d] >= TSI[reference] + minTsiDrop and reference TSI < -tsiLevel, then a strong
    /// close (CLV >= clvLongMin).
    ///
    /// Next-bar confirmation is optional (default off). The scanner does not compute SL/PT
    /// (alert-only).
    /// </summary>
    public static class ReversalEngine
    {
        // =========================================================================
        // --- 1. INDICATOR HELPERS (match repo conventions: Wilder RSI/ATR, EMA seed-first-value) ---
        // =========================================================================

        /// <summary>
        /// Simple Moving Average. NaN until (period - 1); then sliding window mean.
        /// Any NaN inside a window yields NaN for that bar.
        /// </summary>
        public static double[] ComputeSma(IReadOnlyList<double> source, int period)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 1");

            int n = source.Count;
            double[] result = new double[n];
            for (int i = 0; i < n; i++) result[i] = double.NaN;
            if (n < period) return result;

            double sum = 0.0;
            int invalidCount = 0;
            for (int i = 0; i < n; i++)
            {
                double value = source[i];
                if (double.IsNaN(value)) invalidCount++;
                else sum += value;

                if (i >= period)
                {
                    double leaving = source[i - period];
                    if (double.IsNaN(leaving)) invalidCount--;
                    else sum -= leaving;
                }

                if (i >= period - 1)
                    result[i] = invalidCount == 0 ? sum / period : double.NaN;
            }
            return result;
        }

        /// <summary>
        /// Exponential Moving Average matching cTrader / TradingView Pine v6:
        /// seeds with the first valid non-NaN value, then y[t] = alpha*x[t] + (1-alpha)*y[t-1],
        /// alpha = 2 / (period + 1). Leading NaNs are skipped (NaN propagated until first valid bar).
        /// </summary>
        public static double[] ComputeEma(IReadOnlyList<double> source, int period)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 1");

            int n = source.Count;
            double[] result = new double[n];
            for (int i = 0; i < n; i++) result[i] = double.NaN;
            if (n == 0) return result;

            double alpha = 2.0 / (period + 1.0);
            double prev = double.NaN;
            for (int i = 0; i < n; i++)
            {
                double val = source[i];
                if (double.IsNaN(val)) { result[i] = double.NaN; continue; }
                prev = double.IsNaN(prev) ? val : (val * alpha) + (prev * (1.0 - alpha));
                result[i] = prev;
            }
            return result;
        }

        /// <summary>
        /// Average True Range with Wilder's smoothing. NaN before (period - 1).
        /// </summary>
        public static double[] ComputeAtr(IReadOnlyList<double> highs, IReadOnlyList<double> lows, IReadOnlyList<double> closes, int period)
        {
            if (highs == null || lows == null || closes == null)
                throw new ArgumentNullException("arrays must not be null");
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "must be >= 1");

            int n = closes.Count;
            double[] result = new double[n];
            for (int i = 0; i < n; i++) result[i] = double.NaN;
            if (n < period) return result;

            double[] tr = new double[n];
            tr[0] = highs[0] - lows[0];
            for (int i = 1; i < n; i++)
            {
                double hl = highs[i] - lows[i];
                double hc = Math.Abs(highs[i] - closes[i - 1]);
                double lc = Math.Abs(lows[i] - closes[i - 1]);
                tr[i] = Math.Max(hl, Math.Max(hc, lc));
            }

            double sumTr = 0.0;
            for (int i = 0; i < period; i++) sumTr += tr[i];
            result[period - 1] = sumTr / period;
            for (int i = period; i < n; i++)
                result[i] = (result[i - 1] * (period - 1.0) + tr[i]) / period;
            return result;
        }

        /// <summary>
        /// Percentage Price Oscillator (PPO) and signal line.
        ///   PPO    = 100 * (EMA(fast, Close) - EMA(slow, Close)) / EMA(slow, Close)
        ///   PPOsig = EMA(signal, PPO)
        /// Returns NaN during warmup.
        /// </summary>
        public static (double[] Ppo, double[] PpoSig) ComputePpo(IReadOnlyList<double> closes, int fastPeriod, int slowPeriod, int signalPeriod)
        {
            if (closes == null) throw new ArgumentNullException(nameof(closes));
            if (fastPeriod < 1) throw new ArgumentOutOfRangeException(nameof(fastPeriod));
            if (slowPeriod <= fastPeriod) throw new ArgumentException("Slow period must be greater than fast period", nameof(slowPeriod));
            if (signalPeriod < 1) throw new ArgumentOutOfRangeException(nameof(signalPeriod));

            int n = closes.Count;
            double[] ppo = new double[n];
            double[] ppoSig = new double[n];
            for (int i = 0; i < n; i++) { ppo[i] = double.NaN; ppoSig[i] = double.NaN; }
            if (n < slowPeriod + 1) return (ppo, ppoSig);

            double[] fastMa = ComputeEma(closes, fastPeriod);
            double[] slowMa = ComputeEma(closes, slowPeriod);

            for (int i = 0; i < n; i++)
            {
                double f = fastMa[i], s = slowMa[i];
                if (double.IsNaN(f) || double.IsNaN(s) || Math.Abs(s) < 1e-12) { ppo[i] = double.NaN; continue; }
                ppo[i] = 100.0 * (f - s) / s;
            }

            ppoSig = ComputeEma(ppo, signalPeriod);
            return (ppo, ppoSig);
        }

        /// <summary>
        /// True Strength Index (William Blau, standard formula):
        ///   PC      = Close - Close[1]            (price change; PC[0] = 0 by convention)
        ///   TSI     = 100 * EMA(short, EMA(long, PC)) / EMA(short, EMA(long, |PC|))
        ///   TSIsig  = EMA(signal, TSI)
        /// Defaults: long = 25, short = 13, signal = 13. The TSI value is bounded to
        /// (-100, +100); warmup bars and a zero smoothed-|PC| denominator yield double.NaN.
        /// </summary>
        public static (double[] Tsi, double[] TsiSig) ComputeTsi(IReadOnlyList<double> closes, int longPeriod, int shortPeriod, int signalPeriod)
        {
            if (closes == null) throw new ArgumentNullException(nameof(closes));
            if (longPeriod < 1) throw new ArgumentOutOfRangeException(nameof(longPeriod));
            if (shortPeriod < 1) throw new ArgumentOutOfRangeException(nameof(shortPeriod));
            if (signalPeriod < 1) throw new ArgumentOutOfRangeException(nameof(signalPeriod));

            int n = closes.Count;
            double[] tsi = new double[n];
            double[] tsiSig = new double[n];
            for (int i = 0; i < n; i++) { tsi[i] = double.NaN; tsiSig[i] = double.NaN; }
            if (n < 2) return (tsi, tsiSig);

            double[] pc = new double[n];
            double[] absPc = new double[n];
            pc[0] = 0.0;
            absPc[0] = 0.0;
            for (int i = 1; i < n; i++)
            {
                if (double.IsNaN(closes[i]) || double.IsNaN(closes[i - 1]))
                {
                    pc[i] = double.NaN;
                    absPc[i] = double.NaN;
                }
                else
                {
                    pc[i] = closes[i] - closes[i - 1];
                    absPc[i] = Math.Abs(pc[i]);
                }
            }

            double[] num = ComputeEma(ComputeEma(pc, longPeriod), shortPeriod);
            double[] den = ComputeEma(ComputeEma(absPc, longPeriod), shortPeriod);

            for (int i = 0; i < n; i++)
            {
                double nu = num[i], de = den[i];
                if (double.IsNaN(nu) || double.IsNaN(de) || de <= 1e-12)
                    continue;
                tsi[i] = 100.0 * nu / de;
            }

            tsiSig = ComputeEma(tsi, signalPeriod);
            return (tsi, tsiSig);
        }

        // =========================================================================
        // --- 2. REVERSAL SETUP EVALUATION ---
        // =========================================================================

        /// <summary>
        /// Evaluates a Short Reversal (TSI bearish divergence + weak close) at <paramref name="evalIndex"/>.
        /// Step 1 — divergence detection: some bar d in the last <paramref name="triggerWindow"/> bars
        /// (including the signal bar itself) made the highest High of its full lookback window, and sat
        /// at least <paramref name="minTsiDrop"/> below the TSI at the reference extreme (the highest
        /// High of the prior window, at least <paramref name="minGap"/> bars back). The REFERENCE TSI
        /// must exceed <paramref name="tsiLevel"/> (the prior move was genuinely strong); the divergence
        /// bar's own TSI is unconstrained. No higher High since (stale invalidation).
        /// Step 2 — the trigger: the signal bar closes weakly (CLV &lt;= clvMax). The divergence bar
        /// and the trigger bar may be the same bar. Trend filter and <paramref name="spyShortOk"/> apply.
        /// </summary>
        public static ReversalSetupResult EvaluateShortReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvMax,
            bool requireSma200Filter,
            bool requireConfirmation, bool spyShortOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr, evalIndex))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "Null or out-of-range input");

            int t = evalIndex;
            if (t < (requireConfirmation ? 2 : 1))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "No prior bar for the divergence window");

            int signal = requireConfirmation ? t - 1 : t;

            // Step 1 — most recent qualifying divergence bar d in [signal - triggerWindow, signal].
            int divIdx = -1, refIdx = -1;
            double refHigh = double.NaN, divTsi = double.NaN, refTsi = double.NaN;
            for (int d = signal; d >= signal - triggerWindow && d >= 0; d--)
            {
                int windowStart = d - lookback;
                if (windowStart < 0) break;

                int rIdx = -1;
                double rHigh = double.NaN;
                for (int i = windowStart; i < d; i++)
                {
                    if (rIdx < 0 || highs[i] > rHigh)
                    {
                        rIdx = i;
                        rHigh = highs[i];
                    }
                }
                if (rIdx < 0 || d - rIdx < minGap || double.IsNaN(tsi[d]) || double.IsNaN(tsi[rIdx]))
                    continue;

                if (highs[d] > rHigh && tsi[rIdx] > tsiLevel && tsi[d] <= tsi[rIdx] - minTsiDrop)
                {
                    bool stale = false;
                    for (int i = d + 1; i <= signal; i++)
                    {
                        if (highs[i] > highs[d]) { stale = true; break; }
                    }
                    if (stale) continue;

                    divIdx = d; refIdx = rIdx; refHigh = rHigh; divTsi = tsi[d]; refTsi = tsi[rIdx];
                    break;
                }
            }
            if (divIdx < 0)
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    "No TSI bearish divergence in the trigger window");

            // Step 2 — the trigger: weak close on the signal bar.
            double close = closes[signal], high = highs[signal], low = lows[signal];
            double tsiSigT = tsiSig[signal];
            double ema = ema21[signal], sma = sma200[signal], atrT = atr[signal];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(tsiSigT) ||
                double.IsNaN(ema) || double.IsNaN(sma) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "Signal-bar indicator NaN/invalid");

            if (!(clv <= clvMax))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"CLV {clv:F2} not <= {clvMax:F2} (no weak close trigger)");
            if (requireSma200Filter && !(close < sma))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} not < SMA200 {sma:F4} (short reversal trend filter)");
            if (requireConfirmation && !(closes[t] < lows[signal]))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"Confirmation close {closes[t]:F4} not < signal-bar low {lows[signal]:F4}");
            if (!spyShortOk)
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    "SPY benchmark gate failed (SPY not < SPY_SMA50)");

            return new ReversalSetupResult(true, ReversalDirection.Short,
                refIdx, refHigh, -1, t, close, high, low, clv, divTsi, tsiSigT,
                refTsi, ema, atrT, double.NaN, "Short Reversal triggered (TSI divergence + weak close)");
        }

        /// <summary>
        /// Evaluates a Long Reversal (TSI bullish divergence + strong close) at <paramref name="evalIndex"/>.
        /// Mirror of <see cref="EvaluateShortReversal"/>: a bar in the last <paramref name="triggerWindow"/>
        /// bars made the lowest Low of its full lookback window and sits at least
        /// <paramref name="minTsiDrop"/> above the TSI at the reference extreme (the lowest Low of the
        /// prior window, at least <paramref name="minGap"/> bars back), whose TSI must be below
        /// -<paramref name="tsiLevel"/> (no lower Low since). The trigger is the strong close on the
        /// signal bar: CLV &gt;= clvMin (may be the divergence bar itself).
        /// </summary>
        public static ReversalSetupResult EvaluateLongReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvMin,
            bool requireSma200Filter,
            bool requireConfirmation, bool spyLongOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr, evalIndex))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "Null or out-of-range input");

            int t = evalIndex;
            if (t < (requireConfirmation ? 2 : 1))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "No prior bar for the divergence window");

            int signal = requireConfirmation ? t - 1 : t;

            // Step 1 — most recent qualifying divergence bar d in [signal - triggerWindow, signal].
            int divIdx = -1, refIdx = -1;
            double refLow = double.NaN, divTsi = double.NaN, refTsi = double.NaN;
            for (int d = signal; d >= signal - triggerWindow && d >= 0; d--)
            {
                int windowStart = d - lookback;
                if (windowStart < 0) break;

                int rIdx = -1;
                double rLow = double.NaN;
                for (int i = windowStart; i < d; i++)
                {
                    if (rIdx < 0 || lows[i] < rLow)
                    {
                        rIdx = i;
                        rLow = lows[i];
                    }
                }
                if (rIdx < 0 || d - rIdx < minGap || double.IsNaN(tsi[d]) || double.IsNaN(tsi[rIdx]))
                    continue;

                if (lows[d] < rLow && tsi[rIdx] < -tsiLevel && tsi[d] >= tsi[rIdx] + minTsiDrop)
                {
                    bool stale = false;
                    for (int i = d + 1; i <= signal; i++)
                    {
                        if (lows[i] < lows[d]) { stale = true; break; }
                    }
                    if (stale) continue;

                    divIdx = d; refIdx = rIdx; refLow = rLow; divTsi = tsi[d]; refTsi = tsi[rIdx];
                    break;
                }
            }
            if (divIdx < 0)
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    "No TSI bullish divergence in the trigger window");

            // Step 2 — the trigger: strong close on the signal bar.
            double close = closes[signal], high = highs[signal], low = lows[signal];
            double tsiSigT = tsiSig[signal];
            double ema = ema21[signal], sma = sma200[signal], atrT = atr[signal];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(tsiSigT) ||
                double.IsNaN(ema) || double.IsNaN(sma) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "Signal-bar indicator NaN/invalid");

            if (!(clv >= clvMin))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"CLV {clv:F2} not >= {clvMin:F2} (no strong close trigger)");
            if (requireSma200Filter && !(close > sma))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} not > SMA200 {sma:F4} (long reversal trend filter)");
            if (requireConfirmation && !(closes[t] > highs[signal]))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"Confirmation close {closes[t]:F4} not > signal-bar high {highs[signal]:F4}");
            if (!spyLongOk)
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    "SPY benchmark gate failed (SPY not > SPY_SMA50)");

            return new ReversalSetupResult(true, ReversalDirection.Long,
                refIdx, refLow, -1, t, close, high, low, clv, divTsi, tsiSigT,
                refTsi, ema, atrT, double.NaN, "Long Reversal triggered (TSI divergence + strong close)");
        }

        /// <summary>
        /// Evaluates both directions and returns the triggered setup, if any.
        /// Long and Short are mutually exclusive in practice (a bar cannot be both a fresh
        /// lookback High and a fresh lookback Low with the required TSI extremes). When
        /// <paramref name="direction"/> restricts to one side, only that side is evaluated.
        /// <paramref name="spyLongOk"/>/<paramref name="spyShortOk"/> carry the SPY-vs-SPY_SMA50
        /// benchmark gate (computed by the cBot, not the pure engine).
        /// </summary>
        public static ReversalSetupResult Evaluate(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            ReversalScanDirection direction,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvShortMax, double clvLongMin,
            bool requireSma200Filter,
            bool requireConfirmation,
            bool spyLongOk, bool spyShortOk)
        {
            ReversalSetupResult res = ReversalSetupResult.Reject(ReversalDirection.None, "Not evaluated");

            if (direction != ReversalScanDirection.ShortOnly)
            {
                var lon = EvaluateLongReversal(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr,
                    evalIndex, lookback, minGap, tsiLevel, minTsiDrop, triggerWindow, clvLongMin,
                    requireSma200Filter, requireConfirmation, spyLongOk);
                if (lon.IsTriggered) return lon;
                if (direction == ReversalScanDirection.LongOnly) return lon;
                res = lon;
            }

            if (direction != ReversalScanDirection.LongOnly)
            {
                var sh = EvaluateShortReversal(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr,
                    evalIndex, lookback, minGap, tsiLevel, minTsiDrop, triggerWindow, clvShortMax,
                    requireSma200Filter, requireConfirmation, spyShortOk);
                if (sh.IsTriggered) return sh;
                res = sh;
            }

            return res;
        }

        // =========================================================================
        // --- 3. SCAN SCHEDULING & BAR-INDEX HELPERS (no cAlgo dependency) ---
        // =========================================================================

        /// <summary>
        /// Computes the next scan time (UTC) for the given scheduling mode.
        /// </summary>
        public static DateTime CalculateNextScanTime(ReversalScheduleMode mode, int customIntervalSeconds, DateTime fromUtc, int dailyCloseHourEt = 16)
        {
            switch (mode)
            {
                case ReversalScheduleMode.Hourly:
                {
                    var topOfHour = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
                    var next = topOfHour.AddHours(1).AddSeconds(2);
                    if (next <= fromUtc) next = next.AddHours(1);
                    return next;
                }
                case ReversalScheduleMode.Every15Minutes:
                {
                    int nextQuarter = ((fromUtc.Minute / 15) + 1) * 15;
                    var baseHour = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
                    var next = baseHour.AddMinutes(nextQuarter).AddSeconds(2);
                    if (next <= fromUtc) next = next.AddMinutes(15);
                    return next;
                }
                case ReversalScheduleMode.DailyAfterClose:
                {
                    DateTime fromEt = ConvertUtcToEt(fromUtc);
                    DateTime targetTodayEt = new DateTime(fromEt.Year, fromEt.Month, fromEt.Day, dailyCloseHourEt, 0, 2, DateTimeKind.Unspecified);
                    DateTime targetUtc = ConvertEtToUtc(targetTodayEt);
                    if (targetUtc <= fromUtc) targetUtc = ConvertEtToUtc(targetTodayEt.AddDays(1));
                    return targetUtc;
                }
                case ReversalScheduleMode.ManualOnly:
                    return DateTime.MaxValue;
                case ReversalScheduleMode.CustomInterval:
                default:
                    return fromUtc.AddSeconds(Math.Max(60, customIntervalSeconds));
            }
        }

        /// <summary>
        /// Identifies the bar index of the most recently completed regular US cash session daily bar
        /// (09:30 - 16:00 ET, Mon-Fri). The latest bar (n-1) is the forming bar during the session;
        /// the last completed session is n-2. After hours / weekends / pre-market: n-1 is closed.
        /// </summary>
        public static int GetLastCompletedDailyBarIndex(DateTime lastBarOpenUtc, int totalBarsCount, DateTime serverTimeUtc)
        {
            if (totalBarsCount <= 0) return -1;

            DateTime nowEt = ConvertUtcToEt(serverTimeUtc);
            DateTime lastBarEt = ConvertUtcToEt(lastBarOpenUtc);

            // Weekend bar present: last completed regular session is n-2.
            if ((lastBarEt.DayOfWeek == DayOfWeek.Saturday || lastBarEt.DayOfWeek == DayOfWeek.Sunday) && totalBarsCount >= 2)
                return totalBarsCount - 2;

            // Pre-market: latest bar is today's unformed bar; last completed is n-2.
            if (nowEt.TimeOfDay < new TimeSpan(9, 30, 0) && lastBarEt.Date >= nowEt.Date && totalBarsCount >= 2)
                return totalBarsCount - 2;

            // Regular session hours: n-1 is forming; last completed is n-2.
            if (nowEt.DayOfWeek != DayOfWeek.Saturday && nowEt.DayOfWeek != DayOfWeek.Sunday &&
                nowEt.TimeOfDay >= new TimeSpan(9, 30, 0) && nowEt.TimeOfDay < new TimeSpan(16, 0, 0) && totalBarsCount >= 2)
                return totalBarsCount - 2;

            // After-hours broker rollover to tomorrow's bar date: last completed is n-2.
            if (nowEt.TimeOfDay >= new TimeSpan(16, 0, 0) && lastBarEt.Date > nowEt.Date && totalBarsCount >= 2)
                return totalBarsCount - 2;

            return totalBarsCount - 1;
        }

        /// <summary>Converts UTC to US Eastern Time, handling EST/EDT and cross-platform timezone IDs.</summary>
        public static DateTime ConvertUtcToEt(DateTime utcTime)
        {
            try { return TimeZoneInfo.ConvertTimeFromUtc(utcTime, FindTimeZone("Eastern Standard Time", "America/New_York", "EST5EDT")); }
            catch { return utcTime.AddHours(-4); }
        }

        /// <summary>Converts US Eastern Time to UTC, handling EST/EDT and cross-platform timezone IDs.</summary>
        public static DateTime ConvertEtToUtc(DateTime etTime)
        {
            try { return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etTime, DateTimeKind.Unspecified), FindTimeZone("Eastern Standard Time", "America/New_York", "EST5EDT")); }
            catch { return etTime.AddHours(4); }
        }

        private static TimeZoneInfo FindTimeZone(params string[] ids)
        {
            foreach (var id in ids)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch { }
            }
            return TimeZoneInfo.Utc;
        }

        // =========================================================================
        // --- 4. PRIVATE UTILITIES ---
        // =========================================================================

        private static double ClvOf(double close, double high, double low)
        {
            double range = high - low;
            return range > 0.0 ? (2.0 * close - high - low) / range : double.NaN;
        }

        private static bool IsBadInput(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr, int evalIndex)
        {
            if (closes == null || highs == null || lows == null || tsi == null ||
                tsiSig == null || ema21 == null || sma200 == null || atr == null) return true;
            int n = closes.Count;
            if (n == 0 || highs.Count != n || lows.Count != n ||
                tsi.Count != n || tsiSig.Count != n || ema21.Count != n || sma200.Count != n || atr.Count != n) return true;
            return evalIndex < 0 || evalIndex >= n;
        }
    }
}
