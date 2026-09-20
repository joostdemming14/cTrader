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
    /// Result of a Reversal setup evaluation at a single bar.
    /// </summary>
    public readonly struct ReversalSetupResult
    {
        /// <summary>True when the trigger bar fired all conditions on this bar.</summary>
        public readonly bool IsTriggered;

        /// <summary>Long, Short, or None.</summary>
        public readonly ReversalDirection Direction;

        /// <summary>Index of the trailing lookback-bar high/low reference bar (the trigger can be on the same bar).</summary>
        public readonly int ExtremeIndex;

        /// <summary>Level = High[extreme] (short) or Low[extreme] (long).</summary>
        public readonly double Level;

        /// <summary>Reference retest index for alerting; the final trigger logic does not require a separate post-extreme retest.</summary>
        public readonly int RetestIndex;

        /// <summary>Index of the trigger bar (Step 3) being evaluated.</summary>
        public readonly int TriggerIndex;

        // Trigger-bar snapshot values (for alerting / HUD).
        public readonly double Close;
        public readonly double High;
        public readonly double Low;
        public readonly double Clv;
        public readonly double Ppo;
        public readonly double PpoSig;
        public readonly double Ema50;
        public readonly double Atr;

        /// <summary>Distance from the trigger close to the lookback-bar structural level, in ATR units.
        /// Short: (level - close)/ATR. Long: (close - level)/ATR. Used by the optional max-distance filter.</summary>
        public readonly double DistanceAtr;

        /// <summary>Human-readable reason when the setup did not trigger.</summary>
        public readonly string RejectReason;

        public ReversalSetupResult(bool isTriggered, ReversalDirection direction,
            int extremeIndex, double level, int retestIndex, int triggerIndex,
            double close, double high, double low, double clv, double ppo, double ppoSig,
            double ema50, double atr, double distanceAtr, string rejectReason)
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
            Ppo = ppo;
            PpoSig = ppoSig;
            Ema50 = ema50;
            Atr = atr;
            DistanceAtr = distanceAtr;
            RejectReason = rejectReason ?? "";
        }

        /// <summary>Constructs a non-triggered result with a reject reason.</summary>
        public static ReversalSetupResult Reject(ReversalDirection direction, string reason) =>
            new ReversalSetupResult(false, direction, -1, double.NaN, -1, -1,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, reason);
    }

    /// <summary>
    /// Pure C# engine for the Reversal scanner strategy. Zero dependencies on cAlgo APIs —
    /// 100% unit testable, reproducible, and deterministic.
    ///
    /// Final trigger logic used by this scanner:
    ///   EMA50  = 50-period EMA of Close
    ///   ATR    = 14-period ATR (Wilder)
    ///   PPO    = (EMA16(Close) - EMA32(Close)) / EMA32(Close) * 100
    ///   PPOsig = EMA9(PPO)
    ///   CLV    = (2*Close - High - Low) / (High - Low)   range -1..+1
    ///   Lookback = lookback bars (parameter; default 5)
    ///
    /// Short Reversal trigger on bar t:
    ///   Close < lookback_high, CLV <= -0.35, Close > EMA50 + 2.0*ATR, PPO < PPOsig, SPY < SMA50.
    /// Long Reversal trigger on bar t:
    ///   Close > lookback_low, CLV >= +0.35, Close < EMA50 - 2.0*ATR, PPO > PPOsig, SPY > SMA50.
    ///
    /// The lookback-bar structural level is the highest High / lowest Low over the trailing `lookback` bars
    /// EXCLUDING the trigger bar (window = [t - lookback, t - 1]), so "Close breaks the level" is a
    /// genuine break of prior structure. PPO on the trigger bar is the decisive momentum check.
    ///
    /// Optional max-distance filter: reject when the trigger close is already >= maxDistanceAtr * ATR
    /// away from the lookback-bar level, because a stop placed beyond structure would be excessively wide.
    /// The scanner does not compute SL/PT (alert-only).
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

            for (int i = period - 1; i < n; i++)
            {
                double sum = 0.0;
                bool ok = true;
                for (int k = i - period + 1; k <= i; k++)
                {
                    double val = source[k];
                    if (double.IsNaN(val)) { ok = false; break; }
                    sum += val;
                }
                result[i] = ok ? (sum / period) : double.NaN;
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
            if (n <= period) return result;

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

        // =========================================================================
        // --- 2. REVERSAL SETUP EVALUATION ---
        // =========================================================================

        /// <summary>
        /// Evaluates a Short Reversal setup at <paramref name="evalIndex"/> (the trigger candidate bar).
        /// All conditions are tested with the close of that bar (EOD signal, no repaint).
        ///
        /// State model (resolved from the trailing lookback window ending at evalIndex, so it is
        /// full-recalc safe and deterministic):
        ///   1. level = highest High in the trailing `lookback` bars EXCLUDING the trigger bar
        ///      (window = [t - lookback, t - 1], earliest bar on ties).
        ///   2. Trigger at t: Close < level, CLV <= clvMax (-0.35), PPO < PPOsig,
        ///      Close > EMA50 + emaBufferAtr * ATR, and <paramref name="spyShortOk"/> (SPY < SPY_SMA50).
        ///   3. Optional max-distance filter: reject when (level - Close)/ATR >= maxDistanceAtr.
        ///
        /// Invalidation is encoded by re-deriving the extreme from the trailing window each evaluation:
        /// a new higher high refreshes (updates) the level, and an extreme that has rolled out of the
        /// window is replaced by the current window maximum.
        /// </summary>
        public static ReversalSetupResult EvaluateShortReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> ppo, IReadOnlyList<double> ppoSig,
            IReadOnlyList<double> ema50, IReadOnlyList<double> atr,
            int evalIndex, int lookback,
            double clvMax, double emaBufferAtr,
            bool requireMaxDistance, double maxDistanceAtr, bool spyShortOk)
        {
            if (IsBadInput(closes, highs, lows, ppo, ppoSig, ema50, atr, evalIndex))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "Null or out-of-range input");

            int t = evalIndex;
            if (t < 1)
                return ReversalSetupResult.Reject(ReversalDirection.Short, "No prior bar for the lookback-bar level window");

            // Step 1: highest High in the trailing lookback window EXCLUDING the trigger bar
            // (window = [t - lookback, t - 1]). This makes "Close < level" a genuine break of the
            // prior lookback-bar structural high rather than a tautology (close <= own high).
            int winStart = Math.Max(0, t - lookback);
            int winEnd = t - 1;
            int idx = -1;
            double level = double.NaN;
            for (int i = winStart; i <= winEnd; i++)
            {
                if (double.IsNaN(highs[i])) continue;
                if (idx < 0 || highs[i] > level)
                {
                    idx = i;
                    level = highs[i];
                }
            }

            if (idx < 0 || double.IsNaN(level))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "No valid High in lookback window");

            // The definitive spec does not require a separate retest after the extreme; the trigger can
            // occur on the same bar once the close breaks the recent structural high and the momentum gate is met.
            int retestIdx = idx;

            // Trigger conditions on bar t (EOD close).
            double close = closes[t], high = highs[t], low = lows[t];
            double ppoT = ppo[t], ppoSigT = ppoSig[t];
            double ema = ema50[t], atrT = atr[t];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(ppoT) || double.IsNaN(ppoSigT) ||
                double.IsNaN(ema) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "Trigger-bar indicator NaN/invalid");

            if (!(close < level))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} not < level {level:F4}");
            if (!(clv <= clvMax))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"CLV {clv:F2} not <= {clvMax:F2}");
            if (!(ppoT < ppoSigT))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"PPO {ppoT:F2} not < PPOsig {ppoSigT:F2}");
            double extension = ema + emaBufferAtr * atrT;
            if (!(close > extension))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} not > EMA50+{emaBufferAtr:F1}*ATR ({extension:F4})");
            if (!spyShortOk)
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    "SPY benchmark gate failed (SPY not < SPY_SMA50)");

            // Optional max-distance filter: reject when the close has already travelled too far from the
            // lookback-bar high, because a stop beyond structure would be excessively wide.
            double distanceAtr = (level - close) / atrT;
            if (requireMaxDistance && distanceAtr >= maxDistanceAtr)
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} is {distanceAtr:F2} ATR below lookback-bar high {level:F4} (>= {maxDistanceAtr:F2} ATR, SL too wide)");

            return new ReversalSetupResult(true, ReversalDirection.Short,
                idx, level, retestIdx, t, close, high, low, clv, ppoT, ppoSigT,
                ema, atrT, distanceAtr, "Short Reversal triggered");
        }

        /// <summary>
        /// Evaluates a Long Reversal setup at <paramref name="evalIndex"/> (the trigger candidate bar).
        /// Mirror of <see cref="EvaluateShortReversal"/>: level = lowest Low in the trailing `lookback`
        /// bars EXCLUDING the trigger bar, trigger: Close > level, CLV >= clvMin, PPO > PPOsig,
        /// Close < EMA50 - emaBufferAtr * ATR, and <paramref name="spyLongOk"/> (SPY > SPY_SMA50).
        /// Optional max-distance filter: reject when (Close - level)/ATR >= maxDistanceAtr.
        /// </summary>
        public static ReversalSetupResult EvaluateLongReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> ppo, IReadOnlyList<double> ppoSig,
            IReadOnlyList<double> ema50, IReadOnlyList<double> atr,
            int evalIndex, int lookback,
            double clvMin, double emaBufferAtr,
            bool requireMaxDistance, double maxDistanceAtr, bool spyLongOk)
        {
            if (IsBadInput(closes, highs, lows, ppo, ppoSig, ema50, atr, evalIndex))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "Null or out-of-range input");

            int t = evalIndex;
            if (t < 1)
                return ReversalSetupResult.Reject(ReversalDirection.Long, "No prior bar for the lookback-bar level window");

            // Step 1: lowest Low in the trailing lookback window EXCLUDING the trigger bar.
            int winStart = Math.Max(0, t - lookback);
            int winEnd = t - 1;
            int idx = -1;
            double level = double.NaN;
            for (int i = winStart; i <= winEnd; i++)
            {
                if (double.IsNaN(lows[i])) continue;
                if (idx < 0 || lows[i] < level)
                {
                    idx = i;
                    level = lows[i];
                }
            }

            if (idx < 0 || double.IsNaN(level))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "No valid Low in lookback window");

            // The definitive spec does not require a separate retest after the extreme; the trigger can
            // occur on the same bar once the close breaks the recent structural low and the momentum gate is met.
            int retestIdx = idx;

            // Trigger conditions on bar t (EOD close).
            double close = closes[t], high = highs[t], low = lows[t];
            double ppoT = ppo[t], ppoSigT = ppoSig[t];
            double ema = ema50[t], atrT = atr[t];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(ppoT) || double.IsNaN(ppoSigT) ||
                double.IsNaN(ema) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "Trigger-bar indicator NaN/invalid");

            if (!(close > level))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} not > level {level:F4}");
            if (!(clv >= clvMin))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"CLV {clv:F2} not >= {clvMin:F2}");
            if (!(ppoT > ppoSigT))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"PPO {ppoT:F2} not > PPOsig {ppoSigT:F2}");
            double extension = ema - emaBufferAtr * atrT;
            if (!(close < extension))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} not < EMA50-{emaBufferAtr:F1}*ATR ({extension:F4})");
            if (!spyLongOk)
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    "SPY benchmark gate failed (SPY not > SPY_SMA50)");

            // Optional max-distance filter: reject when the close has already travelled too far from the
            // lookback-bar low, because a stop beyond structure would be excessively wide.
            double distanceAtr = (close - level) / atrT;
            if (requireMaxDistance && distanceAtr >= maxDistanceAtr)
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} is {distanceAtr:F2} ATR above lookback-bar low {level:F4} (>= {maxDistanceAtr:F2} ATR, SL too wide)");

            return new ReversalSetupResult(true, ReversalDirection.Long,
                idx, level, retestIdx, t, close, high, low, clv, ppoT, ppoSigT,
                ema, atrT, distanceAtr, "Long Reversal triggered");
        }

        /// <summary>
        /// Evaluates both directions and returns the triggered setup, if any.
        /// Long and Short are mutually exclusive in practice (Short needs Close > EMA50 + 2*ATR,
        /// Long needs Close < EMA50 - 2*ATR). When <paramref name="direction"/> restricts to one
        /// side, only that side is evaluated. <paramref name="spyLongOk"/>/<paramref name="spyShortOk"/>
        /// carry the SPY-vs-SPY_SMA50 benchmark gate (computed by the cBot, not the pure engine).
        /// </summary>
        public static ReversalSetupResult Evaluate(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> ppo, IReadOnlyList<double> ppoSig,
            IReadOnlyList<double> ema50, IReadOnlyList<double> atr,
            int evalIndex, int lookback,
            ReversalScanDirection direction,
            double clvShortMax, double clvLongMin, double emaBufferAtr,
            bool requireMaxDistance, double maxDistanceAtr,
            bool spyLongOk, bool spyShortOk)
        {
            ReversalSetupResult res = ReversalSetupResult.Reject(ReversalDirection.None, "Not evaluated");

            if (direction != ReversalScanDirection.ShortOnly)
            {
                var lon = EvaluateLongReversal(closes, highs, lows, ppo, ppoSig, ema50, atr,
                    evalIndex, lookback, clvLongMin, emaBufferAtr,
                    requireMaxDistance, maxDistanceAtr, spyLongOk);
                if (lon.IsTriggered) return lon;
                if (direction == ReversalScanDirection.LongOnly) return lon;
                res = lon;
            }

            if (direction != ReversalScanDirection.LongOnly)
            {
                var sh = EvaluateShortReversal(closes, highs, lows, ppo, ppoSig, ema50, atr,
                    evalIndex, lookback, clvShortMax, emaBufferAtr,
                    requireMaxDistance, maxDistanceAtr, spyShortOk);
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
            IReadOnlyList<double> ppo, IReadOnlyList<double> ppoSig,
            IReadOnlyList<double> ema50, IReadOnlyList<double> atr, int evalIndex)
        {
            if (closes == null || highs == null || lows == null || ppo == null ||
                ppoSig == null || ema50 == null || atr == null) return true;
            int n = closes.Count;
            if (n == 0 || highs.Count != n || lows.Count != n ||
                ppo.Count != n || ppoSig.Count != n || ema50.Count != n || atr.Count != n) return true;
            return evalIndex < 0 || evalIndex >= n;
        }
    }
}
