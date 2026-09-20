using System;
using System.Collections.Generic;

namespace cAlgo
{
    /// <summary>
    /// Pure alert-cross detection logic. No cTrader dependency.
    /// Determines whether a "cross into zone" or "touch" event should fire,
    /// given the previous and current state. Designed to be called once per
    /// closed bar; returns true only on the bar where the condition newly
    /// becomes true (edge detection), so the caller fires an alert exactly once.
    /// </summary>
    public static class AlertEngine
    {
        /// <summary>
        /// Returns true if <paramref name="current"/> has crossed above
        /// <paramref name="threshold"/> since <paramref name="previous"/>.
        /// A cross is strictly from at-or-below to strictly above. Exact
        /// equality on either side does not count (prevents repeat firing
        /// while sitting on the line).
        /// </summary>
        public static bool CrossedAbove(double previous, double current, double threshold)
        {
            if (double.IsNaN(previous) || double.IsNaN(current)) return false;
            return previous <= threshold && current > threshold;
        }

        /// <summary>
        /// Returns true if <paramref name="current"/> has crossed below
        /// <paramref name="threshold"/> since <paramref name="previous"/>.
        /// </summary>
        public static bool CrossedBelow(double previous, double current, double threshold)
        {
            if (double.IsNaN(previous) || double.IsNaN(current)) return false;
            return previous >= threshold && current < threshold;
        }

        /// <summary>
        /// Returns true if <paramref name="current"/> has crossed into the
        /// band [<paramref name="lower"/>, <paramref name="upper"/>] from
        /// outside it. Useful for OB/OS zone entry.
        /// </summary>
        public static bool CrossedIntoBand(double previous, double current, double lower, double upper)
        {
            if (double.IsNaN(previous) || double.IsNaN(current)) return false;
            bool wasOutside = previous < lower || previous > upper;
            bool isInside = current >= lower && current <= upper;
            return wasOutside && isInside;
        }

        /// <summary>
        /// Returns true if <paramref name="current"/> has crossed out of the
        /// band [<paramref name="lower"/>, <paramref name="upper"/>] from
        /// inside it. Useful for OB/OS zone exit.
        /// </summary>
        public static bool CrossedOutOfBand(double previous, double current, double lower, double upper)
        {
            if (double.IsNaN(previous) || double.IsNaN(current)) return false;
            bool wasInside = previous >= lower && previous <= upper;
            bool isOutside = current < lower || current > upper;
            return wasInside && isOutside;
        }

        /// <summary>
        /// Returns true if <paramref name="current"/> has touched or crossed
        /// <paramref name="threshold"/> from the side that was not previously
        /// touching. A "touch" is current &lt;= threshold while previous was
        /// above (for an upper touch), or current &gt;= threshold while
        /// previous was below (for a lower touch). Use the <paramref name="fromAbove"/>
        /// flag to select direction.
        /// </summary>
        public static bool Touched(double previous, double current, double threshold, bool fromAbove)
        {
            if (double.IsNaN(previous) || double.IsNaN(current)) return false;
            return fromAbove
                ? previous > threshold && current <= threshold
                : previous < threshold && current >= threshold;
        }
    }
}
