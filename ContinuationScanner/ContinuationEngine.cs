using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Result of a Continuation setup evaluation at a single bar.
    /// </summary>
    public readonly struct ContinuationSetupResult
    {
        /// <summary>True when the trigger bar fired all conditions on this bar.</summary>
        public readonly bool IsTriggered;

        /// <summary>Long, Short, or None.</summary>
        public readonly ReversalDirection Direction;

        /// <summary>Legacy structural index, unused when no structural lookback is configured.</summary>
        public readonly int ExtremeIndex;

        /// <summary>Legacy swing high, unused when no structural lookback is configured.</summary>
        public readonly double SwingHigh;

        /// <summary>Legacy swing low, unused when no structural lookback is configured.</summary>
        public readonly double SwingLow;

        /// <summary>Index of the EMA21 touch bar, which is the trigger bar in the current logic.</summary>
        public readonly int EmaTouchIndex;

        /// <summary>Index of the trigger bar being evaluated.</summary>
        public readonly int TriggerIndex;

        // Trigger-bar snapshot values (for alerting / HUD).
        public readonly double Close;
        public readonly double High;
        public readonly double Low;
        public readonly double Clv;
        public readonly double Tsi;
        public readonly double TsiSig;
        public readonly double Ema50;
        public readonly double Sma200;
        public readonly double Atr;

        /// <summary>Distance from the trigger close to the lookback-bar structural extreme, in ATR units.
        /// Long: (close - swingLow)/ATR (distance above the lookback-bar low). Short: (swingHigh - close)/ATR.</summary>
        public readonly double DistanceAtr;

        /// <summary>Human-readable reason when the setup did not trigger.</summary>
        public readonly string RejectReason;

        public ContinuationSetupResult(bool isTriggered, ReversalDirection direction,
            int extremeIndex, double swingHigh, double swingLow, int emaTouchIndex, int triggerIndex,
            double close, double high, double low, double clv, double tsi, double tsiSig,
            double ema50, double sma200, double atr, double distanceAtr, string rejectReason)
        {
            IsTriggered = isTriggered;
            Direction = direction;
            ExtremeIndex = extremeIndex;
            SwingHigh = swingHigh;
            SwingLow = swingLow;
            EmaTouchIndex = emaTouchIndex;
            TriggerIndex = triggerIndex;
            Close = close;
            High = high;
            Low = low;
            Clv = clv;
            Tsi = tsi;
            TsiSig = tsiSig;
            Ema50 = ema50;
            Sma200 = sma200;
            Atr = atr;
            DistanceAtr = distanceAtr;
            RejectReason = rejectReason ?? "";
        }

        /// <summary>Constructs a non-triggered result with a reject reason.</summary>
        public static ContinuationSetupResult Reject(ReversalDirection direction, string reason) =>
            new ContinuationSetupResult(false, direction, -1, double.NaN, double.NaN, -1, -1,
                double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
                double.NaN, double.NaN, double.NaN, double.NaN, reason);
    }

    /// <summary>
    /// Pure C# engine for the Continuation scanner strategy. Zero dependencies on cAlgo APIs —
    /// 100% unit testable, reproducible, and deterministic. Used by the ContinuationScanner cBot.
    /// Reuses <see cref="ReversalEngine"/> for indicator math (EMA/SMA/ATR/TSI/CLV) and scheduling.
    ///
    /// Final continuation logic:
    ///   EMA21 pullback, EMA50 trend alignment, SMA200, ATR(14), TSI(25,13,13), CLV
    ///   plus the SMA200 long-term trend filter (continuations only).
    ///
    /// Long Continuation trigger on bar t:
    ///   Low <= EMA21 on the trigger bar, then Close > EMA21, EMA21 > EMA50, Close > SMA200,
    ///   CLV >= +clvMin, TSI > 0 (momentum regime), no price/TSI divergence over divergenceGuardBars
    ///   (price up while TSI down). SPY gate optional (default off).
    ///
    /// Short Continuation trigger on bar t:
    ///   High >= EMA21 on the trigger bar, then Close < EMA21, EMA21 < EMA50, Close < SMA200,
    ///   CLV <= -clvMin, TSI < 0 (momentum regime), no price/TSI divergence over divergenceGuardBars
    ///   (price down while TSI up). SPY gate optional (default off).
    ///
    /// Momentum is regime-only: the TSI zero line decides, the TSI signal line is not part of the
    /// continuation trigger (it is computed for display only). The scanner does not compute SL/PT (alert-only).
    /// </summary>
    public static class ContinuationEngine
    {
        /// <summary>
        /// Evaluates a Long Continuation setup at <paramref name="evalIndex"/> (the trigger candidate bar).
        /// The trigger bar must touch EMA21 (Low <= EMA21) and close back above it.
        /// </summary>
        public static ContinuationSetupResult EvaluateLongContinuation(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> ema50, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            double clvMin, int divergenceGuardBars, bool spyLongOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, ema50, sma200, atr, evalIndex))
                return ContinuationSetupResult.Reject(ReversalDirection.Long, "Null or out-of-range input");

            int t = evalIndex;

            // Step 1: the trigger bar touched at or below EMA21 (Low <= EMA21).
            if (double.IsNaN(ema21[t]) || double.IsNaN(lows[t]) || !(lows[t] <= ema21[t]))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    "Latest bar did not touch EMA21");

            // Step 2: trigger conditions on bar t (EOD close).
            double close = closes[t], high = highs[t], low = lows[t];
            double tsiT = tsi[t], tsiSigT = tsiSig[t];
            double ema = ema21[t], slowEma = ema50[t], sma = sma200[t], atrT = atr[t];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(tsiT) || double.IsNaN(tsiSigT) ||
                double.IsNaN(ema) || double.IsNaN(slowEma) || double.IsNaN(sma) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ContinuationSetupResult.Reject(ReversalDirection.Long, "Trigger-bar indicator NaN/invalid");

            if (!(close > ema))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} not > EMA21 {ema:F4} (no reclaim)");
            if (!(ema > slowEma))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"EMA21 {ema:F4} not > EMA50 {slowEma:F4} (trend alignment)");
            if (!(close > sma))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"Close {close:F4} not > SMA200 {sma:F4} (long-term trend filter)");
            if (!(clv >= clvMin))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"CLV {clv:F2} not >= {clvMin:F2}");
            if (!(tsiT > 0.0))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"TSI {tsiT:F2} not > 0 (momentum regime)");
            if (HasBearishPriceTsiDivergence(closes, tsi, t, divergenceGuardBars))
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    $"Price up but TSI down over last {divergenceGuardBars} bars (active divergence)");
            if (!spyLongOk)
                return ContinuationSetupResult.Reject(ReversalDirection.Long,
                    "SPY benchmark gate failed (SPY not > SPY_SMA50)");

            return new ContinuationSetupResult(true, ReversalDirection.Long,
                -1, double.NaN, double.NaN, t, t, close, high, low, clv, tsiT, tsiSigT,
                ema, sma, atrT, double.NaN, "Long Continuation triggered");
        }

        /// <summary>
        /// Evaluates a Short Continuation setup at <paramref name="evalIndex"/> (the trigger candidate bar).
        /// Mirror of <see cref="EvaluateLongContinuation"/>: High >= EMA21 on the trigger bar,
        /// trigger Close < EMA21, EMA21 < EMA50, Close < SMA200, CLV <= -clvMin, TSI < 0. SPY gate optional (default off).
        /// </summary>
        public static ContinuationSetupResult EvaluateShortContinuation(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> ema50, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            double clvMin, int divergenceGuardBars, bool spyShortOk)
        {
            if (IsBadInput(closes, highs, lows, tsi, tsiSig, ema21, ema50, sma200, atr, evalIndex))
                return ContinuationSetupResult.Reject(ReversalDirection.Short, "Null or out-of-range input");

            int t = evalIndex;

            // Step 1: the trigger bar touched at or above EMA21 (High >= EMA21).
            if (double.IsNaN(ema21[t]) || double.IsNaN(highs[t]) || !(highs[t] >= ema21[t]))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    "Latest bar did not touch EMA21");

            // Step 2: trigger conditions on bar t (EOD close).
            double close = closes[t], high = highs[t], low = lows[t];
            double tsiT = tsi[t], tsiSigT = tsiSig[t];
            double ema = ema21[t], slowEma = ema50[t], sma = sma200[t], atrT = atr[t];
            double clv = ClvOf(close, high, low);

            if (double.IsNaN(tsiT) || double.IsNaN(tsiSigT) ||
                double.IsNaN(ema) || double.IsNaN(slowEma) || double.IsNaN(sma) || double.IsNaN(atrT) || atrT <= 0.0 || double.IsNaN(clv))
                return ContinuationSetupResult.Reject(ReversalDirection.Short, "Trigger-bar indicator NaN/invalid");

            if (!(close < ema))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} not < EMA21 {ema:F4} (no breakdown)");
            if (!(ema < slowEma))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"EMA21 {ema:F4} not < EMA50 {slowEma:F4} (trend alignment)");
            if (!(close < sma))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"Close {close:F4} not < SMA200 {sma:F4} (long-term trend filter)");
            if (!(clv <= -clvMin))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"CLV {clv:F2} not <= {-clvMin:F2}");
            if (!(tsiT < 0.0))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"TSI {tsiT:F2} not < 0 (momentum regime)");
            if (HasBullishPriceTsiDivergence(closes, tsi, t, divergenceGuardBars))
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    $"Price down but TSI up over last {divergenceGuardBars} bars (active divergence)");
            if (!spyShortOk)
                return ContinuationSetupResult.Reject(ReversalDirection.Short,
                    "SPY benchmark gate failed (SPY not < SPY_SMA50)");

            return new ContinuationSetupResult(true, ReversalDirection.Short,
                -1, double.NaN, double.NaN, t, t, close, high, low, clv, tsiT, tsiSigT,
                ema, sma, atrT, double.NaN, "Short Continuation triggered");
        }

        /// <summary>
        /// Evaluates both directions and returns the triggered setup, if any.
        /// Long and Short are mutually exclusive (Long needs EMA21 > EMA50 and Close > EMA21;
        /// Short needs EMA21 < EMA50 and Close < EMA21).
        /// When <paramref name="direction"/> restricts to one side, only that side is evaluated.
        /// <paramref name="spyLongOk"/>/<paramref name="spyShortOk"/> carry the SPY-vs-SPY_SMA50 gate.
        /// </summary>
        public static ContinuationSetupResult Evaluate(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> ema50, IReadOnlyList<double> sma200, IReadOnlyList<double> atr,
            int evalIndex,
            ReversalScanDirection direction,
            double clvMin,
            int divergenceGuardBars,
            bool spyLongOk, bool spyShortOk)
        {
            ContinuationSetupResult res = ContinuationSetupResult.Reject(ReversalDirection.None, "Not evaluated");

            if (direction != ReversalScanDirection.ShortOnly)
            {
                var lon = EvaluateLongContinuation(closes, highs, lows, tsi, tsiSig, ema21, ema50, sma200, atr,
                    evalIndex, clvMin, divergenceGuardBars, spyLongOk);
                if (lon.IsTriggered) return lon;
                if (direction == ReversalScanDirection.LongOnly) return lon;
                res = lon;
            }

            if (direction != ReversalScanDirection.LongOnly)
            {
                var sh = EvaluateShortContinuation(closes, highs, lows, tsi, tsiSig, ema21, ema50, sma200, atr,
                    evalIndex, clvMin, divergenceGuardBars, spyShortOk);
                if (sh.IsTriggered) return sh;
                res = sh;
            }

            return res;
        }

        // =========================================================================
        // --- PRIVATE UTILITIES ---
        // =========================================================================

        /// <summary>
        /// True when price rose net over the guard window while TSI fell: active bearish price/TSI divergence
        /// (fading momentum under a rising price). Returns false when the guard is off, history is
        /// insufficient, or either value is NaN.
        /// </summary>
        private static bool HasBearishPriceTsiDivergence(IReadOnlyList<double> closes, IReadOnlyList<double> tsi, int t, int divergenceGuardBars)
        {
            if (divergenceGuardBars <= 0) return false;
            int p = t - divergenceGuardBars;
            if (p < 0) return false;
            double curTsi = tsi[t], prevTsi = tsi[p];
            if (double.IsNaN(curTsi) || double.IsNaN(prevTsi)) return false;
            return closes[t] > closes[p] && curTsi < prevTsi;
        }

        /// <summary>
        /// True when price fell net over the guard window while TSI rose: active bullish price/TSI divergence.
        /// Returns false when the guard is off, history is insufficient, or either value is NaN.
        /// </summary>
        private static bool HasBullishPriceTsiDivergence(IReadOnlyList<double> closes, IReadOnlyList<double> tsi, int t, int divergenceGuardBars)
        {
            if (divergenceGuardBars <= 0) return false;
            int p = t - divergenceGuardBars;
            if (p < 0) return false;
            double curTsi = tsi[t], prevTsi = tsi[p];
            if (double.IsNaN(curTsi) || double.IsNaN(prevTsi)) return false;
            return closes[t] < closes[p] && curTsi > prevTsi;
        }

        private static double ClvOf(double close, double high, double low)
        {
            double range = high - low;
            return range > 0.0 ? (2.0 * close - high - low) / range : double.NaN;
        }

        private static bool IsBadInput(
            IReadOnlyList<double> closes, IReadOnlyList<double> highs, IReadOnlyList<double> lows,
            IReadOnlyList<double> tsi, IReadOnlyList<double> tsiSig,
            IReadOnlyList<double> ema21, IReadOnlyList<double> ema50, IReadOnlyList<double> sma200, IReadOnlyList<double> atr, int evalIndex)
        {
            if (closes == null || highs == null || lows == null || tsi == null ||
                tsiSig == null || ema21 == null || ema50 == null || sma200 == null || atr == null) return true;
            int n = closes.Count;
            if (n == 0 || highs.Count != n || lows.Count != n ||
                tsi.Count != n || tsiSig.Count != n || ema21.Count != n || ema50.Count != n || sma200.Count != n || atr.Count != n) return true;
            return evalIndex < 0 || evalIndex >= n;
        }
    }
}