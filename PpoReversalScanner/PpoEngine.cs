using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Four-state momentum coloring matching TradingView's official PPO and MACD indicator.
    /// </summary>
    public enum PpoHistogramState
    {
        None = 0,

        /// <summary>
        /// Positive and expanding momentum (hist >= 0 and hist > prevHist) -> Bright Teal (#26A69A).
        /// </summary>
        BullishGrowing = 1,

        /// <summary>
        /// Positive and contracting momentum (hist >= 0 and hist &lt;= prevHist) -> Faded Teal (#80CBC4 / #B2DFDB).
        /// </summary>
        BullishFading = 2,

        /// <summary>
        /// Negative and contracting momentum (hist &lt; 0 and hist > prevHist) -> Light Red / Pink (#FFCDD2).
        /// </summary>
        BearishFading = 3,

        /// <summary>
        /// Negative and expanding momentum (hist &lt; 0 and hist &lt;= prevHist) -> Bright Red (#FF5252).
        /// </summary>
        BearishGrowing = 4
    }

    /// <summary>
    /// Visual color scheme for the PPO Histogram.
    /// </summary>
    public enum PpoColorScheme
    {
        /// <summary>
        /// Exact 4-state gradient matching TradingView Pine Script v6.
        /// </summary>
        TradingView4Color = 0,

        /// <summary>
        /// Traditional 2-color display (Green for positive, Red for negative).
        /// </summary>
        ClassicTwoColor = 1
    }

    /// <summary>
    /// Type of crossover detected.
    /// </summary>
    public enum PpoCrossType
    {
        None = 0,
        BullishCross = 1,
        BearishCross = 2
    }

    /// <summary>
    /// Moving average calculation model for PPO oscillator and signal line.
    /// </summary>
    public enum PpoMaType
    {
        Exponential = 0,
        Simple = 1
    }

    /// <summary>
    /// Classification of the PPO setup based on direction and position relative to the zero line.
    /// </summary>
    public enum PpoSetupType
    {
        None = 0,

        /// <summary>
        /// Short: Upthrust (Bull Trap Dump) — Bear cross between 0.0% and +1.5%.
        /// Price breaks briefly above the consolidation range (retail buys the breakout),
        /// momentum fails immediately (RSI3 >= 85), and the PPO bear cross launches an aggressive liquidation wave downward.
        /// </summary>
        ShortUpthrust = 1,
        ShortReversal = 1,

        /// <summary>
        /// Short: Bear Flag Breakdown — Bear cross between -1.5% and 0.0%.
        /// In a macro downtrend (below 200 SMA), price bounces briefly, but lacks energy.
        /// The PPO bear cross triggers the resumption of the fall.
        /// </summary>
        ShortBearFlagBreakdown = 2,
        ShortContinuation = 2,

        /// <summary>
        /// Long: Terminal Shakeout (Spring) — Bull cross between -1.5% and 0.0%.
        /// Price dips briefly below the consolidation range (RSI3 sweep collects stops/liquidity),
        /// snaps immediately back into the range, and the PPO bull cross fires the expansion breakout upward.
        /// </summary>
        LongTerminalShakeout = 3,
        LongReversal = 3,

        /// <summary>
        /// Long: Bull Flag Volatility Expansion — Bull cross between 0.0% and +1.5%.
        /// In a strong uptrend (above 200 SMA), price consolidates in a flag.
        /// A rapid 2-day dip touches RSI3 &lt;= 15, PPO crosses, and the trend explodes upward.
        /// </summary>
        LongBullFlagExpansion = 4,
        LongContinuation = 4
    }

    /// <summary>
    /// Trade direction for PPO setups.
    /// </summary>
    public enum PpoTradeDirection
    {
        None = 0,
        Long = 1,
        Short = 2
    }

    /// <summary>
    /// Output result of the PPO Reversal &amp; Continuation evaluation.
    /// </summary>
    public readonly struct PpoReversalSetupResult
    {
        public readonly bool IsTriggered;
        public readonly PpoTradeDirection Direction;
        public readonly PpoSetupType SetupType;
        public readonly double PpoLine;
        public readonly double SignalLine;
        public readonly double Histogram;
        public readonly double CurrentRsi3;
        public readonly double CurrentRsi14;
        public readonly double HistoricalExtremeRsi3;
        public double FootprintExtremeRsi14 => HistoricalExtremeRsi3;
        public readonly int BarsSinceExtreme;
        public readonly string SetupDescription;
        public readonly string RejectReason;
        public readonly double Clv;
        public readonly double RsDelta20;

        public PpoReversalSetupResult(
            bool isTriggered,
            PpoTradeDirection direction,
            PpoSetupType setupType,
            double ppoLine,
            double signalLine,
            double histogram,
            double currentRsi3,
            double currentRsi14,
            double historicalExtremeRsi3,
            int barsSinceExtreme,
            string setupDescription,
            string rejectReason,
            double clv = double.NaN,
            double rsDelta20 = double.NaN)
        {
            IsTriggered = isTriggered;
            Direction = direction;
            SetupType = setupType;
            PpoLine = ppoLine;
            SignalLine = signalLine;
            Histogram = histogram;
            CurrentRsi3 = currentRsi3;
            CurrentRsi14 = currentRsi14;
            HistoricalExtremeRsi3 = historicalExtremeRsi3;
            BarsSinceExtreme = barsSinceExtreme;
            SetupDescription = setupDescription ?? "";
            RejectReason = rejectReason ?? "";
            Clv = clv;
            RsDelta20 = rsDelta20;
        }

        public PpoReversalSetupResult(
            bool isTriggered,
            PpoTradeDirection direction,
            PpoSetupType setupType,
            double ppoLine,
            double signalLine,
            double histogram,
            double currentRsi3,
            double historicalExtremeRsi3,
            int barsSinceExtreme,
            string setupDescription,
            string rejectReason)
            : this(isTriggered, direction, setupType, ppoLine, signalLine, histogram, currentRsi3, double.NaN, historicalExtremeRsi3, barsSinceExtreme, setupDescription, rejectReason, double.NaN, double.NaN)
        {
        }
    }

    /// <summary>
    /// Pure C# canonical calculation engine for the Percentage Price Oscillator (PPO).
    /// Zero dependencies on cAlgo APIs — 100% unit testable, reproducible, and deterministic.
    /// Used identically by both PpoOscillator (Indicator) and PpoReversalScanner (cBot).
    /// </summary>
    public static class PpoEngine
    {
        // =========================================================================
        // --- 1. CORE MOVING AVERAGE & PPO CALCULATIONS ---
        // =========================================================================

        /// <summary>
        /// Computes Exponential Moving Average (EMA) series matching cTrader and TradingView Pine Script v6:
        /// Seeds with the first valid non-NaN value on bar 0, then recurses:
        /// y[t] = alpha * x[t] + (1 - alpha) * y[t-1] with alpha = 2 / (period + 1).
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
                if (double.IsNaN(val))
                {
                    result[i] = double.NaN;
                    continue;
                }

                if (double.IsNaN(prev))
                {
                    prev = val; // Seed with first valid bar
                }
                else
                {
                    prev = (val * alpha) + (prev * (1.0 - alpha));
                }

                result[i] = prev;
            }

            return result;
        }

        /// <summary>
        /// Computes Simple Moving Average (SMA) series:
        /// Returns NaN before (period - 1), then sliding window sum / period.
        /// Uses exact direct summation to match incremental step calculation bit-for-bit.
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
                    if (double.IsNaN(val))
                    {
                        ok = false;
                        break;
                    }
                    sum += val;
                }

                result[i] = ok ? (sum / period) : double.NaN;
            }

            return result;
        }

        /// <summary>
        /// Computes Moving Average series for the specified MA type.
        /// </summary>
        public static double[] ComputeMa(IReadOnlyList<double> source, int period, PpoMaType maType)
        {
            return maType == PpoMaType.Simple
                ? ComputeSma(source, period)
                : ComputeEma(source, period);
        }

        /// <summary>
        /// Computes the Percentage Price Oscillator (PPO), Signal Line, and Histogram:
        /// - Fast MA = EMA or SMA of source
        /// - Slow MA = EMA or SMA of source
        /// - PPO Line = 100.0 * (Fast MA - Slow MA) / Slow MA
        /// - Signal Line = EMA or SMA of PPO Line
        /// - Histogram = PPO Line - Signal Line
        /// 
        /// Matches cTrader and TradingView Pine Script v6 Percentage Price Oscillator 1:1.
        /// </summary>
        public static (double[] PpoLine, double[] SignalLine, double[] Histogram) ComputePpo(
            IReadOnlyList<double> closes,
            int fastPeriod = 12,
            int slowPeriod = 26,
            int signalPeriod = 9,
            PpoMaType oscMaType = PpoMaType.Exponential,
            PpoMaType sigMaType = PpoMaType.Exponential)
        {
            if (closes == null) throw new ArgumentNullException(nameof(closes));
            if (fastPeriod < 1) throw new ArgumentOutOfRangeException(nameof(fastPeriod));
            if (slowPeriod <= fastPeriod) throw new ArgumentException("Slow period must be greater than fast period", nameof(slowPeriod));
            if (signalPeriod < 1) throw new ArgumentOutOfRangeException(nameof(signalPeriod));

            int n = closes.Count;
            double[] ppoLine = new double[n];
            double[] signalLine = new double[n];
            double[] histogram = new double[n];

            for (int i = 0; i < n; i++)
            {
                ppoLine[i] = double.NaN;
                signalLine[i] = double.NaN;
                histogram[i] = double.NaN;
            }

            if (n < slowPeriod) return (ppoLine, signalLine, histogram);

            double[] fastMa = ComputeMa(closes, fastPeriod, oscMaType);
            double[] slowMa = ComputeMa(closes, slowPeriod, oscMaType);

            // Compute PPO Line
            for (int i = 0; i < n; i++)
            {
                ppoLine[i] = ComputePpoValue(fastMa[i], slowMa[i]);
            }

            // Compute Signal Line
            signalLine = ComputeMa(ppoLine, signalPeriod, sigMaType);

            // Compute Histogram
            for (int i = 0; i < n; i++)
            {
                if (!double.IsNaN(ppoLine[i]) && !double.IsNaN(signalLine[i]))
                {
                    histogram[i] = ppoLine[i] - signalLine[i];
                }
            }

            return (ppoLine, signalLine, histogram);
        }

        // =========================================================================
        // --- 2. STEP-BASED INCREMENTAL METHODS (FOR LIVE STREAMING / INDICATOR) ---
        // =========================================================================

        /// <summary>
        /// Computes the PPO line percentage value from fast and slow moving averages:
        /// PPO = 100.0 * (FastMA - SlowMA) / SlowMA
        /// </summary>
        public static double ComputePpoValue(double fastMa, double slowMa)
        {
            if (double.IsNaN(fastMa) || double.IsNaN(slowMa) || Math.Abs(slowMa) < 1e-12)
                return double.NaN;

            return 100.0 * (fastMa - slowMa) / slowMa;
        }

        /// <summary>
        /// Incremental EMA step calculation:
        /// val[t] = alpha * current + (1 - alpha) * prev
        /// with alpha = 2.0 / (period + 1.0).
        /// Seeds with current value if prev is NaN.
        /// </summary>
        public static double ComputeSignalEmaStep(double currentVal, double prevVal, int period)
        {
            if (double.IsNaN(currentVal) || period < 1)
                return double.NaN;

            if (double.IsNaN(prevVal))
                return currentVal;

            double alpha = 2.0 / (period + 1.0);
            return (currentVal * alpha) + (prevVal * (1.0 - alpha));
        }

        /// <summary>
        /// Incremental SMA calculation using a value-lookup function.
        /// Returns double.NaN if index &lt; period - 1 or if any value is NaN.
        /// </summary>
        public static double ComputeSignalSmaStep(Func<int, double> getValue, int index, int period)
        {
            if (getValue == null || period < 1 || index < period - 1)
                return double.NaN;

            double sum = 0.0;
            int start = index - period + 1;
            for (int i = start; i <= index; i++)
            {
                double val = getValue(i);
                if (double.IsNaN(val)) return double.NaN;
                sum += val;
            }

            return sum / period;
        }

        /// <summary>
        /// Incremental SMA calculation from a list of values.
        /// </summary>
        public static double ComputeSignalSmaStep(IReadOnlyList<double> values, int index, int period)
        {
            if (values == null || period < 1 || index < period - 1 || index >= values.Count)
                return double.NaN;

            return ComputeSignalSmaStep(i => values[i], index, period);
        }

        // =========================================================================
        // --- 3. STATE CLASSIFICATION & CROSSOVER EVALUATION ---
        // =========================================================================

        /// <summary>
        /// Classifies the histogram value into the 4-state momentum model matching TradingView:
        /// - hist >= 0 &amp;&amp; hist > prevHist: BullishGrowing (Bright Teal)
        /// - hist >= 0 &amp;&amp; hist &lt;= prevHist: BullishFading (Faded Teal)
        /// - hist &lt; 0 &amp;&amp; hist > prevHist: BearishFading (Faded Red)
        /// - hist &lt; 0 &amp;&amp; hist &lt;= prevHist: BearishGrowing (Bright Red)
        /// </summary>
        public static PpoHistogramState ClassifyHistogram(double currentHist, double prevHist)
        {
            if (double.IsNaN(currentHist))
                return PpoHistogramState.None;

            if (double.IsNaN(prevHist))
            {
                return currentHist >= 0.0 ? PpoHistogramState.BullishGrowing : PpoHistogramState.BearishGrowing;
            }

            if (currentHist >= 0.0)
            {
                return currentHist > prevHist ? PpoHistogramState.BullishGrowing : PpoHistogramState.BullishFading;
            }
            else
            {
                return currentHist > prevHist ? PpoHistogramState.BearishFading : PpoHistogramState.BearishGrowing;
            }
        }

        /// <summary>
        /// Evaluates whether a crossover occurred between PPO and Signal line.
        /// </summary>
        public static PpoCrossType EvaluateCross(double currentPpo, double currentSignal, double prevPpo, double prevSignal)
        {
            if (double.IsNaN(currentPpo) || double.IsNaN(currentSignal) ||
                double.IsNaN(prevPpo) || double.IsNaN(prevSignal))
            {
                return PpoCrossType.None;
            }

            bool wasUnderOrEqual = prevPpo <= prevSignal;
            bool isAbove = currentPpo > currentSignal;
            if (wasUnderOrEqual && isAbove)
                return PpoCrossType.BullishCross;

            bool wasAboveOrEqual = prevPpo >= prevSignal;
            bool isUnder = currentPpo < currentSignal;
            if (wasAboveOrEqual && isUnder)
                return PpoCrossType.BearishCross;

            return PpoCrossType.None;
        }

        /// <summary>
        /// Evaluates whether a PPO Line / Signal Line cross occurred today.
        /// - Short (Bear Cross): PPO was &gt;= Signal on previous bar, and is &lt; Signal today.
        /// - Long (Bull Cross): PPO was &lt;= Signal on previous bar, and is &gt; Signal today.
        /// </summary>
        public static (bool IsCross, string Reason) EvaluatePpoCross(
            double prevPpo,
            double prevSignal,
            double currPpo,
            double currSignal,
            bool isShort = true)
        {
            var cross = EvaluateCross(currPpo, currSignal, prevPpo, prevSignal);
            if (isShort)
            {
                if (cross == PpoCrossType.BearishCross)
                {
                    return (true, $"PPO Bear Cross Confirmed: Crossed under Signal ({currPpo:F2}% < {currSignal:F2}%)");
                }

                return (false, $"No Bear Cross: prev diff {prevPpo - prevSignal:F3}, curr diff {currPpo - currSignal:F3}");
            }
            else
            {
                if (cross == PpoCrossType.BullishCross)
                {
                    return (true, $"PPO Bull Cross Confirmed: Crossed above Signal ({currPpo:F2}% > {currSignal:F2}%)");
                }

                return (false, $"No Bull Cross: prev diff {prevPpo - prevSignal:F3}, curr diff {currPpo - currSignal:F3}");
            }
        }

        /// <summary>
        /// Evaluates the Close Location Value (CLV) of the current bar (e.g. at 14:15 EST or closed bar):
        /// CLV = (CurrentPrice - Low) / (High - Low)
        /// Range is [0.0, 1.0].
        /// Long filter: CLV >= minClvLong (default 0.65). Price must be in the upper 35% of the daily range (buyers maintain control).
        /// Short filter: CLV <= maxClvShort (default 0.35). Price must be in the lower 35% of the daily range (sellers maintain control).
        /// Rejects long upper wicks on longs or long lower wicks on shorts (price rejection / fading).
        /// If High == Low (zero range), CLV defaults to 0.50 (neutral midpoint).
        /// </summary>
        public static (bool Passes, double Clv, string Reason) EvaluateClv(
            bool isLong,
            double currentPrice,
            double high,
            double low,
            double minClvLong = 0.65,
            double maxClvShort = 0.35)
        {
            if (double.IsNaN(currentPrice) || double.IsNaN(high) || double.IsNaN(low))
                return (false, double.NaN, "CLV input price/high/low is NaN");

            double range = high - low;
            double clv;
            if (range <= 1e-9)
            {
                clv = 0.50; // Zero range / flat bar: neutral midpoint
            }
            else
            {
                clv = (currentPrice - low) / range;
                if (clv < 0.0) clv = 0.0;
                else if (clv > 1.0) clv = 1.0;
            }

            if (isLong)
            {
                if (clv >= minClvLong)
                {
                    return (true, clv, $"CLV OK: {clv:F2} >= {minClvLong:F2} (Price in top {(1.0 - minClvLong) * 100:F0}% of daily range, buyers dominant)");
                }
                return (false, clv, $"CLV Rejected: {clv:F2} < {minClvLong:F2} (Price faded into middle/bottom of range, buyers lost initiative)");
            }
            else
            {
                if (clv <= maxClvShort)
                {
                    return (true, clv, $"CLV OK: {clv:F2} <= {maxClvShort:F2} (Price in bottom {maxClvShort * 100:F0}% of daily range, sellers dominant)");
                }
                return (false, clv, $"CLV Rejected: {clv:F2} > {maxClvShort:F2} (Price bounced into middle/top of range, sellers lost initiative)");
            }
        }

        /// <summary>
        /// Computes the 50% retracement level of the cross candle:
        /// LimitPrice = (High + Low) / 2.0.
        /// Used for EOD scan limit order entry on the next trading day.
        /// </summary>
        public static double Compute50PctRetrace(double high, double low)
        {
            if (double.IsNaN(high) || double.IsNaN(low))
                return double.NaN;
            return (high + low) / 2.0;
        }

        /// <summary>
        /// Computes a Fibonacci retracement limit price for the signal candle.
        /// Long: retraces down from the high by <paramref name="pct"/> of the range.
        /// Short: retraces up from the low by <paramref name="pct"/> of the range.
        /// <paramref name="pct"/> = 0.6818 for 68.18%, 0.50 for 50%, 0.618 for 61.8%.
        /// </summary>
        public static double ComputeRetracementLimit(double high, double low, bool isShort, double pct = 0.6818)
        {
            if (double.IsNaN(high) || double.IsNaN(low))
                return double.NaN;
            double range = high - low;
            if (range <= 0)
                return high;
            return isShort
                ? low + pct * range
                : high - pct * range;
        }

        // =========================================================================
        // --- 4. RSI(14) FOOTPRINT & WYCKOFF SETUP DETERMINATION ---
        // =========================================================================

        /// <summary>
        /// Computes the minimum and maximum Daily RSI values over the preceding lookback window (default 7 days),
        /// along with the recency (bars ago relative to evalIdx) of the most recent capitulation dip (<= minThreshold)
        /// and most recent power thrust peak (>= maxThreshold).
        /// Specifically scans closed bars [evalIdx - 1 - lookbackBars .. evalIdx - 2] (bars [2 .. 1 + lookbackBars] relative to live bar [0]).
        /// </summary>
        public static (double MinRsi, double MaxRsi, int MinBarsAgo, int MaxBarsAgo) ComputePriorRsiExtremesWithRecency(
            IReadOnlyList<double> rsiSeries,
            int evalIdx,
            int lookbackBars = 30,
            double minThreshold = 35.0,
            double maxThreshold = 65.0)
        {
            if (rsiSeries == null || lookbackBars <= 0 || evalIdx < 1)
                return (double.NaN, double.NaN, -1, -1);

            int startIdx = Math.Max(0, evalIdx - lookbackBars);
            int endIdx = evalIdx - 1;

            if (startIdx > endIdx)
                return (double.NaN, double.NaN, -1, -1);

            double minVal = double.MaxValue;
            double maxVal = double.MinValue;
            int mostRecentMinBarsAgo = -1;
            int mostRecentMaxBarsAgo = -1;
            bool found = false;

            for (int k = endIdx; k >= startIdx; k--)
            {
                double val = rsiSeries[k];
                if (double.IsNaN(val)) continue;
                if (val < minVal) minVal = val;
                if (val > maxVal) maxVal = val;
                found = true;

                int barsAgo = evalIdx - k;

                if (val <= minThreshold && mostRecentMinBarsAgo == -1)
                {
                    mostRecentMinBarsAgo = barsAgo;
                }
                if (val >= maxThreshold && mostRecentMaxBarsAgo == -1)
                {
                    mostRecentMaxBarsAgo = barsAgo;
                }
            }

            if (!found)
                return (double.NaN, double.NaN, -1, -1);

            return (minVal, maxVal, mostRecentMinBarsAgo, mostRecentMaxBarsAgo);
        }

        /// <summary>
        /// Evaluates the Unified PPO &amp; Daily RSI(14) Polarity Matrix (30-Day Lookback):
        /// - Long Continuation (Power Flag): RSI cooled to &lt;= 45.0 in 30-day footprint. Close[1] > 50 EMA[1]. No OS (&lt;= 35.0).
        /// - Long Reversal (Wyckoff Spring): Capitulation dip (Min RSI &lt;= 35.0) in 30-day footprint. No opposing extreme check.
        /// - Short Continuation (Bear Flag): RSI relieved to &gt;= 55.0 in 30-day footprint. Close[1] &lt; 50 EMA[1]. No OB (&gt;= 65.0).
        /// - Short Reversal (Wyckoff Upthrust): Blow-off peak (Max RSI &gt;= 65.0) in 30-day footprint. Close &lt; 200 SMA. No opposing extreme check.
        /// </summary>
        public static (bool Passes, PpoSetupType SetupType, string SetupName, double RunwayAtr, string Reason) EvaluatePpoRsi14PolarityMatrix(
            double ppoLine,
            double bar1Close,
            double bar150Ema,
            double bar1Rsi14,
            double dailyAtr,
            double currentClose,
            double daily200Sma,
            bool isShort,
            bool require200Sma = true,
            double priorMinRsi12 = double.NaN,
            double priorMaxRsi12 = double.NaN,
            double longReversalRsiMax = 35.0,
            double shortReversalRsiMin = 65.0,
            double longContinuationRsiMin = 45.0,
            double shortContinuationRsiMax = 55.0,
            bool require50Ema = true,
            double sma200BufferAtr = 0.50)
        {
            if (double.IsNaN(ppoLine) || double.IsNaN(currentClose))
                return (false, PpoSetupType.None, "", double.NaN, "Invalid PPO line or Close price");

            // 1. Secular 200 SMA check (Strict: Longs > 200 SMA, Shorts < 200 SMA; 0.5 ATR neutral zone removed)
            if (require200Sma)
            {
                if (double.IsNaN(daily200Sma) || daily200Sma <= 0)
                    return (false, PpoSetupType.None, "", double.NaN, "Daily 200 SMA is invalid");

                if (!isShort)
                {
                    if (bar1Close <= daily200Sma)
                    {
                        return (false, PpoSetupType.None, "", double.NaN,
                            $"Secular 200 SMA Blocked: Long requires Close[1] (${bar1Close:F2}) > 200 SMA (${daily200Sma:F2})");
                    }
                    if (!double.IsNaN(currentClose) && currentClose <= daily200Sma)
                    {
                        return (false, PpoSetupType.None, "", double.NaN,
                            $"Secular 200 SMA Blocked: Long requires Close (${currentClose:F2}) > 200 SMA (${daily200Sma:F2})");
                    }
                }
                else
                {
                    if (bar1Close >= daily200Sma)
                    {
                        return (false, PpoSetupType.None, "", double.NaN,
                            $"Secular 200 SMA Blocked: Short requires Close[1] (${bar1Close:F2}) < 200 SMA (${daily200Sma:F2})");
                    }
                    if (!double.IsNaN(currentClose) && currentClose >= daily200Sma)
                    {
                        return (false, PpoSetupType.None, "", double.NaN,
                            $"Secular 200 SMA Blocked: Short requires Close (${currentClose:F2}) < 200 SMA (${daily200Sma:F2})");
                    }
                }
            }

            // 2. Bar [1] Close validity
            if (double.IsNaN(bar1Close))
                return (false, PpoSetupType.None, "", double.NaN, "Bar [1] Close is invalid");

            if (!isShort)
            {
                // ==========================================
                // LONG REGIME (Above 200 SMA):
                // - Long Reversal (Wyckoff Spring): Proven capitulation dip (Min RSI <= 35.0 in footprint).
                // - Long Continuation (Power Flag): No oversold dip, healthy RSI-afkoeling (Min RSI <= 45.0 in footprint).
                // ==========================================
                bool isReversal = !double.IsNaN(priorMinRsi12) && priorMinRsi12 <= longReversalRsiMax;
                bool isContinuation = !isReversal;

                if (isContinuation)
                {
                    // -------------------------------------------------------------
                    // QUADRANT 1: LONG CONTINUATION (POWER FLAG)
                    // -------------------------------------------------------------
                    // 1. RSI-Afkoeling Check: Not overbought currently on setup candle (Bar [1] RSI <= 65.0)
                    if (!double.IsNaN(bar1Rsi14) && bar1Rsi14 > 65.0)
                    {
                        return (false, PpoSetupType.LongBullFlagExpansion, "Long Continuation (Power Flag)", double.NaN,
                            $"PPO Matrix Blocked: Long Continuation requires RSI-Afkoeling (Bar[1] RSI was {bar1Rsi14:F1} > 65.0) [Overbought Expansion]");
                    }
                    // 1b. RSI-Afkoeling Footprint: Must have cooled to <= longContinuationRsiMin (default 45.0) in lookback
                    if (!double.IsNaN(priorMinRsi12) && priorMinRsi12 > longContinuationRsiMin)
                    {
                        return (false, PpoSetupType.LongBullFlagExpansion, "Long Continuation (Power Flag)", double.NaN,
                            $"PPO Matrix Blocked: Long Continuation requires RSI-Afkoeling in footprint (Min RSI was {priorMinRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} > {longContinuationRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [No Healthy Pullback / Never Cooled]");
                    }
                    // Optional explicit thrust floor when caller explicitly sets a thrust requirement (e.g. >= 60.0):
                    if (longContinuationRsiMin > 60.0 && !double.IsNaN(priorMaxRsi12) && priorMaxRsi12 < longContinuationRsiMin)
                    {
                        return (false, PpoSetupType.LongBullFlagExpansion, "Long Continuation (Power Flag)", double.NaN,
                            $"PPO Matrix Blocked: Long Continuation requires proven power thrust in footprint (Max RSI was {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} < {longContinuationRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Insufficient Buying Force]");
                    }

                    // 2. No Opposing Oversold: Must NOT have touched capitulation dip (<= 35.0) in lookback
                    if (!double.IsNaN(priorMinRsi12) && priorMinRsi12 <= longReversalRsiMax)
                    {
                        return (false, PpoSetupType.LongBullFlagExpansion, "Long Continuation (Power Flag)", double.NaN,
                            $"PPO Matrix Blocked: Long Continuation touched opposing oversold extreme in footprint (Min RSI was {priorMinRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} <= {longReversalRsiMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Capitulation Dump — Reversal Not Continuation]");
                    }

                    string thrustNote = (longContinuationRsiMin > 60.0 && !double.IsNaN(priorMaxRsi12)) ? $", Prior Thrust {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} >= {longContinuationRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" : "";
                    return (true, PpoSetupType.LongBullFlagExpansion, "Long Continuation (Power Flag)", double.PositiveInfinity,
                        $"PPO Matrix OK: Long Continuation (Power Flag) (PPO {ppoLine:F2}%, Close > 200 SMA{thrustNote}, RSI-Afkoeling OK)");
                }
                else
                {
                    // -------------------------------------------------------------
                    // QUADRANT 2: LONG REVERSAL (WYCKOFF SPRING)
                    // -------------------------------------------------------------
                    // 1. Footprint Check: Requires proven capitulation (Min RSI <= 35.0)
                    if (!double.IsNaN(priorMinRsi12) && priorMinRsi12 > longReversalRsiMax)
                    {
                        return (false, PpoSetupType.LongTerminalShakeout, "Long Reversal (Wyckoff Spring)", double.NaN,
                            $"PPO Matrix Blocked: Long Spring requires proven capitulation in footprint (Min RSI was {priorMinRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} > {longReversalRsiMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Insufficient Shakeout]");
                    }
                    if (double.IsNaN(priorMinRsi12) && !double.IsNaN(priorMaxRsi12))
                    {
                        return (false, PpoSetupType.LongTerminalShakeout, "Long Reversal (Wyckoff Spring)", double.NaN,
                            $"PPO Matrix Blocked: Long Spring requires proven capitulation in footprint [Insufficient Shakeout]");
                    }

                    string shakeNote = !double.IsNaN(priorMinRsi12) ? $", Capitulation Dip {priorMinRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} <= {longReversalRsiMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" : "";
                    return (true, PpoSetupType.LongTerminalShakeout, "Long Reversal (Wyckoff Spring)", double.NaN,
                        $"PPO Matrix OK: Long Reversal (Wyckoff Spring) (PPO {ppoLine:F2}%{shakeNote}, Close > 200 SMA)");
                }
            }
            else
            {
                // ==========================================
                // SHORT REGIME (Below 200 SMA):
                // - Short Reversal (Wyckoff Upthrust): Proven blow-off peak (Max RSI >= 65.0 in footprint).
                // - Short Continuation (Bear Flag): No overbought peak, healthy RSI relief bounce (Max RSI >= 55.0 in footprint).
                // ==========================================
                bool isReversal = !double.IsNaN(priorMaxRsi12) && priorMaxRsi12 >= shortReversalRsiMin;
                bool isContinuation = !isReversal;

                if (isContinuation)
                {
                    // -------------------------------------------------------------
                    // QUADRANT 3: SHORT CONTINUATION (BEAR FLAG)
                    // -------------------------------------------------------------
                    // 1. RSI-Relief / Bounce Check: Not oversold currently on setup candle (Bar [1] RSI >= 35.0)
                    if (!double.IsNaN(bar1Rsi14) && bar1Rsi14 < 35.0)
                    {
                        return (false, PpoSetupType.ShortBearFlagBreakdown, "Short Continuation (Bear Flag)", double.NaN,
                            $"PPO Matrix Blocked: Short Continuation requires RSI relief bounce (Bar[1] RSI was {bar1Rsi14:F1} < 35.0) [Oversold Breakdown]");
                    }
                    // 1b. RSI-Relief Footprint: Must have bounced to >= shortContinuationRsiMax (default 55.0) in lookback
                    if (!double.IsNaN(priorMaxRsi12) && priorMaxRsi12 < shortContinuationRsiMax)
                    {
                        return (false, PpoSetupType.ShortBearFlagBreakdown, "Short Continuation (Bear Flag)", double.NaN,
                            $"PPO Matrix Blocked: Short Continuation requires RSI-Relief in footprint (Max RSI was {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} < {shortContinuationRsiMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [No Healthy Bounce / Never Relieved]");
                    }
                    // Optional explicit dump floor when caller explicitly sets a dump requirement (e.g. <= 40.0):
                    if (shortContinuationRsiMax < 40.0 && !double.IsNaN(priorMinRsi12) && priorMinRsi12 > shortContinuationRsiMax)
                    {
                        return (false, PpoSetupType.ShortBearFlagBreakdown, "Short Continuation (Bear Flag)", double.NaN,
                            $"PPO Matrix Blocked: Short Continuation requires proven breakdown dump in footprint (Min RSI was {priorMinRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} > {shortContinuationRsiMax.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Insufficient Selling Force]");
                    }

                    // 2. No Opposing Overbought: Must NOT have touched blow-off peak (>= 65.0) in lookback
                    if (!double.IsNaN(priorMaxRsi12) && priorMaxRsi12 >= shortReversalRsiMin)
                    {
                        return (false, PpoSetupType.ShortBearFlagBreakdown, "Short Continuation (Bear Flag)", double.NaN,
                            $"PPO Matrix Blocked: Short Continuation touched opposing overbought extreme in footprint (Max RSI was {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} >= {shortReversalRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Blow-Off Squeeze — Reversal Not Continuation]");
                    }

                    return (true, PpoSetupType.ShortBearFlagBreakdown, "Short Continuation (Bear Flag)", double.PositiveInfinity,
                        $"PPO Matrix OK: Short Continuation (Bear Flag) (PPO {ppoLine:F2}%, Close < 200 SMA, RSI Relief OK)");
                }
                else
                {
                    // -------------------------------------------------------------
                    // QUADRANT 4: SHORT REVERSAL (WYCKOFF UPTHRUST)
                    // -------------------------------------------------------------
                    // 1. Footprint Check: Requires proven blow-off peak (Max RSI >= 65.0)
                    if (!double.IsNaN(priorMaxRsi12) && priorMaxRsi12 < shortReversalRsiMin)
                    {
                        return (false, PpoSetupType.ShortUpthrust, "Short Reversal (Wyckoff Upthrust)", double.NaN,
                            $"PPO Matrix Blocked: Short Upthrust requires proven blow-off in footprint (Max RSI was {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} < {shortReversalRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}) [Insufficient Exhaustion]");
                    }
                    if (double.IsNaN(priorMaxRsi12) && !double.IsNaN(priorMinRsi12))
                    {
                        return (false, PpoSetupType.ShortUpthrust, "Short Reversal (Wyckoff Upthrust)", double.NaN,
                            $"PPO Matrix Blocked: Short Upthrust requires proven blow-off in footprint [Insufficient Exhaustion]");
                    }

                    string blowNote = !double.IsNaN(priorMaxRsi12) ? $", Blow-Off Peak {priorMaxRsi12.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} >= {shortReversalRsiMin.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}" : "";
                    return (true, PpoSetupType.ShortUpthrust, "Short Reversal (Wyckoff Upthrust)", double.NaN,
                        $"PPO Matrix OK: Short Reversal (Wyckoff Upthrust) (PPO {ppoLine:F2}%{blowNote}, Close < 200 SMA)");
                }
            }
        }

        /// <summary>
        /// Pure Daily RSI Polarity orchestrator evaluating PPO Reversal &amp; Continuation setups.
        /// Evaluates PPO cross, RSI[1] in [40, 60], 10-day AvgRSI polarity, 50 EMA slope (for flags),
        /// 30-day prior RSI catalyst (<=35.0 / >=65.0 for reversals, 40-50 afkoeling for flags).
        /// </summary>
        public static PpoReversalSetupResult EvaluatePpoReversalSetupRsi14(
            IReadOnlyList<double> rsi14Series,
            IReadOnlyList<double> ppoLineSeries,
            IReadOnlyList<double> signalLineSeries,
            int evalIdx,
            double bar1Close,
            double bar150Ema = double.NaN,
            double dailyAtr = double.NaN,
            double currentClose = double.NaN,
            double daily200Sma = double.NaN,
            bool require200Sma = true,
            bool isShort = true,
            double bar0High = double.NaN,
            double bar0Low = double.NaN,
            bool requireClv = false,
            double minClvLong = 0.65,
            double maxClvShort = 0.35,
            int reversalCatalystLookbackBars = 10,
            double longReversalRsiMax = 35.0,
            double shortReversalRsiMin = 65.0,
            double longContinuationRsiMin = 45.0,
            double shortContinuationRsiMax = 55.0,
            double rsDelta20 = double.NaN,
            bool require50Ema = false,
            double sma200BufferAtr = 0.0)
        {
            if (rsi14Series == null || ppoLineSeries == null || signalLineSeries == null)
                return new PpoReversalSetupResult(false, PpoTradeDirection.None, PpoSetupType.None, 0, 0, 0, 0, double.NaN, 0, -1, "", "Null input series");

            if (evalIdx < 1 || evalIdx >= rsi14Series.Count || evalIdx >= ppoLineSeries.Count || evalIdx >= signalLineSeries.Count)
                return new PpoReversalSetupResult(false, PpoTradeDirection.None, PpoSetupType.None, 0, 0, 0, 0, double.NaN, 0, -1, "", "Index out of range");

            double currPpo = ppoLineSeries[evalIdx];
            double prevPpo = ppoLineSeries[evalIdx - 1];
            double currSignal = signalLineSeries[evalIdx];
            double prevSignal = signalLineSeries[evalIdx - 1];
            double currRsi = rsi14Series[evalIdx];
            double bar1Rsi = rsi14Series[evalIdx - 1];
            double histogram = currPpo - currSignal;

            var tradeDir = isShort ? PpoTradeDirection.Short : PpoTradeDirection.Long;

            // 1. PPO Cross Check (Unconstrained by corridor: cross is 100% binary timing trigger)
            var crossCheck = EvaluatePpoCross(prevPpo, prevSignal, currPpo, currSignal, isShort);
            if (!crossCheck.IsCross)
            {
                return new PpoReversalSetupResult(false, tradeDir, PpoSetupType.None, currPpo, currSignal, histogram, currRsi, bar1Rsi, double.NaN, -1, "", crossCheck.Reason, double.NaN, rsDelta20);
            }

            // 2. Compute prior RSI extremes over closed bars [evalIdx - 1 - lookback .. evalIdx - 2] with recency (30-day footprint)
            var (priorMinRsi, priorMaxRsi, priorMinRsiBarsAgo, priorMaxRsiBarsAgo) = ComputePriorRsiExtremesWithRecency(
                rsi14Series, evalIdx, reversalCatalystLookbackBars, longReversalRsiMax, shortReversalRsiMin);

            // 3. Evaluate the Polarity Matrix
            var matrixCheck = EvaluatePpoRsi14PolarityMatrix(
                currPpo, bar1Close, bar150Ema, bar1Rsi, dailyAtr, currentClose, daily200Sma,
                isShort, require200Sma, priorMinRsi, priorMaxRsi, longReversalRsiMax, shortReversalRsiMin,
                longContinuationRsiMin, shortContinuationRsiMax,
                require50Ema, sma200BufferAtr);

            if (!matrixCheck.Passes)
            {
                return new PpoReversalSetupResult(false, tradeDir, PpoSetupType.None, currPpo, currSignal, histogram, currRsi, bar1Rsi, double.NaN, -1, "", matrixCheck.Reason, double.NaN, rsDelta20);
            }

            // 5. Close Location Value (CLV) Filter
            double clv = double.NaN;
            if (requireClv)
            {
                var clvCheck = EvaluateClv(!isShort, currentClose, bar0High, bar0Low, minClvLong, maxClvShort);
                clv = clvCheck.Clv;
                if (!clvCheck.Passes)
                {
                    return new PpoReversalSetupResult(false, tradeDir, PpoSetupType.None, currPpo, currSignal, histogram, currRsi, bar1Rsi, double.NaN, -1, "", clvCheck.Reason, clv, rsDelta20);
                }
            }
            else if (!double.IsNaN(bar0High) && !double.IsNaN(bar0Low))
            {
                var clvCheck = EvaluateClv(!isShort, currentClose, bar0High, bar0Low, minClvLong, maxClvShort);
                clv = clvCheck.Clv;
            }

            double histExtreme;
            int barsSinceExtreme;
            switch (matrixCheck.SetupType)
            {
                case PpoSetupType.LongTerminalShakeout:
                    histExtreme = priorMinRsi;
                    barsSinceExtreme = priorMinRsiBarsAgo > 0 ? priorMinRsiBarsAgo : 1;
                    break;
                case PpoSetupType.ShortUpthrust:
                    histExtreme = priorMaxRsi;
                    barsSinceExtreme = priorMaxRsiBarsAgo > 0 ? priorMaxRsiBarsAgo : 1;
                    break;
                case PpoSetupType.LongBullFlagExpansion:
                    histExtreme = priorMinRsi;
                    barsSinceExtreme = priorMinRsiBarsAgo > 0 ? priorMinRsiBarsAgo : 1;
                    break;
                case PpoSetupType.ShortBearFlagBreakdown:
                    histExtreme = priorMaxRsi;
                    barsSinceExtreme = priorMaxRsiBarsAgo > 0 ? priorMaxRsiBarsAgo : 1;
                    break;
                default:
                    histExtreme = priorMaxRsi;
                    barsSinceExtreme = 1;
                    break;
            }

            return new PpoReversalSetupResult(
                isTriggered: true,
                direction: tradeDir,
                setupType: matrixCheck.SetupType,
                ppoLine: currPpo,
                signalLine: currSignal,
                histogram: histogram,
                currentRsi3: currRsi, // live forming RSI
                currentRsi14: bar1Rsi, // closed bar [1] RSI
                historicalExtremeRsi3: histExtreme,
                barsSinceExtreme: barsSinceExtreme,
                setupDescription: matrixCheck.SetupName,
                rejectReason: "",
                clv: clv,
                rsDelta20: rsDelta20);
        }

        /// <summary>
        /// Computes the 20-day Outperformance Delta (Relative Strength vs Benchmark / SPX) over closed daily bars:
        /// RS_Delta20 = ((CloseStock[1] - CloseStock[20]) / CloseStock[20] - (CloseSPX[1] - CloseSPX[20]) / CloseSPX[20]) * 100.0%
        /// - Positive value (&gt; +10%): Stock is crushing the benchmark (institutional alpha leader).
        /// - Zero (~0%): Beta trade (tracks index).
        /// - Negative value (&lt; -10%): Lagging benchmark (isolated weakness or capitulating reversal).
        /// Returns double.NaN if any input is NaN or &lt;= 0.
        /// </summary>
        public static double ComputeRelativeStrengthDelta20(double stockClose1, double stockClose20, double benchClose1, double benchClose20)
        {
            if (double.IsNaN(stockClose1) || double.IsNaN(stockClose20) || stockClose1 <= 0.0 || stockClose20 <= 0.0)
                return double.NaN;
            if (double.IsNaN(benchClose1) || double.IsNaN(benchClose20) || benchClose1 <= 0.0 || benchClose20 <= 0.0)
                return double.NaN;

            double stockReturn20 = (stockClose1 - stockClose20) / stockClose20;
            double benchReturn20 = (benchClose1 - benchClose20) / benchClose20;

            return (stockReturn20 - benchReturn20) * 100.0;
        }

        /// <summary>
        /// Canonical Tie-Breaker comparison logic for ranking multiple candidate setups at 14:15 EST when portfolio slots are limited.
        /// Implements the 14:15 EST Tie-Breaker Matrix:
        /// - Long Continuations (Bull Flags): Priority 1 = Highest positive RS_Delta20 (alpha leaders), Priority 2 = Highest CLV.
        /// - Long Reversals (Wyckoff Springs): Priority 1 = Highest CLV (&gt;=0.80 buyer support), Priority 2 = Lowest MinRSI12 (deepest shakeout).
        /// - Short Continuations (Bear Flags): Priority 1 = Lowest / most negative RS_Delta20 (weakest laggards), Priority 2 = Lowest CLV.
        /// - Short Reversals (Wyckoff Upthrusts): Priority 1 = Lowest CLV (seller rejection), Priority 2 = Highest MaxRSI12 (highest blow-off).
        /// Returns negative if A ranks higher than B (A should come first), positive if B ranks higher, 0 if equal.
        /// </summary>
        public static int CompareSetupsTieBreaker(
            PpoSetupType typeA, double clvA, double rsDeltaA, double extremeRsiA,
            PpoSetupType typeB, double clvB, double rsDeltaB, double extremeRsiB)
        {
            // If different setup types, order by setup type category:
            // LongBullFlagExpansion (1) -> LongTerminalShakeout (2) -> ShortBearFlagBreakdown (3) -> ShortUpthrust (4)
            int CategoryOrder(PpoSetupType t) => t switch
            {
                PpoSetupType.LongBullFlagExpansion => 1,
                PpoSetupType.LongTerminalShakeout => 2,
                PpoSetupType.ShortBearFlagBreakdown => 3,
                PpoSetupType.ShortUpthrust => 4,
                _ => 99
            };

            int catA = CategoryOrder(typeA);
            int catB = CategoryOrder(typeB);
            if (catA != catB)
                return catA.CompareTo(catB);

            // Helpers for safe sorting with NaN (NaN values go to bottom)
            int CompareDesc(double a, double b)
            {
                bool nanA = double.IsNaN(a);
                bool nanB = double.IsNaN(b);
                if (nanA && nanB) return 0;
                if (nanA) return 1; // a is NaN -> goes after b
                if (nanB) return -1; // b is NaN -> a goes first
                return b.CompareTo(a); // descending
            }

            int CompareAsc(double a, double b)
            {
                bool nanA = double.IsNaN(a);
                bool nanB = double.IsNaN(b);
                if (nanA && nanB) return 0;
                if (nanA) return 1;
                if (nanB) return -1;
                return a.CompareTo(b); // ascending
            }

            switch (typeA)
            {
                case PpoSetupType.LongBullFlagExpansion:
                {
                    // Priority 1: Highest positive RS_Delta20
                    int cRs = CompareDesc(rsDeltaA, rsDeltaB);
                    if (cRs != 0) return cRs;
                    // Priority 2: Highest CLV
                    int cClv = CompareDesc(clvA, clvB);
                    if (cClv != 0) return cClv;
                    // Priority 3: Highest MaxRSI12
                    return CompareDesc(extremeRsiA, extremeRsiB);
                }

                case PpoSetupType.LongTerminalShakeout:
                {
                    // Priority 1: Highest CLV (aggressive buyer absorption)
                    int cClv = CompareDesc(clvA, clvB);
                    if (cClv != 0) return cClv;
                    // Priority 2: Lowest MinRSI12 (deepest capitulation shakeout)
                    int cRsi = CompareAsc(extremeRsiA, extremeRsiB);
                    if (cRsi != 0) return cRsi;
                    // Priority 3: RS_Delta20 (descending)
                    return CompareDesc(rsDeltaA, rsDeltaB);
                }

                case PpoSetupType.ShortBearFlagBreakdown:
                {
                    // Priority 1: Lowest / most negative RS_Delta20 (heaviest laggard)
                    int cRs = CompareAsc(rsDeltaA, rsDeltaB);
                    if (cRs != 0) return cRs;
                    // Priority 2: Lowest CLV (close at low of day)
                    int cClv = CompareAsc(clvA, clvB);
                    if (cClv != 0) return cClv;
                    // Priority 3: Lowest MinRSI12
                    return CompareAsc(extremeRsiA, extremeRsiB);
                }

                case PpoSetupType.ShortUpthrust:
                {
                    // Priority 1: Lowest CLV (aggressive seller rejection)
                    int cClv = CompareAsc(clvA, clvB);
                    if (cClv != 0) return cClv;
                    // Priority 2: Highest MaxRSI12 (highest blow-off exhaustion)
                    int cRsi = CompareDesc(extremeRsiA, extremeRsiB);
                    if (cRsi != 0) return cRsi;
                    // Priority 3: RS_Delta20 (ascending)
                    return CompareAsc(rsDeltaA, rsDeltaB);
                }

                default:
                    return 0;
            }
        }

        /// <summary>
        /// Evaluates the Relative Volatility Floor:
        /// Ensures that the stock's Daily ATR(14) represents at least a minimum percentage of the stock price.
        /// Formula: (Daily ATR / Close) * 100% >= minRelativeAtrPct (default 1.0%).
        /// Sluit trage nutsbedrijven en dividendaandelen uit die te weinig bewegen om de 3.0 ATR Take Profit te bereiken.
        /// </summary>
        public static (bool Passes, double RelativeAtrPct, string Reason) EvaluateRelativeVolatilityFloor(
            double dailyAtr,
            double currentPrice,
            double minRelativeAtrPct = 1.0)
        {
            if (double.IsNaN(dailyAtr) || double.IsNaN(currentPrice) || currentPrice <= 0 || dailyAtr <= 0)
                return (false, double.NaN, "Invalid ATR or Price for Relative Volatility Floor");

            double relAtrPct = (dailyAtr / currentPrice) * 100.0;
            if (minRelativeAtrPct > 0 && relAtrPct < minRelativeAtrPct)
            {
                return (false, relAtrPct,
                    $"Low Relative Volatility: ATR% ({relAtrPct:F2}%) < {minRelativeAtrPct:F1}% of price");
            }

            return (true, relAtrPct,
                $"Relative Volatility OK: ATR% ({relAtrPct:F2}%) >= {minRelativeAtrPct:F1}% of price");
        }

        /// <summary>
        /// Pure engine calculation for next scanner scheduled trigger time.
        /// Automatically adds a 2-second offset after bar close (:00:02, :15:02, market close + 2s)
        /// so that broker feeds have 100% finalized and closed the completed candle.
        /// </summary>
        public static DateTime CalculateNextScanTime(
            ScanScheduleMode mode,
            int customIntervalSeconds,
            DateTime fromUtc,
            int dailyCloseHourEt = 16)
        {
            switch (mode)
            {
                case ScanScheduleMode.Hourly:
                {
                    var topOfCurrentHour = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
                    var next = topOfCurrentHour.AddHours(1).AddSeconds(2);
                    if (next <= fromUtc)
                        next = next.AddHours(1);
                    return next;
                }
                case ScanScheduleMode.Every15Minutes:
                {
                    int nextQuarterMinute = ((fromUtc.Minute / 15) + 1) * 15;
                    var baseHour = new DateTime(fromUtc.Year, fromUtc.Month, fromUtc.Day, fromUtc.Hour, 0, 0, DateTimeKind.Utc);
                    var next = baseHour.AddMinutes(nextQuarterMinute).AddSeconds(2);
                    if (next <= fromUtc)
                        next = next.AddMinutes(15);
                    return next;
                }
                case ScanScheduleMode.DailyAfterClose:
                {
                    DateTime fromEt = ConvertUtcToEt(fromUtc);
                    DateTime targetTodayEt = new DateTime(fromEt.Year, fromEt.Month, fromEt.Day, dailyCloseHourEt, 0, 2, DateTimeKind.Unspecified);
                    DateTime targetUtc = ConvertEtToUtc(targetTodayEt);
                    if (targetUtc <= fromUtc)
                    {
                        DateTime targetTomorrowEt = targetTodayEt.AddDays(1);
                        targetUtc = ConvertEtToUtc(targetTomorrowEt);
                    }
                    return targetUtc;
                }
                case ScanScheduleMode.ManualOnly:
                    return DateTime.MaxValue;
                case ScanScheduleMode.CustomInterval:
                default:
                    return fromUtc.AddSeconds(Math.Max(60, customIntervalSeconds));
            }
        }

        /// <summary>
        /// Converts a UTC DateTime to US Eastern Time (ET), safely handling EST/EDT and cross-platform timezone IDs.
        /// </summary>
        public static DateTime ConvertUtcToEt(DateTime utcTime)
        {
            try
            {
                var tz = FindTimeZone("Eastern Standard Time", "America/New_York", "EST5EDT");
                return TimeZoneInfo.ConvertTimeFromUtc(utcTime, tz);
            }
            catch
            {
                // Fallback EDT (-4h)
                return utcTime.AddHours(-4);
            }
        }

        /// <summary>
        /// Determines whether the bar at index n - 1 represents an active, forming intraday bar
        /// during live US stock market trading hours (09:30 - 16:00 ET, Monday to Friday).
        /// If false (e.g. weekends, after hours, pre-market, or when useNearCloseFormingBar is disabled),
        /// the bar at index n - 1 is treated as a completed, closed daily bar.
        /// </summary>
        public static bool IsBarCurrentlyLive(DateTime barOpenUtc, DateTime currentServerTimeUtc, bool useNearCloseFormingBar)
        {
            if (!useNearCloseFormingBar)
                return false;

            // Convert server time to US Eastern Time (ET)
            DateTime nowEt = ConvertUtcToEt(currentServerTimeUtc);

            // Weekends (Saturday / Sunday): US equity market is 100% closed; bar n - 1 is last closed daily bar (e.g. Friday)
            if (nowEt.DayOfWeek == DayOfWeek.Saturday || nowEt.DayOfWeek == DayOfWeek.Sunday)
                return false;

            // Regular US Cash Session: 09:30 ET - 16:00 ET
            TimeSpan timeOfDay = nowEt.TimeOfDay;
            if (timeOfDay < new TimeSpan(9, 30, 0) || timeOfDay >= new TimeSpan(16, 0, 0))
                return false;

            // Verify the bar was opened for today's session (not a stale bar from prior sessions)
            if ((currentServerTimeUtc - barOpenUtc).TotalHours > 36.0)
                return false;

            return true;
        }

        /// <summary>
        /// Identifies the bar index of the most recently completed regular US cash session daily bar (09:30 - 16:00 ET).
        /// Protects against broker pre-market rollover bars that exist before the regular session opens.
        /// </summary>
        public static int GetLastCompletedDailyBarIndex(DateTime lastBarOpenUtc, int totalBarsCount, DateTime serverTimeUtc)
        {
            if (totalBarsCount <= 0)
                return -1;

            DateTime nowEt = ConvertUtcToEt(serverTimeUtc);
            DateTime lastBarEt = ConvertUtcToEt(lastBarOpenUtc);

            // If the latest bar has a Saturday or Sunday date, it represents a weekend bar (regular US cash session is closed on weekends).
            // The last completed regular session is Friday (index n - 2).
            if ((lastBarEt.DayOfWeek == DayOfWeek.Saturday || lastBarEt.DayOfWeek == DayOfWeek.Sunday) && totalBarsCount >= 2)
            {
                return totalBarsCount - 2;
            }

            // If the latest bar in the series is dated for a future day or today before 09:30 ET,
            // it represents an unformed pre-market bar. The last completed session is index n - 2.
            if (nowEt.TimeOfDay < new TimeSpan(9, 30, 0) && lastBarEt.Date >= nowEt.Date && totalBarsCount >= 2)
            {
                return totalBarsCount - 2;
            }

            // During regular market hours (09:30 - 16:00 ET, Monday to Friday): the latest bar (n - 1) is today's
            // forming bar, NOT a completed session. The last completed session is index n - 2.
            if (nowEt.DayOfWeek != DayOfWeek.Saturday && nowEt.DayOfWeek != DayOfWeek.Sunday &&
                nowEt.TimeOfDay >= new TimeSpan(9, 30, 0) && nowEt.TimeOfDay < new TimeSpan(16, 0, 0) && totalBarsCount >= 2)
            {
                return totalBarsCount - 2;
            }

            // If it's evening after 16:00 ET, but broker has already rolled over to tomorrow's bar date (e.g. 17:00 ET rollover):
            if (nowEt.TimeOfDay >= new TimeSpan(16, 0, 0) && lastBarEt.Date > nowEt.Date && totalBarsCount >= 2)
            {
                return totalBarsCount - 2;
            }

            return totalBarsCount - 1;
        }

        /// <summary>
        /// Converts a US Eastern Time (ET) DateTime to UTC, safely handling EST/EDT and cross-platform timezone IDs.
        /// </summary>
        public static DateTime ConvertEtToUtc(DateTime etTime)
        {
            try
            {
                var tz = FindTimeZone("Eastern Standard Time", "America/New_York", "EST5EDT");
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(etTime, DateTimeKind.Unspecified), tz);
            }
            catch
            {
                // Fallback EDT (+4h)
                return etTime.AddHours(4);
            }
        }

        private static TimeZoneInfo FindTimeZone(params string[] ids)
        {
            foreach (var id in ids)
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch { }
            }
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Automatic scan scheduling modes for PPO Reversal Scanner.
    /// </summary>
    public enum ScanScheduleMode
    {
        /// <summary>
        /// Scans once per day, 2 seconds after market close (e.g. 16:00:02 ET).
        /// </summary>
        DailyAfterClose,

        /// <summary>
        /// Scans at the top of every hour, 2 seconds after the close (XX:00:02 UTC).
        /// </summary>
        Hourly,

        /// <summary>
        /// Scans at every 15-minute mark, 2 seconds after the close (:00:02, :15:02, :30:02, :45:02 UTC).
        /// </summary>
        Every15Minutes,

        /// <summary>
        /// Manual only: only scans when clicking the on-chart '▶ SCAN NOW' button or on bot startup.
        /// </summary>
        ManualOnly,

        /// <summary>
        /// Scans using a custom interval defined by ScanIntervalSeconds.
        /// </summary>
        CustomInterval
    }

    /// <summary>
    /// Permitted scan trade directions for PPO Reversal Scanner.
    /// </summary>
    public enum ScanAllowedDirection
    {
        Both,
        LongOnly,
        ShortOnly
    }

    /// <summary>
    /// Supported timeframe selector for the PPO Reversal Scanner.
    /// Using a custom enum guarantees cTrader UI defaults strictly to Daily,
    /// rather than auto-syncing to the chart timeframe.
    /// </summary>
    public enum ScannerTimeFrame
    {
        Daily,
        Hour4,
        Hour,
        Minute30,
        Minute15
    }
}

