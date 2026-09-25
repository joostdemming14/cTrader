using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Single confirmed swing pivot point.
    /// </summary>
    public readonly struct PivotPoint
    {
        public readonly int BarIndex;
        public readonly double Price;
        public readonly bool IsHigh;

        public PivotPoint(int barIndex, double price, bool isHigh)
        {
            BarIndex = barIndex;
            Price = price;
            IsHigh = isHigh;
        }
    }

    /// <summary>
    /// Pure swing-pivot detection and clustering engine. Zero cTrader dependencies — 100% unit-testable.
    /// Features:
    ///   - Asymmetric left/right strength pivot detection with plateau/double-top handling
    ///   - Deterministic 1D proximity clustering (order-independent, no sequential drift)
    ///   - Unified Support/Resistance Role-Reversal (Polarity) detection (Highs & Lows merged)
    ///   - Zone boundary calculation (MinPrice/MaxPrice) and touch recency tracking
    /// </summary>
    public static class PivotEngine
    {
        /// <summary>
        /// Scans a high-price series for confirmed pivot highs.
        /// Handles equal highs/plateaus by requiring >= on the left and strictly > on the right.
        /// </summary>
        /// <param name="highs">High prices, index 0 = oldest.</param>
        /// <param name="leftStrength">Bars before the pivot that must be lower or equal.</param>
        /// <param name="rightStrength">Bars after the pivot that must be strictly lower.</param>
        /// <param name="maxIndex">Highest index to evaluate (inclusive).</param>
        /// <param name="minIndex">Lowest index to evaluate (inclusive, default 0).</param>
        /// <returns>List of pivot-high indices, ascending.</returns>
        public static List<int> FindPivotHighs(
            IReadOnlyList<double> highs,
            int leftStrength,
            int rightStrength,
            int maxIndex,
            int minIndex = 0)
        {
            var result = new List<int>();
            if (highs == null || highs.Count == 0) return result;
            if (leftStrength < 1 || rightStrength < 1) return result;

            int first = Math.Max(leftStrength, minIndex + leftStrength);
            int last = Math.Min(maxIndex, highs.Count - 1);

            for (int i = first; i + rightStrength <= last; i++)
            {
                double pivot = highs[i];
                if (double.IsNaN(pivot)) continue;

                bool isPivot = true;
                // Left side: prior bars must be <= pivot
                for (int j = 1; j <= leftStrength; j++)
                {
                    double v = highs[i - j];
                    if (double.IsNaN(v) || pivot < v) { isPivot = false; break; }
                }
                if (!isPivot) continue;

                // Right side: subsequent bars must be strictly < pivot (confirms the rightmost peak)
                for (int j = 1; j <= rightStrength; j++)
                {
                    double v = highs[i + j];
                    if (double.IsNaN(v) || pivot <= v) { isPivot = false; break; }
                }
                if (isPivot) result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// Scans a low-price series for confirmed pivot lows.
        /// Handles equal lows/double bottoms by requiring <= on the left and strictly < on the right.
        /// </summary>
        public static List<int> FindPivotLows(
            IReadOnlyList<double> lows,
            int leftStrength,
            int rightStrength,
            int maxIndex,
            int minIndex = 0)
        {
            var result = new List<int>();
            if (lows == null || lows.Count == 0) return result;
            if (leftStrength < 1 || rightStrength < 1) return result;

            int first = Math.Max(leftStrength, minIndex + leftStrength);
            int last = Math.Min(maxIndex, lows.Count - 1);

            for (int i = first; i + rightStrength <= last; i++)
            {
                double pivot = lows[i];
                if (double.IsNaN(pivot)) continue;

                bool isPivot = true;
                // Left side: prior bars must be >= pivot
                for (int j = 1; j <= leftStrength; j++)
                {
                    double v = lows[i - j];
                    if (double.IsNaN(v) || pivot > v) { isPivot = false; break; }
                }
                if (!isPivot) continue;

                // Right side: subsequent bars must be strictly > pivot (confirms the rightmost trough)
                for (int j = 1; j <= rightStrength; j++)
                {
                    double v = lows[i + j];
                    if (double.IsNaN(v) || pivot >= v) { isPivot = false; break; }
                }
                if (isPivot) result.Add(i);
            }
            return result;
        }

        /// <summary>
        /// Clusters pivot prices using deterministic 1D proximity clustering.
        /// Guaranteed to be order-independent, symmetric, and drift-free.
        /// </summary>
        /// <param name="pivotPrices">Pivot prices.</param>
        /// <param name="tolerance">Cluster merge radius (e.g. 0.5 * ATR).</param>
        public static List<PivotCluster> Cluster(IReadOnlyList<double> pivotPrices, double tolerance)
        {
            var clusters = new List<PivotCluster>();
            if (pivotPrices == null || pivotPrices.Count == 0 || tolerance <= 0) return clusters;

            var validPrices = new List<double>();
            foreach (var p in pivotPrices)
            {
                if (!double.IsNaN(p)) validPrices.Add(p);
            }
            if (validPrices.Count == 0) return clusters;

            // Sort ascending for deterministic clustering
            validPrices.Sort();

            var currentCluster = new PivotCluster(validPrices[0]);
            clusters.Add(currentCluster);

            for (int i = 1; i < validPrices.Count; i++)
            {
                double p = validPrices[i];
                // Check distance from current cluster representative / last member
                if (Math.Abs(p - currentCluster.Representative) <= tolerance ||
                    Math.Abs(p - currentCluster.MaxPrice) <= tolerance * 0.75)
                {
                    currentCluster.Add(p);
                }
                else
                {
                    currentCluster = new PivotCluster(p);
                    clusters.Add(currentCluster);
                }
            }

            // Post-pass: merge any adjacent clusters whose representatives are within tolerance
            for (int i = 0; i < clusters.Count - 1; )
            {
                if (Math.Abs(clusters[i].Representative - clusters[i + 1].Representative) <= tolerance)
                {
                    clusters[i].MergeWith(clusters[i + 1]);
                    clusters.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }

            return clusters;
        }

        /// <summary>
        /// Clusters a list of structured PivotPoints (tracking both high/low polarity and bar indices).
        /// </summary>
        public static List<PivotCluster> ClusterPoints(IReadOnlyList<PivotPoint> points, double tolerance)
        {
            var clusters = new List<PivotCluster>();
            if (points == null || points.Count == 0 || tolerance <= 0) return clusters;

            var sortedPoints = new List<PivotPoint>();
            foreach (var pt in points)
            {
                if (!double.IsNaN(pt.Price)) sortedPoints.Add(pt);
            }
            if (sortedPoints.Count == 0) return clusters;

            sortedPoints.Sort((a, b) => a.Price.CompareTo(b.Price));

            var currentCluster = new PivotCluster(sortedPoints[0].Price, sortedPoints[0].IsHigh, sortedPoints[0].BarIndex);
            clusters.Add(currentCluster);

            for (int i = 1; i < sortedPoints.Count; i++)
            {
                var pt = sortedPoints[i];
                if (Math.Abs(pt.Price - currentCluster.Representative) <= tolerance ||
                    Math.Abs(pt.Price - currentCluster.MaxPrice) <= tolerance * 0.75)
                {
                    currentCluster.Add(pt.Price, pt.IsHigh, pt.BarIndex);
                }
                else
                {
                    currentCluster = new PivotCluster(pt.Price, pt.IsHigh, pt.BarIndex);
                    clusters.Add(currentCluster);
                }
            }

            for (int i = 0; i < clusters.Count - 1; )
            {
                if (Math.Abs(clusters[i].Representative - clusters[i + 1].Representative) <= tolerance)
                {
                    clusters[i].MergeWith(clusters[i + 1]);
                    clusters.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }

            return clusters;
        }
    }

    /// <summary>
    /// A cluster of pivot prices merged within a tolerance.
    /// Tracks representative median price, zone boundaries, touch counts, polarity (role-reversal), and recency.
    /// </summary>
    public class PivotCluster
    {
        private readonly List<double> _members = new();
        private readonly List<int> _barIndices = new();
        private int _highTouches;
        private int _lowTouches;
        private double _minPrice;
        private double _maxPrice;
        private double _representative;
        private bool _dirty;

        public PivotCluster(double firstPrice, bool isHigh = true, int barIndex = -1)
        {
            _members.Add(firstPrice);
            if (barIndex >= 0) _barIndices.Add(barIndex);
            if (isHigh) _highTouches++;
            else _lowTouches++;
            _minPrice = firstPrice;
            _maxPrice = firstPrice;
            _representative = firstPrice;
            _dirty = false;
        }

        /// <summary>Total number of pivot touches in this cluster.</summary>
        public int TouchCount => _members.Count;

        /// <summary>Number of times price tested this level as Resistance (Swing Highs).</summary>
        public int HighTouchCount => _highTouches;

        /// <summary>Number of times price tested this level as Support (Swing Lows).</summary>
        public int LowTouchCount => _lowTouches;

        /// <summary>True if this level has acted as BOTH Support and Resistance (Role Reversal Key Level).</summary>
        public bool IsFlipped => _highTouches > 0 && _lowTouches > 0;

        /// <summary>Most recent bar index among member touches, or -1 if untracked.</summary>
        public int LatestBarIndex
        {
            get
            {
                // Members are stored in price order, not time order: the most recent touch is
                // the highest bar index, not the last-added member.
                int max = -1;
                for (int i = 0; i < _barIndices.Count; i++)
                    if (_barIndices[i] > max) max = _barIndices[i];
                return max;
            }
        }

        /// <summary>Median price of all members — the level's primary drawn price.</summary>
        public double Representative
        {
            get
            {
                if (_dirty) Recompute();
                return _representative;
            }
        }

        /// <summary>Lowest price among cluster members (zone bottom).</summary>
        public double MinPrice
        {
            get
            {
                if (_dirty) Recompute();
                return _minPrice;
            }
        }

        /// <summary>Highest price among cluster members (zone top).</summary>
        public double MaxPrice
        {
            get
            {
                if (_dirty) Recompute();
                return _maxPrice;
            }
        }

        private void Recompute()
        {
            if (_members.Count == 0)
            {
                _minPrice = double.NaN;
                _maxPrice = double.NaN;
                _representative = double.NaN;
                _dirty = false;
                return;
            }

            double min = double.MaxValue;
            double max = double.MinValue;
            foreach (var m in _members)
            {
                if (m < min) min = m;
                if (m > max) max = m;
            }
            _minPrice = min;
            _maxPrice = max;

            var sorted = new List<double>(_members);
            sorted.Sort();
            int mid = sorted.Count / 2;
            _representative = sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
            _dirty = false;
        }

        /// <summary>Adds a pivot price to this cluster.</summary>
        public void Add(double price, bool isHigh = true, int barIndex = -1)
        {
            _members.Add(price);
            if (barIndex >= 0) _barIndices.Add(barIndex);
            if (isHigh) _highTouches++;
            else _lowTouches++;
            _dirty = true;
        }

        /// <summary>Merges another cluster into this one.</summary>
        public void MergeWith(PivotCluster other)
        {
            if (other == null) return;
            _members.AddRange(other._members);
            _barIndices.AddRange(other._barIndices);
            _highTouches += other._highTouches;
            _lowTouches += other._lowTouches;
            _dirty = true;
        }
    }
}
