using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo
{
    /// <summary>
    /// True Strength Index (William Blau) oscillator pane. Standard formula:
    ///   TSI    = 100 * EMA(short, EMA(long, Close - Close[1])) / EMA(short, EMA(long, |Close - Close[1]|))
    ///   Signal = EMA(signalPeriod, TSI)
    /// Defaults 13/7/7. The zero line marks the momentum regime used by the scanners
    /// (TSI &gt; 0 = bullish regime, TSI &lt; 0 = bearish regime); the Continuation scanner
    /// uses exactly that gate. The signal line is optional and display-only. Values are
    /// bounded to (-100, +100); warmup bars are NaN (not drawn). AccessRights = None.
    /// </summary>
    [Indicator(IsOverlay = false, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class TrueStrengthIndex : Indicator
    {
        [Parameter("TSI Long EMA", Group = "TSI", DefaultValue = 13, MinValue = 1)]
        public int TsiLongPeriod { get; set; } = 13;

        [Parameter("TSI Short EMA", Group = "TSI", DefaultValue = 7, MinValue = 1)]
        public int TsiShortPeriod { get; set; } = 7;

        [Parameter("TSI Signal (EMA)", Group = "TSI", DefaultValue = 7, MinValue = 1)]
        public int TsiSignalPeriod { get; set; } = 7;

        [Parameter("Show Signal Line", Group = "TSI", DefaultValue = true)]
        public bool ShowSignalLine { get; set; } = true;

        [Output("TSI", LineColor = "CornflowerBlue", Thickness = 2, PlotType = PlotType.Line)]
        public IndicatorDataSeries Tsi { get; set; } = null!;

        [Output("TSI Signal", LineColor = "Orange", Thickness = 1, PlotType = PlotType.Line)]
        public IndicatorDataSeries TsiSignal { get; set; } = null!;

        // Cumulative per-bar state (written at every index so a full recalculation
        // reproduces the live pane exactly; recomputing the same index re-derives
        // from the stored index-1 state, so tick updates on the forming bar are safe).
        private readonly List<double> _pc = new();
        private readonly List<double> _absPc = new();
        private readonly List<double> _emaLongPc = new();
        private readonly List<double> _emaShortPc = new();
        private readonly List<double> _emaLongAbs = new();
        private readonly List<double> _emaShortAbs = new();
        private readonly List<double> _tsiValues = new();
        private readonly List<double> _tsiSigValues = new();

        protected override void Initialize()
        {
            _pc.Clear();
            _absPc.Clear();
            _emaLongPc.Clear();
            _emaShortPc.Clear();
            _emaLongAbs.Clear();
            _emaShortAbs.Clear();
            _tsiValues.Clear();
            _tsiSigValues.Clear();
            Print($"[TrueStrengthIndex] TSI({TsiLongPeriod},{TsiShortPeriod},{TsiSignalPeriod}) | Signal line {(ShowSignalLine ? "ON" : "OFF")} | Zero line = momentum regime.");
        }

        public override void Calculate(int index)
        {
            if (index < 0) return;

            // Bar 0: reset state (full recalculation starts here). PC[0] = 0 by convention.
            if (index == 0 && _pc.Count > 0)
            {
                _pc.Clear();
                _absPc.Clear();
                _emaLongPc.Clear();
                _emaShortPc.Clear();
                _emaLongAbs.Clear();
                _emaShortAbs.Clear();
                _tsiValues.Clear();
                _tsiSigValues.Clear();
            }

            double close = Bars.ClosePrices[index];
            double prevClose = index > 0 ? Bars.ClosePrices[index - 1] : double.NaN;
            double pc = index > 0 ? close - prevClose : 0.0;
            if (double.IsNaN(pc)) pc = 0.0;
            double absPc = Math.Abs(pc);

            // Ensure the lists cover this index (trim anything beyond it on revisit).
            while (_pc.Count <= index) _pc.Add(double.NaN);
            while (_absPc.Count <= index) _absPc.Add(double.NaN);
            while (_emaLongPc.Count <= index) _emaLongPc.Add(double.NaN);
            while (_emaShortPc.Count <= index) _emaShortPc.Add(double.NaN);
            while (_emaLongAbs.Count <= index) _emaLongAbs.Add(double.NaN);
            while (_emaShortAbs.Count <= index) _emaShortAbs.Add(double.NaN);
            while (_tsiValues.Count <= index) _tsiValues.Add(double.NaN);
            while (_tsiSigValues.Count <= index) _tsiSigValues.Add(double.NaN);

            double emaLongPc = EmaStep(pc, Prev(_emaLongPc, index), TsiLongPeriod);
            double emaLongAbs = EmaStep(absPc, Prev(_emaLongAbs, index), TsiLongPeriod);
            double emaShortPc = EmaStep(emaLongPc, Prev(_emaShortPc, index), TsiShortPeriod);
            double emaShortAbs = EmaStep(emaLongAbs, Prev(_emaShortAbs, index), TsiShortPeriod);

            double tsi = emaShortAbs > 1e-12 ? 100.0 * emaShortPc / emaShortAbs : double.NaN;

            _pc[index] = pc;
            _absPc[index] = absPc;
            _emaLongPc[index] = emaLongPc;
            _emaShortPc[index] = emaShortPc;
            _emaLongAbs[index] = emaLongAbs;
            _emaShortAbs[index] = emaShortAbs;
            _tsiValues[index] = tsi;

            double tsiSig = EmaStep(tsi, Prev(_tsiSigValues, index), TsiSignalPeriod);
            _tsiSigValues[index] = tsiSig;

            Tsi[index] = tsi;
            TsiSignal[index] = ShowSignalLine ? tsiSig : double.NaN;
        }

        /// <summary>Returns the stored value at index - 1 (NaN when none exists).</summary>
        private static double Prev(List<double> series, int index)
        {
            int i = index - 1;
            return i >= 0 && i < series.Count ? series[i] : double.NaN;
        }

        /// <summary>
        /// One EMA step matching ReversalEngine.ComputeEma: seeds with the first valid
        /// value, then y = alpha*x + (1-alpha)*y_prev with alpha = 2/(period+1).
        /// A NaN input propagates NaN; a NaN previous state seeds with the input.
        /// </summary>
        private static double EmaStep(double value, double previous, int period)
        {
            if (period < 1) return double.NaN;
            if (double.IsNaN(value)) return double.NaN;
            if (double.IsNaN(previous)) return value;
            double alpha = 2.0 / (period + 1.0);
            return value * alpha + previous * (1.0 - alpha);
        }
    }
}
