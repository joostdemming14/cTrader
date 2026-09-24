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
    /// Relative-strength ranking bucket. Symbols are ranked against their own asset class only:
    /// cross-asset returns are incomparable (a FX pair and an equity do not share a return scale).
    /// </summary>
    public enum RsAssetBucket
    {
        /// <summary>US cash-session equities ('.US' suffix).</summary>
        UsEquity = 0,
        /// <summary>Symbols in the configured crypto list.</summary>
        Crypto = 1,
        /// <summary>Everything else (FX, metals, commodities, indices without a suffix).</summary>
        Other = 2
    }

    /// <summary>
    /// Relative-strength rank of one symbol inside its asset bucket: 1 = strongest of the bucket.
    /// The score is informational only and never excludes a symbol from alerting.
    /// </summary>
    public readonly struct RsRank
    {
        /// <summary>1-based rank inside the bucket (1 = highest score).</summary>
        public readonly int Rank;
        /// <summary>Number of symbols with a valid score in the bucket, this symbol included.</summary>
        public readonly int BucketSize;
        /// <summary>Share of the bucket scoring at or below this symbol, 0..100 (higher = stronger).</summary>
        public readonly double Percentile;
        /// <summary>The symbol's risk-adjusted momentum score the rank is derived from.</summary>
        public readonly double Score;

        /// <summary>Builds a rank entry.</summary>
        public RsRank(int rank, int bucketSize, double percentile, double score)
        {
            Rank = rank;
            BucketSize = bucketSize;
            Percentile = percentile;
            Score = score;
        }

        /// <summary>True when the entry carries a usable ranking.</summary>
        public bool IsValid => BucketSize > 0 && Rank > 0;
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

        /// <summary>TSI on the signal bar (the bar that carries Close/CLV; with confirmation it is the bar before the confirmation bar).</summary>
        public readonly double SignalTsi;

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
            double close, double high, double low, double clv, double tsi, double signalTsi, double tsiSig,
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
            SignalTsi = signalTsi;
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
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, reason);
    }

    /// <summary>
    /// Pure C# engine for the Reversal scanner strategy. Zero dependencies on cAlgo APIs —
    /// 100% unit testable, reproducible, and deterministic.
    ///
    /// Final trigger logic used by this scanner (TSI divergence, no pivot waiting):
    ///   TSI    = 100 * EMA(short, EMA(long, PC)) / EMA(short, EMA(long, |PC|))   (Blau; defaults 25/13)
    ///   CLV    = (2*Close - High - Low) / (High - Low)   range -1..+1
    ///   Lookback = divergence window (parameter; default 90, rolling: the structural window
    ///   spans minGap..lookback bars — short divergences and structural ones share one rule)
    ///
    /// Short Reversal = two steps, both derived from trailing windows (deterministic,
    /// full-recalc safe):
    ///   1. DIVERGENCE DETECTION: some bar d in the last `triggerWindow` bars (including the
    ///      signal bar) printed a High at or above the highest High of its rolling lookback
    ///      window (fresh extreme) or within `nearExtremeAtr` ATR of it (near-extreme), with
    ///      TSI[d] <= TSI[reference] - minTsiDrop. The reference is the highest High of the prior
    ///      window, at least minGap bars back, whose TSI exceeds +tsiLevel (a genuinely strong
    ///      prior move); intermediate divergence highs with weaker TSI are skipped, so a chain
    ///      of higher-high / lower-TSI extremes re-anchors to the original strong extreme —
    ///      the setup survives newer, more extreme prints as long as the TSI keeps stepping
    ///      down vs the previous extreme. It only dies when momentum recovers at a newer
    ///      extreme (a higher High with a higher TSI).
    ///   2. TRIGGER: the signal bar closes weakly (CLV <= clvShortMax) and its TSI sits at or
    ///      below the rolling TSI average (momentum flat or falling; `tsiMomentumPeriod`).
    ///      The divergence bar and the trigger bar may be the same bar. Trend filter and gates
    ///      still apply.
    ///
    /// Long Reversal is the mirror: fresh or near-extreme (higher low within `nearExtremeAtr`
    /// ATR) rolling-window Low with TSI[d] >= TSI[reference] + minTsiDrop and reference
    /// TSI < -tsiLevel, then a strong close (CLV >= clvLongMin) with TSI at or above its
    /// rolling average. Newer, deeper lows re-anchor the divergence chain to the newest
    /// extreme while the TSI keeps stepping up vs the previous extreme; the setup only dies
    /// when momentum deteriorates at a newer extreme.
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
        /// (including the signal bar itself) printed a High at or above the highest High of its rolling
        /// lookback window (fresh extreme) or within <paramref name="nearExtremeAtr"/> ATR of it
        /// (near-extreme), and sat at least <paramref name="minTsiDrop"/> below the TSI at the reference
        /// extreme (the highest High of the prior window, at least <paramref name="minGap"/> bars back).
        /// The REFERENCE TSI must exceed <paramref name="tsiLevel"/> (the prior move was genuinely
        /// strong); the divergence bar's own TSI is unconstrained. No higher High since (stale invalidation).
        /// Step 2 — the trigger: the signal bar closes weakly (CLV &lt;= clvMax) and, when
        /// <paramref name="tsiMomentumPeriod"/> &gt; 1, its TSI sits at or below the rolling TSI average
        /// (momentum flat or falling). The divergence bar and the trigger bar may be the same bar.
        /// Trend filter and <paramref name="spyShortOk"/> apply.
        /// </summary>
        public static ReversalSetupResult EvaluateShortReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig, IReadOnlyList<double> tsiAvg,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvMax, double nearExtremeAtr,
            int tsiMomentumPeriod,
            bool requireSma200Filter,
            bool requireConfirmation, bool spyShortOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr, evalIndex) ||
                (tsiMomentumPeriod > 1 && tsiAvg == null) ||
                (tsiMomentumPeriod > 1 && (tsiAvg.Count != closes.Count)))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "Null or out-of-range input");

            int t = evalIndex;
            if (t < (requireConfirmation ? 2 : 1))
                return ReversalSetupResult.Reject(ReversalDirection.Short, "No prior bar for the divergence window");

            int signal = requireConfirmation ? t - 1 : t;

            // Step 1 — most recent qualifying divergence bar d in [signal - triggerWindow, signal]
            // via the shared rolling-window core (fresh or near-extreme High, weaker TSI).
            var divB = FindRollingBearishDivergence(highs, tsi, atr, signal,
                lookback, minGap, tsiLevel, minTsiDrop, nearExtremeAtr, triggerWindow);
            int divIdx = divB.DivIndex;
            int refIdx = divB.RefIndex;
            double refHigh = divB.RefLevel;
            double divTsi = double.NaN, refTsi = divB.RefTsi;
            if (divIdx < 0)
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    "No TSI bearish divergence in the trigger window");
            divTsi = tsi[divIdx];

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
            if (tsiMomentumPeriod > 1 && !PassesShortMomentumGate(tsi, tsiAvg, signal, tsiMomentumPeriod))
                return ReversalSetupResult.Reject(ReversalDirection.Short,
                    $"TSI {tsi[signal]:F2} not <= {tsiAvg[signal]:F2} ({tsiMomentumPeriod}-bar avg; momentum not flat/falling)");
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
                refIdx, refHigh, -1, t, close, high, low, clv, divTsi, tsi[signal], tsiSigT,
                refTsi, ema, atrT, double.NaN, "Short Reversal triggered (TSI divergence + weak close)");
        }

        /// <summary>
        /// Evaluates a Long Reversal (TSI bullish divergence + strong close) at <paramref name="evalIndex"/>.
        /// Mirror of <see cref="EvaluateShortReversal"/>: a bar in the last <paramref name="triggerWindow"/>
        /// bars printed a Low at or below the lowest Low of its rolling lookback window (fresh extreme)
        /// or within <paramref name="nearExtremeAtr"/> ATR above it (near-extreme higher low), and sits
        /// at least <paramref name="minTsiDrop"/> above the TSI at the reference extreme (the lowest Low
        /// of the prior window, at least <paramref name="minGap"/> bars back), whose TSI must be below
        /// -<paramref name="tsiLevel"/> (no lower Low since). The trigger is the strong close on the
        /// signal bar: CLV &gt;= clvMin and, when <paramref name="tsiMomentumPeriod"/> &gt; 1, its TSI at
        /// or above the rolling TSI average (momentum flat or rising); may be the divergence bar itself.
        /// </summary>
        public static ReversalSetupResult EvaluateLongReversal(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig, IReadOnlyList<double> tsiAvg,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvMin, double nearExtremeAtr,
            int tsiMomentumPeriod,
            bool requireSma200Filter,
            bool requireConfirmation, bool spyLongOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, sma200, atr, evalIndex) ||
                (tsiMomentumPeriod > 1 && tsiAvg == null) ||
                (tsiMomentumPeriod > 1 && (tsiAvg.Count != closes.Count)))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "Null or out-of-range input");

            int t = evalIndex;
            if (t < (requireConfirmation ? 2 : 1))
                return ReversalSetupResult.Reject(ReversalDirection.Long, "No prior bar for the divergence window");

            int signal = requireConfirmation ? t - 1 : t;

            // Step 1 — most recent qualifying divergence bar d in [signal - triggerWindow, signal]
            // via the shared rolling-window core (fresh or near-extreme Low, stronger TSI).
            var divL = FindRollingBullishDivergence(lows, tsi, atr, signal,
                lookback, minGap, tsiLevel, minTsiDrop, nearExtremeAtr, triggerWindow);
            int divIdx = divL.DivIndex;
            int refIdx = divL.RefIndex;
            double refLow = divL.RefLevel;
            double divTsi = double.NaN, refTsi = divL.RefTsi;
            if (divIdx < 0)
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    "No TSI bullish divergence in the trigger window");
            divTsi = tsi[divIdx];

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
            if (tsiMomentumPeriod > 1 && !PassesLongMomentumGate(tsi, tsiAvg, signal, tsiMomentumPeriod))
                return ReversalSetupResult.Reject(ReversalDirection.Long,
                    $"TSI {tsi[signal]:F2} not >= {tsiAvg[signal]:F2} ({tsiMomentumPeriod}-bar avg; momentum not flat/rising)");
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
                refIdx, refLow, -1, t, close, high, low, clv, divTsi, tsi[signal], tsiSigT,
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
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig, IReadOnlyList<double> tsiAvg,
            IReadOnlyList<double> ema21, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            ReversalScanDirection direction,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, int triggerWindow,
            double clvShortMax, double clvLongMin, double nearExtremeAtr,
            int tsiMomentumPeriod,
            bool requireSma200Filter,
            bool requireConfirmation,
            bool spyLongOk, bool spyShortOk)
        {
            ReversalSetupResult res = ReversalSetupResult.Reject(ReversalDirection.None, "Not evaluated");

            if (direction != ReversalScanDirection.ShortOnly)
            {
                var lon = EvaluateLongReversal(closes, highs, lows, tsi, tsiSig, tsiAvg, ema21, sma200, atr,
                    evalIndex, lookback, minGap, tsiLevel, minTsiDrop, triggerWindow, clvLongMin, nearExtremeAtr,
                    tsiMomentumPeriod,
                    requireSma200Filter, requireConfirmation, spyLongOk);
                if (lon.IsTriggered) return lon;
                if (direction == ReversalScanDirection.LongOnly) return lon;
                res = lon;
            }

            if (direction != ReversalScanDirection.LongOnly)
            {
                var sh = EvaluateShortReversal(closes, highs, lows, tsi, tsiSig, tsiAvg, ema21, sma200, atr,
                    evalIndex, lookback, minGap, tsiLevel, minTsiDrop, triggerWindow, clvShortMax, nearExtremeAtr,
                    tsiMomentumPeriod,
                    requireSma200Filter, requireConfirmation, spyShortOk);
                if (sh.IsTriggered) return sh;
                res = sh;
            }

            return res;
        }

        // =========================================================================
        // --- 2b. DIVERGENCE DETECTION (shared with the Continuation scanner) ---
        // =========================================================================

        /// <summary>
        /// Detects an active bearish price/TSI divergence at <paramref name="evalIndex"/> with the exact
        /// same rolling rule the reversal trigger uses: some bar d in the last
        /// <paramref name="triggerWindow"/> bars (including the eval bar) printed a High at or above
        /// the highest High of its rolling lookback window (fresh extreme) or within
        /// <paramref name="nearExtremeAtr"/> ATR of it (near-extreme), while its TSI stayed at or
        /// below the TSI of the previous extreme and at least <paramref name="minTsiDrop"/> below the
        /// TSI of the strong reference extreme (the highest High at least <paramref name="minGap"/>
        /// bars back with TSI &gt; <paramref name="tsiLevel"/>), and no higher High has printed since.
        /// When any qualifying divergence bar is found the momentum behind the highs is fading,
        /// which suppresses continuation longs; the TSI extreme gate keeps harmless lookbacks from
        /// suppressing healthy trends.
        /// </summary>
        public static bool HasActiveBearishTsiDivergence(
            IReadOnlyList<double> highs,
            IReadOnlyList<double> tsi,
            IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, double nearExtremeAtr, int triggerWindow)
        {
            if (highs == null || tsi == null || atr == null) return false;
            int n = highs.Count;
            if (n == 0 || tsi.Count != n || atr.Count != n || evalIndex < 0 || evalIndex >= n) return false;

            return FindRollingBearishDivergence(highs, tsi, atr, evalIndex,
                lookback, minGap, tsiLevel, minTsiDrop, nearExtremeAtr, triggerWindow).DivIndex >= 0;
        }

        /// <summary>
        /// Mirror of <see cref="HasActiveBearishTsiDivergence"/> for lows: some bar d in the last
        /// <paramref name="triggerWindow"/> bars printed a Low at or below the lowest Low of its rolling
        /// lookback window (fresh extreme) or within <paramref name="nearExtremeAtr"/> ATR above it
        /// (near-extreme higher low) while its TSI stayed at or above the TSI of the previous extreme
        /// and at least <paramref name="minTsiDrop"/> above the TSI of the strong reference extreme
        /// (the lowest Low at least <paramref name="minGap"/> bars back with
        /// TSI &lt; -<paramref name="tsiLevel"/>), and no lower Low has printed since. Suppresses
        /// continuation shorts.
        /// </summary>
        public static bool HasActiveBullishTsiDivergence(
            IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi,
            IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, double nearExtremeAtr, int triggerWindow)
        {
            if (lows == null || tsi == null || atr == null) return false;
            int n = lows.Count;
            if (n == 0 || tsi.Count != n || atr.Count != n || evalIndex < 0 || evalIndex >= n) return false;

            return FindRollingBullishDivergence(lows, tsi, atr, evalIndex,
                lookback, minGap, tsiLevel, minTsiDrop, nearExtremeAtr, triggerWindow).DivIndex >= 0;
        }

        // =========================================================================
        // --- 2c. RELATIVE STRENGTH RANKING (shared with the Continuation scanner) ---
        // =========================================================================

        /// <summary>
        /// Risk-adjusted relative-strength score over the last <paramref name="rsPeriod"/> completed
        /// bars: total return divided by the standard deviation of the per-bar returns scaled by the
        /// square root of the period (a t-statistic of the trend). A steady grind scores higher than
        /// a single melt-up bar followed by noise, and low-volatility symbols are not penalised
        /// against high-volatility ones. Returns <see cref="double.NaN"/> when the window has fewer
        /// than <paramref name="rsPeriod"/> bars, a non-positive close, or a zero return spread —
        /// the caller then skips the symbol instead of ranking a meaningless value.
        /// </summary>
        public static double ComputeRsScore(IReadOnlyList<double> closes, int evalIndex, int rsPeriod)
        {
            if (closes == null || closes.Count == 0 || rsPeriod < 2 || evalIndex < 0 || evalIndex >= closes.Count)
                return double.NaN;
            int startIdx = evalIndex - rsPeriod;
            if (startIdx < 0)
                return double.NaN;
            double baseClose = closes[startIdx];
            double lastClose = closes[evalIndex];
            if (double.IsNaN(baseClose) || double.IsNaN(lastClose) || baseClose <= 0.0 || lastClose <= 0.0)
                return double.NaN;
            double totalReturn = (lastClose - baseClose) / baseClose;
            double sum = 0.0;
            double sumSq = 0.0;
            int bars = 0;
            for (int i = startIdx + 1; i <= evalIdxSafe(evalIndex, closes.Count - 1); i++)
            {
                double prev = closes[i - 1];
                double cur = closes[i];
                if (double.IsNaN(prev) || double.IsNaN(cur) || prev <= 0.0)
                    return double.NaN;
                double r = (cur - prev) / prev;
                sum += r;
                sumSq += r * r;
                bars++;
            }
            if (bars < rsPeriod)
                return double.NaN;
            double mean = sum / bars;
            double variance = sumSq / bars - mean * mean;
            if (variance < 0.0) variance = 0.0;
            double sd = Math.Sqrt(variance);
            double denom = sd * Math.Sqrt(rsPeriod);
            if (denom <= 0.0)
                return double.NaN;
            return totalReturn / denom;
        }

        private static int evalIdxSafe(int evalIndex, int last)
        {
            return evalIndex > last ? last : evalIndex;
        }

        /// <summary>
        /// Ranks the scored symbols inside each asset bucket. Ties share a rank (standard
        /// competition ranking): with scores 3.0, 2.0, 2.0, 1.0 the ranks are 1, 2, 2, 4. Symbols
        /// without a valid score (NaN) are excluded from the ranking entirely and get no entry.
        /// The percentile is the share of ranked symbols with a score at or below this symbol's
        /// (0..100, higher = stronger), so the bucket's weakest symbol always sits at 0 and the
        /// strongest at 100.
        /// </summary>
        public static Dictionary<string, RsRank> BuildRsRankings(IReadOnlyDictionary<string, (RsAssetBucket Bucket, double Score)> scores)
        {
            var result = new Dictionary<string, RsRank>(StringComparer.OrdinalIgnoreCase);
            if (scores == null || scores.Count == 0)
                return result;
            var byBucket = new Dictionary<RsAssetBucket, List<KeyValuePair<string, (RsAssetBucket Bucket, double Score)>>>();
            foreach (var kv in scores)
            {
                if (double.IsNaN(kv.Value.Score)) continue;
                if (!byBucket.TryGetValue(kv.Value.Bucket, out var list))
                {
                    list = new List<KeyValuePair<string, (RsAssetBucket, double)>>();
                    byBucket[kv.Value.Bucket] = list;
                }
                list.Add(kv);
            }
            foreach (var bucketKv in byBucket)
            {
                var members = bucketKv.Value;
                members.Sort((a, b) => b.Value.Score.CompareTo(a.Value.Score));
                int size = members.Count;
                int currentRank = 0;
                for (int i = 0; i < size; i++)
                {
                    if (i == 0 || members[i].Value.Score.CompareTo(members[i - 1].Value.Score) != 0)
                        currentRank = i + 1;
                    double percentile = 100.0 * (size - currentRank) / Math.Max(size - 1, 1);
                    if (size == 1)
                        percentile = 100.0;
                    result[members[i].Key] = new RsRank(currentRank, size, percentile, members[i].Value.Score);
                }
            }
            return result;
        }

        /// <summary>
        /// Formats the rank portion of the RS tag used inside the alert detail: rank plus percentile
        /// inside the asset bucket, or "n/a" when no ranking is available (too few bars, RS off).
        /// Purely informational, never a gate.
        /// </summary>
        public static string FormatRsTag(RsRank rank)
        {
            if (!rank.IsValid)
                return "n/a";
            return $"#{rank.Rank}/{rank.BucketSize} ({rank.Percentile:F0} pct)";
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
        /// True when the regular US cash session (09:30 - 16:00 ET, Mon-Fri) contained in a daily bar
        /// that opened at <paramref name="barOpenUtc"/> has already closed.
        ///
        /// Brokers stamp US-equity daily bars in different ways (00:00 UTC, 00:00 ET, cTrader's own
        /// 17:00 ET aggregation boundary, or the session open), so the bar's own open time is used to
        /// locate the 16:00 ET boundary of the session that this bar carries: the first 16:00 ET
        /// strictly after the bar opened. Every daily bar window contains exactly one regular cash
        /// session, which makes the test independent of the broker's stamping convention. Inside the
        /// session - and for a bar the broker pre-created for the next session - the result is false,
        /// so a still-forming (or not yet traded) bar is never reported as completed.
        /// </summary>
        public static bool HasUsCashSessionEnded(DateTime barOpenUtc, DateTime serverTimeUtc)
        {
            DateTime openEt = ConvertUtcToEt(barOpenUtc);
            DateTime sessionEndEt = openEt.Date.AddHours(16.0);
            if (sessionEndEt <= openEt) sessionEndEt = sessionEndEt.AddDays(1);
            return ConvertUtcToEt(serverTimeUtc) >= sessionEndEt;
        }

        /// <summary>
        /// UTC instant at which the daily bar that opened at <paramref name="barOpenUtc"/> stops
        /// receiving data: exactly 24 hours later on the ET wall clock. Using the ET wall clock
        /// (instead of adding 24h to the UTC value) keeps daily boundaries stable across the DST
        /// switches that would otherwise shift them by one hour.
        /// </summary>
        public static DateTime GetDailyBarCloseTimeUtc(DateTime barOpenUtc)
        {
            return ConvertEtToUtc(ConvertUtcToEt(barOpenUtc).AddDays(1.0));
        }

        /// <summary>
        /// True when the daily bar that opened at <paramref name="barOpenUtc"/> can no longer receive
        /// data, i.e. it is a completed bar that may be evaluated.
        ///
        /// US cash equities (<paramref name="usCashEquity"/>): the regular session closes at 16:00 ET,
        /// so the bar is completed once the 16:00 ET boundary of the session it carries has passed
        /// (see <see cref="HasUsCashSessionEnded"/>). The boundary is derived from the bar's own open
        /// time, so the rule is independent of the broker's daily bar stamping convention (00:00 UTC,
        /// 00:00 ET, cTrader's own 17:00 ET aggregation boundary or the session open) - every daily
        /// bar window contains exactly one regular session.
        ///
        /// Every other instrument (FX, metals, commodities, indices, crypto): the bar's own 24h
        /// window must have elapsed. Prices there are not tied to a cash session, so the bar stays
        /// current until its next boundary arrives; a stale feed, a holiday that is still reported as
        /// an open market, or a symbol without usable market hours therefore never costs a bar.
        /// </summary>
        public static bool IsDailyBarCompleted(DateTime barOpenUtc, DateTime serverTimeUtc, bool usCashEquity)
        {
            if (usCashEquity) return HasUsCashSessionEnded(barOpenUtc, serverTimeUtc);
            return ConvertUtcToEt(serverTimeUtc) >= ConvertUtcToEt(barOpenUtc).AddDays(1.0);
        }

        /// <summary>
        /// True when a daily bar has not traded yet: no ticks at all and no range. Brokers
        /// pre-create the next session's daily bar (weekend / holiday / after-hours rollover), and
        /// such an empty bar has no close location and no divergence, so it can never carry a signal
        /// and must never hide the session that just finished.
        /// </summary>
        public static bool IsUntouchedDailyBar(double open, double high, double low, double close, double tickVolume)
        {
            return tickVolume == 0.0 && open == close && high == low;
        }

        /// <summary>
        /// Index of the last completed daily bar of a series, or -1 when there is none.
        ///
        /// The series is walked from the newest bar backwards and the first bar that
        /// 1. is not stamped in the future,
        /// 2. has traded (see <see cref="IsUntouchedDailyBar"/>),
        /// 3. and cannot receive data any more (see <see cref="IsDailyBarCompleted"/>)
        /// is the last completed bar. Bars that the broker pre-created for the next session are
        /// therefore skipped instead of hiding the last closed bar, and a bar that is still forming
        /// (or still ahead) is never evaluated - outside market hours this returns the newest closed
        /// bar of the series rather than the one before it.
        /// </summary>
        public static int GetLastCompletedDailyBarIndex(
            IReadOnlyList<DateTime> openTimesUtc,
            IReadOnlyList<bool> barHasTraded,
            bool usCashEquity,
            DateTime serverTimeUtc)
        {
            if (openTimesUtc == null || barHasTraded == null) return -1;
            if (openTimesUtc.Count == 0 || openTimesUtc.Count != barHasTraded.Count) return -1;

            for (int i = openTimesUtc.Count - 1; i >= 0; i--)
            {
                DateTime openUtc = openTimesUtc[i];
                if (openUtc > serverTimeUtc) continue;
                if (!barHasTraded[i]) continue;
                if (IsDailyBarCompleted(openUtc, serverTimeUtc, usCashEquity)) return i;
            }

            return -1;
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
        // --- 3b. ROLLING-DIVERGENCE SHARED CORE ---
        // =========================================================================

        /// <summary>
        /// Finds the most recent qualifying bearish-divergence bar for <paramref name="evalIndex"/>
        /// (this includes the signal bar itself) using the rolling structural window: the candidate
        /// bar d must print a High that is at or above the highest High of its trailing window
        /// (fresh extreme) or within <paramref name="nearExtremeAtr"/> ATR of it (near-extreme), with
        /// windowStart = d - lookback clamped so the window never reaches further back than
        /// <paramref name="minGap"/> bars before d. TSI constraints: TSI[d] at or below the TSI of
        /// the previous window extreme, at least <paramref name="minTsiDrop"/> below the TSI of the
        /// strong reference (highest High at least <paramref name="minGap"/> back with
        /// TSI &gt; <paramref name="tsiLevel"/>), and no higher High printed since d (stale check). Only
        /// bars d in the last <paramref name="triggerWindow"/> bars (including the evaluated bar)
        /// are considered. Returns the divergence bar index, reference index, reference level and
        /// reference TSI, or a DivIndex of -1 when no qualifying bar exists.
        /// </summary>
        private static (int DivIndex, int RefIndex, double RefLevel, double RefTsi) FindRollingBearishDivergence(
            IReadOnlyList<double> highs, IReadOnlyList<double> tsi, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, double nearExtremeAtr, int triggerWindow)
        {
            for (int d = evalIndex; d >= evalIndex - triggerWindow && d >= 0; d--)
            {
                int windowStart = d - lookback;
                if (windowStart < 0) windowStart = 0;
                if (windowStart > d - minGap) windowStart = d - minGap;
                if (windowStart < 0) break;

                int prevIdx = -1;
                double prevHigh = double.NaN;
                for (int i = windowStart; i < d; i++)
                {
                    if (prevIdx < 0 || highs[i] > prevHigh)
                    {
                        prevIdx = i;
                        prevHigh = highs[i];
                    }
                }

                int rIdx = -1;
                double rHigh = double.NaN;
                for (int i = windowStart; i <= d - minGap; i++)
                {
                    if (double.IsNaN(tsi[i]) || !(tsi[i] > tsiLevel)) continue;
                    if (rIdx < 0 || highs[i] > rHigh)
                    {
                        rIdx = i;
                        rHigh = highs[i];
                    }
                }
                if (prevIdx < 0 || rIdx < 0 ||
                    double.IsNaN(tsi[d]) || double.IsNaN(tsi[prevIdx]) || double.IsNaN(tsi[rIdx]) ||
                    double.IsNaN(atr[d]) || atr[d] <= 0.0)
                    continue;

                bool freshExtreme = highs[d] >= prevHigh;
                bool nearExtreme = highs[d] >= prevHigh - nearExtremeAtr * atr[d];
                if ((freshExtreme || nearExtreme) &&
                    tsi[d] <= tsi[prevIdx] &&
                    tsi[d] <= tsi[rIdx] - minTsiDrop)
                {
                    bool stale = false;
                    for (int i = d + 1; i <= evalIndex; i++)
                    {
                        if (highs[i] > highs[d]) { stale = true; break; }
                    }
                    if (!stale) return (d, rIdx, rHigh, tsi[rIdx]);
                }
            }
            return (-1, -1, double.NaN, double.NaN);
        }

        /// <summary>
        /// Mirror of <see cref="FindRollingBearishDivergence"/> for lows: the candidate bar d must
        /// print a Low at or below the lowest Low of its trailing window (fresh extreme) or within
        /// <paramref name="nearExtremeAtr"/> ATR above it (near-extreme higher low), with the same
        /// rolling window and the mirrored TSI constraints (TSI[d] at or above the previous
        /// extreme's TSI, at least <paramref name="minTsiDrop"/> above the strong reference with
        /// TSI &lt; -<paramref name="tsiLevel"/>), and no lower Low printed since d. Only bars d in the
        /// last <paramref name="triggerWindow"/> bars (including the evaluated bar) are considered.
        /// Returns the divergence bar index, reference index, reference level and reference TSI,
        /// or a DivIndex of -1 when no qualifying bar exists.
        /// </summary>
        private static (int DivIndex, int RefIndex, double RefLevel, double RefTsi) FindRollingBullishDivergence(
            IReadOnlyList<double> lows, IReadOnlyList<double> tsi, IReadOnlyList<double> atr,
            int evalIndex,
            int lookback, int minGap, double tsiLevel, double minTsiDrop, double nearExtremeAtr, int triggerWindow)
        {
            for (int d = evalIndex; d >= evalIndex - triggerWindow && d >= 0; d--)
            {
                int windowStart = d - lookback;
                if (windowStart < 0) windowStart = 0;
                if (windowStart > d - minGap) windowStart = d - minGap;
                if (windowStart < 0) break;

                int prevIdx = -1;
                double prevLow = double.NaN;
                for (int i = windowStart; i < d; i++)
                {
                    if (prevIdx < 0 || lows[i] < prevLow)
                    {
                        prevIdx = i;
                        prevLow = lows[i];
                    }
                }

                int rIdx = -1;
                double rLow = double.NaN;
                for (int i = windowStart; i <= d - minGap; i++)
                {
                    if (double.IsNaN(tsi[i]) || !(tsi[i] < -tsiLevel)) continue;
                    if (rIdx < 0 || lows[i] < rLow)
                    {
                        rIdx = i;
                        rLow = lows[i];
                    }
                }
                if (prevIdx < 0 || rIdx < 0 ||
                    double.IsNaN(tsi[d]) || double.IsNaN(tsi[prevIdx]) || double.IsNaN(tsi[rIdx]) ||
                    double.IsNaN(atr[d]) || atr[d] <= 0.0)
                    continue;

                bool freshExtreme = lows[d] <= prevLow;
                bool nearExtreme = lows[d] <= prevLow + nearExtremeAtr * atr[d];
                if ((freshExtreme || nearExtreme) &&
                    tsi[d] >= tsi[prevIdx] &&
                    tsi[d] >= tsi[rIdx] + minTsiDrop)
                {
                    bool stale = false;
                    for (int i = d + 1; i <= evalIndex; i++)
                    {
                        if (lows[i] < lows[d]) { stale = true; break; }
                    }
                    if (!stale) return (d, rIdx, rLow, tsi[rIdx]);
                }
            }
            return (-1, -1, double.NaN, double.NaN);
        }

        /// <summary>
        /// True when the TSI on the evaluated bar clears the short rolling-average momentum gate:
        /// TSI[bar] &lt;= SMA(TSI, period)[bar] (short momentum flat or falling). A non-positive period
        /// disables the gate; NaN in the TSI or its average fails the gate (reject, never pass).
        /// </summary>
        public static bool PassesShortMomentumGate(IReadOnlyList<double> tsi, IReadOnlyList<double> tsiAvg, int bar, int avgPeriod)
        {
            if (avgPeriod <= 1 || tsi == null || tsiAvg == null) return true;
            if (bar < 0 || bar >= tsi.Count || bar >= tsiAvg.Count) return false;
            double t = tsi[bar], a = tsiAvg[bar];
            if (double.IsNaN(t) || double.IsNaN(a)) return false;
            return t <= a;
        }

        /// <summary>
        /// Mirror of <see cref="PassesShortMomentumGate"/> for longs: TSI[bar] &gt;= SMA(TSI, period)[bar]
        /// (long momentum flat or rising). A non-positive period disables the gate; NaN fails it.
        /// </summary>
        public static bool PassesLongMomentumGate(IReadOnlyList<double> tsi, IReadOnlyList<double> tsiAvg, int bar, int avgPeriod)
        {
            if (avgPeriod <= 1 || tsi == null || tsiAvg == null) return true;
            if (bar < 0 || bar >= tsi.Count || bar >= tsiAvg.Count) return false;
            double t = tsi[bar], a = tsiAvg[bar];
            if (double.IsNaN(t) || double.IsNaN(a)) return false;
            return t >= a;
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
