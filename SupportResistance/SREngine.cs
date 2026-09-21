using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Overlay mode for Support/Resistance indicator.
    /// Daily: Forces Daily S/R on all charts (e.g. 1-Hour, Daily).
    /// Weekly: Forces Weekly S/R on all charts (e.g. 1-Hour, Daily, Weekly).
    /// </summary>
    public enum SROverlayMode
    {
        Daily,
        Weekly
    }

    /// <summary>
    /// Pivot detection source price.
    /// </summary>
    public enum PivotSourceMode
    {
        HighLow,
        CloseOpen
    }

    /// <summary>
    /// Immutable Support/Resistance Channel zone.
    /// </summary>
    public readonly struct SRChannel
    {
        public readonly double High;
        public readonly double Low;
        public readonly double Mid;
        public readonly int PivotCount;
        public readonly int BarTouchCount;
        public readonly double Strength;

        public SRChannel(double high, double low, int pivotCount, int barTouchCount, double strength)
        {
            High = high;
            Low = low;
            Mid = (high + low) * 0.5;
            PivotCount = pivotCount;
            BarTouchCount = barTouchCount;
            Strength = strength;
        }

        public override string ToString() => $"[{Low:F5} - {High:F5}] (Pivots: {PivotCount}, Touches: {BarTouchCount}, Strength: {Strength:F0})";
    }

    /// <summary>
    /// Pure Support/Resistance Channel Engine. Zero cTrader dependency — 100% unit-testable.
    /// Port of LonesomeTheBlue's S/R Channel algorithm with enhancements:
    /// - Dynamic macro-volatility width based on historical range
    /// - Dual-layer scoring (pivots * 20 + interval bar touches * 1)
    /// - Fixed interval candle overlap detection
    /// - Non-overlapping greedy zone selection
    /// </summary>
    public static class SREngine
    {
        /// <summary>
        /// Builds Support/Resistance Channel zones from price series.
        /// </summary>
        public static List<SRChannel> BuildChannels(
            IReadOnlyList<double> highs,
            IReadOnlyList<double> lows,
            IReadOnlyList<double> closes,
            IReadOnlyList<double>? opens = null,
            int pivotPeriod = 5,
            PivotSourceMode sourceMode = PivotSourceMode.HighLow,
            double channelWidthPct = 4.0,
            int loopback = 300,
            int minStrength = 1,
            int maxChannels = 6,
            int maxIndex = -1)
        {
            var result = new List<SRChannel>();
            if (highs == null || lows == null || closes == null) return result;

            int n = (maxIndex >= 0 && maxIndex < closes.Count) ? maxIndex + 1 : closes.Count;
            if (n < pivotPeriod * 2 + 1) return result;

            int startIdx = Math.Max(0, n - loopback);
            int windowSize = n - startIdx;
            if (windowSize < pivotPeriod * 2 + 1) return result;

            // 1. Calculate Macro-Volatility Channel Width (cwidth)
            double highest = double.MinValue;
            double lowest = double.MaxValue;
            for (int i = startIdx; i < n; i++)
            {
                double h = highs[i];
                double l = lows[i];
                if (!double.IsNaN(h) && h > highest) highest = h;
                if (!double.IsNaN(l) && l < lowest) lowest = l;
            }

            if (highest <= lowest || highest == double.MinValue || lowest == double.MaxValue)
                return result;

            double cwidth = (highest - lowest) * (channelWidthPct / 100.0);
            if (cwidth <= 0) return result;

            // 2. Extract Pivot Points within Loopback Window
            var pivotVals = new List<double>();
            for (int i = startIdx + pivotPeriod; i <= n - 1 - pivotPeriod; i++)
            {
                double srcHigh = (sourceMode == PivotSourceMode.HighLow || opens == null)
                    ? highs[i]
                    : Math.Max(closes[i], opens[i]);

                double srcLow = (sourceMode == PivotSourceMode.HighLow || opens == null)
                    ? lows[i]
                    : Math.Min(closes[i], opens[i]);

                // Check Pivot High
                bool isPh = !double.IsNaN(srcHigh);
                if (isPh)
                {
                    for (int j = 1; j <= pivotPeriod; j++)
                    {
                        double vLeft = (sourceMode == PivotSourceMode.HighLow || opens == null) ? highs[i - j] : Math.Max(closes[i - j], opens[i - j]);
                        double vRight = (sourceMode == PivotSourceMode.HighLow || opens == null) ? highs[i + j] : Math.Max(closes[i + j], opens[i + j]);
                        if (double.IsNaN(vLeft) || srcHigh < vLeft || double.IsNaN(vRight) || srcHigh <= vRight)
                        {
                            isPh = false;
                            break;
                        }
                    }
                    if (isPh) pivotVals.Add(srcHigh);
                }

                // Check Pivot Low
                bool isPl = !double.IsNaN(srcLow);
                if (isPl)
                {
                    for (int j = 1; j <= pivotPeriod; j++)
                    {
                        double vLeft = (sourceMode == PivotSourceMode.HighLow || opens == null) ? lows[i - j] : Math.Min(closes[i - j], opens[i - j]);
                        double vRight = (sourceMode == PivotSourceMode.HighLow || opens == null) ? lows[i + j] : Math.Min(closes[i + j], opens[i + j]);
                        if (double.IsNaN(vLeft) || srcLow > vLeft || double.IsNaN(vRight) || srcLow >= vRight)
                        {
                            isPl = false;
                            break;
                        }
                    }
                    if (isPl) pivotVals.Add(srcLow);
                }
            }

            if (pivotVals.Count == 0) return result;

            // 3. Form Candidate Channels around each Pivot
            var candidates = new List<(double hi, double lo, int numpp, int touches, double strength)>();
            for (int x = 0; x < pivotVals.Count; x++)
            {
                double lo = pivotVals[x];
                double hi = lo;
                int numpp = 0;

                for (int y = 0; y < pivotVals.Count; y++)
                {
                    double cpp = pivotVals[y];
                    double newLo = Math.Min(lo, cpp);
                    double newHi = Math.Max(hi, cpp);
                    if (newHi - newLo <= cwidth)
                    {
                        lo = newLo;
                        hi = newHi;
                        numpp++;
                    }
                }

                // 4. Calculate Bar Touch Strength (Interval Overlap Fix)
                int touches = 0;
                for (int y = startIdx; y < n; y++)
                {
                    double bh = highs[y];
                    double bl = lows[y];
                    if (!double.IsNaN(bh) && !double.IsNaN(bl))
                    {
                        if (bl <= hi && bh >= lo)
                        {
                            touches++;
                        }
                    }
                }

                double totalStrength = (numpp * 20.0) + (touches * 1.0);
                candidates.Add((hi, lo, numpp, touches, totalStrength));
            }

            // 5. Non-Overlapping Greedy Selection
            var remaining = new List<(double hi, double lo, int numpp, int touches, double strength)>(candidates);
            while (remaining.Count > 0 && result.Count < maxChannels)
            {
                int bestIdx = -1;
                double bestStrength = -1.0;

                for (int i = 0; i < remaining.Count; i++)
                {
                    var cand = remaining[i];
                    if (cand.numpp >= minStrength && cand.strength > bestStrength)
                    {
                        bestStrength = cand.strength;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestStrength <= 0) break;

                var selected = remaining[bestIdx];
                result.Add(new SRChannel(selected.hi, selected.lo, selected.numpp, selected.touches, selected.strength));

                // Eliminate any candidate channels that overlap with the selected channel
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    var cand = remaining[i];
                    if (cand.lo <= selected.hi && cand.hi >= selected.lo)
                    {
                        remaining.RemoveAt(i);
                    }
                }
            }

            // Sort resulting channels ascending by price (Low)
            result.Sort((a, b) => a.Low.CompareTo(b.Low));
            return result;
        }

        /// <summary>
        /// Selects the nearest Resistance (above price) and Support (below price) channels,
        /// and identifies any channel price is currently inside.
        /// </summary>
        public static (List<SRChannel> resistance, List<SRChannel> support, List<SRChannel> inside) SelectNearestChannels(
            IReadOnlyList<SRChannel> channels,
            double price,
            int maxPerSide)
        {
            var res = new List<SRChannel>();
            var sup = new List<SRChannel>();
            var inside = new List<SRChannel>();
            if (channels == null || double.IsNaN(price)) return (res, sup, inside);

            foreach (var c in channels)
            {
                if (price >= c.Low && price <= c.High)
                {
                    inside.Add(c);
                }
                else if (c.Low > price)
                {
                    res.Add(c);
                }
                else if (c.High < price)
                {
                    sup.Add(c);
                }
            }

            // Resistance: nearest first = ascending by Low
            res.Sort((a, b) => a.Low.CompareTo(b.Low));
            if (res.Count > maxPerSide) res.RemoveRange(maxPerSide, res.Count - maxPerSide);

            // Support: nearest first = descending by High
            sup.Sort((a, b) => b.High.CompareTo(a.High));
            if (sup.Count > maxPerSide) sup.RemoveRange(maxPerSide, sup.Count - maxPerSide);

            return (res, sup, inside);
        }

        /// <summary>
        /// Checks if price (high, low, close) is near or touching any of the given channels within tolerance.
        /// </summary>
        public static (bool isNear, SRChannel matchedChannel) IsNearChannel(
            double curHigh,
            double curLow,
            double curClose,
            IReadOnlyList<SRChannel> channels,
            double tolerance,
            bool isSupport)
        {
            if (channels == null || channels.Count == 0 || double.IsNaN(curClose) || double.IsNaN(tolerance) || tolerance < 0)
                return (false, default);

            foreach (var ch in channels)
            {
                double zoneLo = ch.Low - tolerance;
                double zoneHi = ch.High + tolerance;

                // Check interval touch: candle overlaps [zoneLo, zoneHi]
                if (curLow <= zoneHi && curHigh >= zoneLo)
                {
                    if (isSupport && ch.Mid <= curClose + tolerance)
                    {
                        return (true, ch);
                    }
                    if (!isSupport && ch.Mid >= curClose - tolerance)
                    {
                        return (true, ch);
                    }
                }
            }

            return (false, default);
        }

        /// <summary>
        /// Returns true if price has broken out of any channel (close crossed through a boundary).
        /// </summary>
        public static (bool brokenRes, bool brokenSup, SRChannel channel) CheckBreakout(
            IReadOnlyList<SRChannel> channels,
            double prevClose,
            double curClose)
        {
            if (channels == null || double.IsNaN(prevClose) || double.IsNaN(curClose))
                return (false, false, default);

            foreach (var ch in channels)
            {
                // Broken Resistance: prevClose <= ch.High and curClose > ch.High
                if (prevClose <= ch.High && curClose > ch.High)
                {
                    return (true, false, ch);
                }
                // Broken Support: prevClose >= ch.Low and curClose < ch.Low
                if (prevClose >= ch.Low && curClose < ch.Low)
                {
                    return (false, true, ch);
                }
            }

            return (false, false, default);
        }

        /// <summary>
        /// Computes Exponential Moving Average (EMA) series across all values.
        /// </summary>
        public static double[] ComputeEma(IReadOnlyList<double> values, int period)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));

            int n = values.Count;
            double[] ema = new double[n];
            for (int i = 0; i < n; i++) ema[i] = double.NaN;

            if (n < period) return ema;

            double sum = 0.0;
            for (int i = 0; i < period; i++)
            {
                if (double.IsNaN(values[i])) return ema;
                sum += values[i];
            }

            ema[period - 1] = sum / period;
            double k = 2.0 / (period + 1.0);

            for (int i = period; i < n; i++)
            {
                double v = values[i];
                if (double.IsNaN(v))
                {
                    ema[i] = ema[i - 1];
                    continue;
                }

                ema[i] = (v * k) + (ema[i - 1] * (1.0 - k));
            }

            return ema;
        }

        /// <summary>
        /// Computes Welles Wilder smoothed Relative Strength Index (RSI) across a price series.
        /// </summary>
        public static double[] ComputeRsiSeries(IReadOnlyList<double> closes, int period)
        {
            if (closes == null || period < 1) return Array.Empty<double>();
            int n = closes.Count;
            double[] rsi = new double[n];
            for (int i = 0; i < n; i++) rsi[i] = double.NaN;
            if (n <= period) return rsi;

            double sumGain = 0.0;
            double sumLoss = 0.0;
            for (int i = 1; i <= period; i++)
            {
                double diff = closes[i] - closes[i - 1];
                if (diff >= 0) sumGain += diff;
                else sumLoss += -diff;
            }

            double avgGain = sumGain / period;
            double avgLoss = sumLoss / period;
            rsi[period] = (avgLoss == 0) ? 100.0 : ((avgGain == 0) ? 0.0 : 100.0 - (100.0 / (1.0 + (avgGain / avgLoss))));

            for (int i = period + 1; i < n; i++)
            {
                double diff = closes[i] - closes[i - 1];
                double gain = diff > 0 ? diff : 0.0;
                double loss = diff < 0 ? -diff : 0.0;
                avgGain = (avgGain * (period - 1) + gain) / period;
                avgLoss = (avgLoss * (period - 1) + loss) / period;
                if (avgLoss == 0) rsi[i] = 100.0;
                else if (avgGain == 0) rsi[i] = 0.0;
                else rsi[i] = 100.0 - (100.0 / (1.0 + (avgGain / avgLoss)));
            }

            return rsi;
        }

        /// <summary>
        /// Computes Simple Moving Average (SMA) series across all values.
        /// </summary>
        public static double[] ComputeSma(IReadOnlyList<double> values, int period)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            if (period < 1) throw new ArgumentOutOfRangeException(nameof(period));

            int n = values.Count;
            double[] sma = new double[n];
            for (int i = 0; i < n; i++) sma[i] = double.NaN;

            if (n < period) return sma;

            double sum = 0.0;
            for (int i = 0; i < period; i++)
            {
                if (double.IsNaN(values[i])) return sma;
                sum += values[i];
            }

            sma[period - 1] = sum / period;

            for (int i = period; i < n; i++)
            {
                double vIn = values[i];
                double vOut = values[i - period];
                if (double.IsNaN(vIn) || double.IsNaN(vOut))
                {
                    sma[i] = sma[i - 1];
                    continue;
                }

                sum += vIn - vOut;
                sma[i] = sum / period;
            }

            return sma;
        }
    }
}
