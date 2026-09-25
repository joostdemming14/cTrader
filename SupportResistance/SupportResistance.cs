using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo
{
    /// <summary>
    /// Support and Resistance Channels Indicator with Daily and Weekly Overlay Modes.
    /// Identifies institutional liquidity walls using ATR clustering and touch quota fallback.
    /// Strictly renders only the 2 nearest Resistance (Red) and 2 nearest Support (Lime) levels.
    ///
    /// Overlay Modes:
    /// - Daily: Calculates Support/Resistance channels from Daily bars across all charts (e.g. 1-Hour, Daily).
    /// - Weekly: Calculates Support/Resistance channels from Weekly bars across all charts (e.g. 1-Hour, Daily, Weekly).
    /// </summary>
    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SupportResistance : Indicator
    {
        // =========================================================================
        // --- 1. Settings ---
        // =========================================================================
        [Parameter("Pivot Period", DefaultValue = 10, Group = "1. Settings", MinValue = 2, MaxValue = 20)]
        public int PivotPeriod { get; set; } = 10;

        [Parameter("Pivot Source", DefaultValue = PivotSourceMode.HighLow, Group = "1. Settings")]
        public PivotSourceMode SourceMode { get; set; } = PivotSourceMode.HighLow;

        [Parameter("Max Channel Width %", DefaultValue = 5.0, Group = "1. Settings", MinValue = 1.0, MaxValue = 10.0, Step = 0.5)]
        public double ChannelWidthPct { get; set; } = 5.0;

        [Parameter("Min Strength (Pivots)", DefaultValue = 1, Group = "1. Settings", MinValue = 1, MaxValue = 10)]
        public int MinStrength { get; set; } = 1;

        [Parameter("Max Number of Channels", DefaultValue = 6, Group = "1. Settings", MinValue = 1, MaxValue = 10)]
        public int MaxChannels { get; set; } = 6;

        [Parameter("Show Only Nearest (2 Res / 2 Sup)", DefaultValue = true, Group = "1. Settings")]
        public bool ShowOnlyNearest { get; set; } = true;

        [Parameter("Max Channels Per Side", DefaultValue = 2, Group = "1. Settings", MinValue = 1, MaxValue = 5)]
        public int MaxChannelsPerSide { get; set; } = 2;

        [Parameter("Loopback Period", DefaultValue = 500, Group = "1. Settings", MinValue = 50, MaxValue = 1000)]
        public int Loopback { get; set; } = 500;

        // =========================================================================
        // --- 2. Overlay Timeframe ---
        // =========================================================================
        [Parameter("Overlay Mode", DefaultValue = SROverlayMode.Daily, Group = "2. Overlay")]
        public SROverlayMode OverlayMode { get; set; } = SROverlayMode.Daily;

        // =========================================================================
        // --- 3. Visuals ---
        // =========================================================================
        [Parameter("Show S/R Zones", DefaultValue = false, Group = "3. Visuals")]
        public bool ShowChannels { get; set; } = false;

        [Parameter("Resistance Color", DefaultValue = "Red", Group = "3. Visuals")]
        public Color ResColor { get; set; } = Color.Red;

        [Parameter("Support Color", DefaultValue = "Lime", Group = "3. Visuals")]
        public Color SupColor { get; set; } = Color.Lime;

        [Parameter("In-Channel Color", DefaultValue = "Gold", Group = "3. Visuals")]
        public Color InChannelColor { get; set; } = Color.Gold;

        [Parameter("Zone Fill Opacity (0-255)", DefaultValue = 40, Group = "3. Visuals", MinValue = 0, MaxValue = 255)]
        public int ZoneOpacity { get; set; } = 40;

        // =========================================================================
        // --- 4. Extras (Pivots, Moving Averages) ---
        // =========================================================================
        [Parameter("Show Pivot Points (H/L)", DefaultValue = false, Group = "4. Extras")]
        public bool ShowPivotPoints { get; set; } = false;

        [Parameter("Show Chart-TF MA 1 (Local)", DefaultValue = false, Group = "4. Extras")]
        public bool ShowMa1 { get; set; } = false;

        [Parameter("Chart-TF MA 1 Period", DefaultValue = 50, Group = "4. Extras", MinValue = 1)]
        public int Ma1Period { get; set; } = 50;

        [Parameter("Chart-TF MA 1 Type", DefaultValue = MovingAverageType.Simple, Group = "4. Extras")]
        public MovingAverageType Ma1Type { get; set; } = MovingAverageType.Simple;

        [Parameter("Show Chart-TF MA 2 (Local)", DefaultValue = false, Group = "4. Extras")]
        public bool ShowMa2 { get; set; } = false;

        [Parameter("Chart-TF MA 2 Period", DefaultValue = 200, Group = "4. Extras", MinValue = 1)]
        public int Ma2Period { get; set; } = 200;

        [Parameter("Chart-TF MA 2 Type", DefaultValue = MovingAverageType.Simple, Group = "4. Extras")]
        public MovingAverageType Ma2Type { get; set; } = MovingAverageType.Simple;

        // =========================================================================
        // --- 5. HUD & Trend Bias ---
        // =========================================================================
        [Parameter("Show HUD", DefaultValue = true, Group = "5. HUD & Trend Bias")]
        public bool ShowHud { get; set; } = true;

        [Parameter("Show S/R in HUD", DefaultValue = false, Group = "5. HUD & Trend Bias")]
        public bool ShowSrInHud { get; set; } = false;

        [Parameter("Daily Fast EMA Period", DefaultValue = 21, Group = "5. HUD & Trend Bias", MinValue = 2)]
        public int FastEmaPeriod { get; set; } = 21;

        [Parameter("Daily Slow EMA Period", DefaultValue = 50, Group = "5. HUD & Trend Bias", MinValue = 5)]
        public int SlowEmaPeriod { get; set; } = 50;

        [Parameter("Show Stepped Daily EMAs", DefaultValue = true, Group = "5. HUD & Trend Bias")]
        public bool ShowDailyEma { get; set; } = true;

        [Parameter("Show Daily 200 SMA", DefaultValue = true, Group = "5. HUD & Trend Bias")]
        public bool ShowDailySma200 { get; set; } = true;

        [Parameter("Daily 200 SMA Period", DefaultValue = 200, Group = "5. HUD & Trend Bias", MinValue = 10)]
        public int DailySmaPeriod { get; set; } = 200;

        [Parameter("Show 1H 21 EMA (Pink)", DefaultValue = false, Group = "5. HUD & Trend Bias")]
        public bool ShowH1Ema { get; set; } = false;

        [Parameter("1H Fast EMA Period", DefaultValue = 21, Group = "5. HUD & Trend Bias", MinValue = 2)]
        public int H1FastEmaPeriod { get; set; } = 21;

        [Parameter("ATR Period", DefaultValue = 14, Group = "5. HUD & Trend Bias", MinValue = 1)]
        public int AtrPeriod { get; set; } = 14;

        [Parameter("Show ATR in Pips", DefaultValue = true, Group = "5. HUD & Trend Bias")]
        public bool ShowAtrPips { get; set; } = true;

        [Parameter("Show Spread Guard", DefaultValue = true, Group = "5. HUD & Trend Bias")]
        public bool ShowSpreadGuard { get; set; } = true;

        [Parameter("Max Spread ATR Fraction", DefaultValue = 0.05, Group = "5. HUD & Trend Bias", MinValue = 0.001, MaxValue = 0.50, Step = 0.005)]
        public double MaxSpreadAtrFraction { get; set; } = 0.05;

        // =========================================================================
        // --- 6. Alerts ---
        // =========================================================================
        [Parameter("Alert on Channel Touch", DefaultValue = true, Group = "6. Alerts")]
        public bool AlertTouch { get; set; } = true;

        [Parameter("Alert on Channel Breakout", DefaultValue = true, Group = "6. Alerts")]
        public bool AlertBreakout { get; set; } = true;

        [Parameter("Alert Sound", DefaultValue = true, Group = "6. Alerts")]
        public bool AlertSound { get; set; } = true;

        [Parameter("Alert Popup", DefaultValue = true, Group = "6. Alerts")]
        public bool AlertPopup { get; set; } = true;

        [Output("1H 21 EMA", LineColor = "HotPink", Thickness = 2, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries H1EmaFast { get; set; } = null!;

        [Output("Daily Fast EMA (21)", LineColor = "DeepSkyBlue", Thickness = 2, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries DailyEmaFast { get; set; } = null!;

        [Output("Daily Slow EMA (50)", LineColor = "Orange", Thickness = 2, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries DailyEmaSlow { get; set; } = null!;

        [Output("Daily 200 SMA", LineColor = "Gold", Thickness = 2, LineStyle = LineStyle.Solid)]
        public IndicatorDataSeries DailySma200 { get; set; } = null!;

        [Output("Chart-TF MA 1 (Local)", LineColor = "Blue", Thickness = 1)]
        public IndicatorDataSeries Ma1Series { get; set; } = null!;

        [Output("Chart-TF MA 2 (Local)", LineColor = "Red", Thickness = 1)]
        public IndicatorDataSeries Ma2Series { get; set; } = null!;

        private MovingAverage _ma1 = null!;
        private MovingAverage _ma2 = null!;

        private Bars _primaryBars = null!;
        private bool _isPrimarySameAsChart;
        private bool _initialized;
        private int _lastAlertBarIndex = -1;

        private List<SRChannel> _curChannels = new();
        private int _channelCacheCount = -1;

        private const string Prefix = "SR_CHAN_";
        private const string HudPrefix = "SR_HUD_";

        // Daily Trend Moving Averages (21 EMA, 50 EMA, 200 SMA) — strictly Daily source
        private MovingAverage _nativeDailyEmaFast = null!;
        private MovingAverage _nativeDailyEmaSlow = null!;
        private MovingAverage _nativeDailySma200 = null!;
        private MovingAverage _nativeH1Ema = null!;

        private Bars _dailyBars = null!;
        private double[] _dailyEmaFastCache = Array.Empty<double>();
        private double[] _dailyEmaSlowCache = Array.Empty<double>();
        private double[] _dailySma200Cache = Array.Empty<double>();
        private int _dailyCachedCount = -1;

        private AverageTrueRange _atrChart = null!;
        private Bars _atr1hBars = null!;
        private AverageTrueRange _atr1h = null!;
        private AverageTrueRange _atrDaily = null!;
        private double[] _h1EmaCache = Array.Empty<double>();
        private int _h1CachedCount = -1;

        // Live forming-bar EMA / SMA values — kept separate from the cache arrays so
        // the forming-bar value doesn't leak into historical chart bars.
        private double _liveDailyEmaFast = double.NaN;
        private double _liveDailyEmaSlow = double.NaN;
        private double _liveDailySma200 = double.NaN;
        private double _liveH1Ema = double.NaN;
        private int _timerTicks;

        protected override void Initialize()
        {
            if (ShowMa1) _ma1 = Indicators.MovingAverage(Bars.ClosePrices, Ma1Period, Ma1Type);
            if (ShowMa2) _ma2 = Indicators.MovingAverage(Bars.ClosePrices, Ma2Period, Ma2Type);

            TimeFrame primaryTf = (OverlayMode == SROverlayMode.Daily) ? TimeFrame.Daily : TimeFrame.Weekly;
            _isPrimarySameAsChart = (primaryTf == TimeFrame || string.Equals(primaryTf.Name, TimeFrame.Name, StringComparison.OrdinalIgnoreCase));
            _primaryBars = _isPrimarySameAsChart ? Bars : MarketData.GetBars(primaryTf);

            // Ensure sufficient history is loaded for primary Daily/Weekly S/R bars on lower timeframes!
            if (!_isPrimarySameAsChart && _primaryBars != null)
            {
                _primaryBars.HistoryLoaded += OnPrimaryHistoryLoaded;
                _primaryBars.BarOpened += OnPrimaryBarOpened;

                int req = Math.Min(Loopback, 1000);
                while (_primaryBars.Count < req)
                {
                    int loaded = _primaryBars.LoadMoreHistory();
                    if (loaded <= 0) break;
                }
            }

            // Regime & Trend Timeframe setup
            bool isDailyChart = TimeFrame == TimeFrame.Daily || string.Equals(TimeFrame.Name, "Daily", StringComparison.OrdinalIgnoreCase);
            bool isH1Chart = TimeFrame == TimeFrame.Hour || string.Equals(TimeFrame.Name, "Hour", StringComparison.OrdinalIgnoreCase);

            if (isH1Chart)
            {
                _nativeH1Ema = Indicators.MovingAverage(Bars.ClosePrices, H1FastEmaPeriod, MovingAverageType.Exponential);
            }
            if (isDailyChart)
            {
                _nativeDailyEmaFast = Indicators.MovingAverage(Bars.ClosePrices, FastEmaPeriod, MovingAverageType.Exponential);
                _nativeDailyEmaSlow = Indicators.MovingAverage(Bars.ClosePrices, SlowEmaPeriod, MovingAverageType.Exponential);
                _nativeDailySma200 = Indicators.MovingAverage(Bars.ClosePrices, DailySmaPeriod, MovingAverageType.Simple);
            }

            // Earliest chart-bar open time — secondary TF bars must cover at least
            // this far back, otherwise FindParentBarIndex clamps every old chart
            // bar to the same secondary index and the stepped EMA renders as a
            // flat horizontal line.
            DateTime chartEarliest = (Bars != null && Bars.Count > 0) ? Bars.OpenTimes[0] : DateTime.UtcNow.AddYears(-2);

            // Daily bars for ATR & Moving Averages (21 EMA, 50 EMA, 200 SMA) — strictly Daily source
            _dailyBars = (isDailyChart ? Bars : MarketData.GetBars(TimeFrame.Daily)) ?? Bars!;
            if (_dailyBars != Bars && _dailyBars != null)
            {
                _dailyBars.HistoryLoaded += OnDailyHistoryLoaded;
                _dailyBars.BarOpened += OnDailyBarOpened;
                LoadHistoryUntil(_dailyBars, 1000, chartEarliest);
            }

            // Current chart timeframe ATR
            _atrChart = Indicators.AverageTrueRange(Bars, AtrPeriod, MovingAverageType.WilderSmoothing);

            // 1H ATR and 1H 21-EMA (Pink)
            _atr1hBars = (isH1Chart ? Bars : MarketData.GetBars(TimeFrame.Hour)) ?? Bars!;
            if (_atr1hBars != Bars && _atr1hBars != null)
            {
                LoadHistoryUntil(_atr1hBars, 2000, chartEarliest);
                _atr1hBars.HistoryLoaded += OnSecondaryHistoryLoaded;
            }
            _atr1h = Indicators.AverageTrueRange(_atr1hBars, AtrPeriod, MovingAverageType.WilderSmoothing);

            // Daily ATR for HUD (overnight swing model uses daily ATR for SL/TP)
            if (_dailyBars != null)
            {
                _atrDaily = Indicators.AverageTrueRange(_dailyBars, AtrPeriod, MovingAverageType.WilderSmoothing);
            }

            if (Bars != null)
            {
                Bars.BarOpened += OnBarOpened;
            }

            // Safety net: secondary-TF history often arrives asynchronously after
            // Initialize returns, and the HistoryLoaded event may have already
            // fired before we subscribed (or LoadMoreHistory returns 0 synchronously
            // and delivers a moment later). A periodic timer ensures secondary bars
            // are loaded and triggers full chart recomputes until populated.
            _timerTicks = 0;
            Timer.Start(TimeSpan.FromSeconds(2));

            _curChannels.Clear();
            _lastAlertBarIndex = -1;
            _initialized = false;
        }

        protected override void OnTimer()
        {
            if (_dailyBars != null && _dailyBars != Bars && _dailyBars.Count < 300)
            {
                _dailyBars.LoadMoreHistory();
            }

            _dailyCachedCount = -1;
            _h1CachedCount = -1;
            _channelCacheCount = -1;

            if (Bars != null && Bars.Count > 0)
            {
                for (int i = 0; i < Bars.Count; i++)
                {
                    Calculate(i);
                }
            }

            _timerTicks++;
            // Hard cap: symbols whose available daily history never reaches the 250-bar target
            // (recent IPOs, thin CFDs) must not keep the bootstrap timer and its full-chart
            // recompute loop alive forever.
            if (((_dailyBars == null || _dailyBars == Bars || _dailyBars.Count >= 250) && _timerTicks >= 3) || _timerTicks >= 150)
            {
                Timer.Stop();
            }
        }

        private void OnPrimaryHistoryLoaded(BarsHistoryLoadedEventArgs args)
        {
            _channelCacheCount = -1;
            if (Bars != null && Bars.Count > 0)
            {
                Calculate(Bars.Count - 1);
            }
        }

        /// <summary>
        /// Loads history into a secondary-timeframe Bars object until it has at
        /// least <paramref name="minCount"/> bars AND its earliest open time is at
        /// or before <paramref name="coverUntil"/>. cTrader's LoadMoreHistory may
        /// return 0 synchronously and deliver the rest asynchronously via the
        /// HistoryLoaded event, so this only does a best-effort synchronous load;
        /// the async handler finishes the job and triggers a recompute.
        /// </summary>
        private static void LoadHistoryUntil(Bars bars, int minCount, DateTime coverUntil)
        {
            if (bars == null) return;
            int guard = 0;
            while (guard++ < 200)
            {
                bool enoughCount = bars.Count >= minCount;
                bool coversRange = bars.Count > 0 && bars.OpenTimes[0] <= coverUntil;
                if (enoughCount && coversRange) break;

                int loaded = bars.LoadMoreHistory();
                if (loaded <= 0) break;
            }
        }

        private void OnDailyHistoryLoaded(BarsHistoryLoadedEventArgs args)
        {
            DateTime chartEarliest = (Bars != null && Bars.Count > 0) ? Bars.OpenTimes[0] : DateTime.UtcNow.AddYears(-2);
            if (_dailyBars != null && _dailyBars != Bars)
            {
                bool enoughCount = _dailyBars.Count >= 1000;
                bool coversRange = _dailyBars.Count > 0 && _dailyBars.OpenTimes[0] <= chartEarliest;
                if (!enoughCount || !coversRange)
                {
                    int loaded = _dailyBars.LoadMoreHistory();
                    if (loaded > 0)
                    {
                        LoadHistoryUntil(_dailyBars, 1000, chartEarliest);
                    }
                }
            }

            _dailyCachedCount = -1;

            if (Bars != null && Bars.Count > 0)
            {
                int n = Bars.Count;
                for (int i = 0; i < n; i++)
                {
                    Calculate(i);
                }
            }
        }

        private void OnDailyBarOpened(BarOpenedEventArgs args)
        {
            _dailyCachedCount = -1;
            if (Bars != null && Bars.Count > 0)
            {
                Calculate(Bars.Count - 1);
            }
        }

        private void OnSecondaryHistoryLoaded(BarsHistoryLoadedEventArgs args)
        {
            _h1CachedCount = -1;
            _channelCacheCount = -1;

            if (Bars != null && Bars.Count > 0)
            {
                int n = Bars.Count;
                for (int i = 0; i < n; i++)
                {
                    Calculate(i);
                }
            }
        }

        private void OnPrimaryBarOpened(BarOpenedEventArgs args)
        {
            _channelCacheCount = -1;
            if (Bars != null && Bars.Count > 0)
            {
                Calculate(Bars.Count - 1);
            }
        }

        private void OnBarOpened(BarOpenedEventArgs args)
        {
            _dailyCachedCount = -1;
            _h1CachedCount = -1;
            _channelCacheCount = -1;
            _initialized = true;
            // Seed the alert dedup state so the first post-init pass skips the primary-bar close
            // transition that completed before this indicator started (no stale attach alert).
            if (_primaryBars != null && _primaryBars.Count >= 3)
                _lastAlertBarIndex = _primaryBars.Count;
        }

        public override void Calculate(int index)
        {
            // Moving Averages output series
            if (ShowMa1 && _ma1 != null && index < _ma1.Result.Count)
                Ma1Series[index] = _ma1.Result[index];
            else
                Ma1Series[index] = double.NaN;

            if (ShowMa2 && _ma2 != null && index < _ma2.Result.Count)
                Ma2Series[index] = _ma2.Result[index];
            else
                Ma2Series[index] = double.NaN;

            // Stepped 1H 21-EMA (Pink)
            if (ShowH1Ema)
            {
                if (_nativeH1Ema != null && index < _nativeH1Ema.Result.Count)
                {
                    H1EmaFast[index] = _nativeH1Ema.Result[index];
                }
                else
                {
                    EnsureH1Cache();
                    if (index == Bars.Count - 1)
                    {
                        // Live forming bar: use the live forming 1H EMA (separate field,
                        // not the cache array, to prevent repaint of historical bars).
                        H1EmaFast[index] = _liveH1Ema;
                    }
                    else
                    {
                        int h1Idx;
                        if (index + 1 < Bars.Count && _atr1hBars != null && Bars.OpenTimes[index + 1] > Bars.OpenTimes[index].AddHours(1))
                        {
                            // Higher timeframe historical bar (e.g. Daily): map to 1-Hour bar at the close of that candle
                            DateTime nextTime = Bars.OpenTimes[index + 1];
                            int nextH1 = FindParentBarIndex(_atr1hBars, nextTime);
                            h1Idx = (nextH1 > 0) ? nextH1 - 1 : FindParentBarIndex(_atr1hBars, Bars.OpenTimes[index]);
                        }
                        else
                        {
                            // Intraday timeframe (e.g. 15m, 5m): map to parent 1-Hour bar
                            h1Idx = _atr1hBars != null ? FindParentBarIndex(_atr1hBars, Bars.OpenTimes[index]) : -1;
                        }

                        if (h1Idx >= 0 && h1Idx < _h1EmaCache.Length)
                        {
                            H1EmaFast[index] = _h1EmaCache[h1Idx];
                        }
                        else
                        {
                            H1EmaFast[index] = double.NaN;
                        }
                    }
                }
            }
            else
            {
                H1EmaFast[index] = double.NaN;
            }

            bool isDailyChart = TimeFrame == TimeFrame.Daily || string.Equals(TimeFrame.Name, "Daily", StringComparison.OrdinalIgnoreCase);

            // Stepped Daily 21/50 EMAs (always sourced from Daily bars)
            if (ShowDailyEma)
            {
                if (isDailyChart && _nativeDailyEmaFast != null && _nativeDailyEmaSlow != null && index < _nativeDailyEmaFast.Result.Count)
                {
                    DailyEmaFast[index] = _nativeDailyEmaFast.Result[index];
                    DailyEmaSlow[index] = _nativeDailyEmaSlow.Result[index];
                }
                else
                {
                    EnsureDailyTrendCache();
                    if (index == Bars.Count - 1)
                    {
                        DailyEmaFast[index] = _liveDailyEmaFast;
                        DailyEmaSlow[index] = _liveDailyEmaSlow;
                    }
                    else
                    {
                        int dIdx;
                        if (index + 1 < Bars.Count && _dailyBars != null && Bars.OpenTimes[index + 1] > Bars.OpenTimes[index].AddDays(1))
                        {
                            DateTime nextTime = Bars.OpenTimes[index + 1];
                            int nextD = FindParentBarIndex(_dailyBars, nextTime);
                            dIdx = (nextD > 0) ? nextD - 1 : FindParentBarIndex(_dailyBars, Bars.OpenTimes[index]);
                        }
                        else
                        {
                            dIdx = _dailyBars != null ? FindParentBarIndex(_dailyBars, Bars.OpenTimes[index]) : -1;
                        }

                        if (dIdx >= 0 && dIdx < _dailyEmaFastCache.Length)
                            DailyEmaFast[index] = _dailyEmaFastCache[dIdx];
                        else
                            DailyEmaFast[index] = double.NaN;

                        if (dIdx >= 0 && dIdx < _dailyEmaSlowCache.Length)
                            DailyEmaSlow[index] = _dailyEmaSlowCache[dIdx];
                        else
                            DailyEmaSlow[index] = double.NaN;
                    }
                }
            }
            else
            {
                DailyEmaFast[index] = double.NaN;
                DailyEmaSlow[index] = double.NaN;
            }

            // Stepped Daily 200 SMA (always sourced from Daily bars)
            if (ShowDailySma200)
            {
                if (isDailyChart && _nativeDailySma200 != null && index < _nativeDailySma200.Result.Count)
                {
                    DailySma200[index] = _nativeDailySma200.Result[index];
                }
                else
                {
                    EnsureDailyTrendCache();
                    if (index == Bars.Count - 1)
                    {
                        DailySma200[index] = _liveDailySma200;
                    }
                    else
                    {
                        int dIdx;
                        if (index + 1 < Bars.Count && _dailyBars != null && Bars.OpenTimes[index + 1] > Bars.OpenTimes[index].AddDays(1))
                        {
                            DateTime nextTime = Bars.OpenTimes[index + 1];
                            int nextD = FindParentBarIndex(_dailyBars, nextTime);
                            dIdx = (nextD > 0) ? nextD - 1 : FindParentBarIndex(_dailyBars, Bars.OpenTimes[index]);
                        }
                        else
                        {
                            dIdx = _dailyBars != null ? FindParentBarIndex(_dailyBars, Bars.OpenTimes[index]) : -1;
                        }

                        if (dIdx >= 0 && dIdx < _dailySma200Cache.Length)
                            DailySma200[index] = _dailySma200Cache[dIdx];
                        else
                            DailySma200[index] = double.NaN;
                    }
                }
            }
            else
            {
                DailySma200[index] = double.NaN;
            }

            if (index < 1) return;

            // Recompute and draw channels on the latest bar
            if (_primaryBars != null)
            {
                if (!_isPrimarySameAsChart && _primaryBars.Count < PivotPeriod * 2 + 1)
                {
                    _primaryBars.LoadMoreHistory();
                }

                int pConfirmed = _primaryBars.Count - 2; // Last closed bar of primary timeframe for stability
                if (pConfirmed >= PivotPeriod * 2 + 1)
                {
                    // Only rebuild channels when primary bar count changes (bar open), not every tick
                    double livePrice = (Bars != null && Bars.Count > 0) ? Bars.ClosePrices.LastValue : Symbol.Bid;

                    if (_channelCacheCount != _primaryBars.Count)
                    {
                        int pn = pConfirmed + 1;
                        double[] ph = new double[pn];
                        double[] pl = new double[pn];
                        double[] pc = new double[pn];
                        double[] po = new double[pn];
                        for (int i = 0; i < pn; i++)
                        {
                            ph[i] = _primaryBars.HighPrices[i];
                            pl[i] = _primaryBars.LowPrices[i];
                            pc[i] = _primaryBars.ClosePrices[i];
                            po[i] = _primaryBars.OpenPrices[i];
                        }

                        var rawCur = SREngine.BuildChannels(
                            ph, pl, pc, po,
                            pivotPeriod: PivotPeriod,
                            sourceMode: SourceMode,
                            channelWidthPct: ChannelWidthPct,
                            loopback: Loopback,
                            minStrength: MinStrength,
                            maxChannels: MaxChannels,
                            maxIndex: pConfirmed);

                        if (ShowOnlyNearest)
                        {
                            var (res, sup, inside) = SREngine.SelectNearestChannels(rawCur, livePrice, MaxChannelsPerSide);
                            var list = new List<SRChannel>();
                            list.AddRange(res);
                            list.AddRange(sup);
                            list.AddRange(inside);
                            list.Sort((a, b) => a.Low.CompareTo(b.Low));
                            _curChannels = list;
                        }
                        else
                        {
                            _curChannels = rawCur;
                        }

                        _channelCacheCount = _primaryBars.Count;
                    }

                    // Fire Alerts (once per primary bar, using closed primary-bar closes)
                    // Uses _primaryBars (Daily/Weekly) closes, not the chart's forming bar,
                    // so alerts don't repaint and don't fire on every chart bar.
                    if (_initialized && _primaryBars != null && _primaryBars.Count >= 3 &&
                        _channelCacheCount == _primaryBars.Count &&
                        _primaryBars.Count != _lastAlertBarIndex)
                    {
                        string tfLabel = (OverlayMode == SROverlayMode.Daily) ? "Daily" : "Weekly";
                        int pClose = _primaryBars.Count - 2; // last closed primary bar
                        double curClose = _primaryBars.ClosePrices[pClose];
                        double prevClose = _primaryBars.ClosePrices[pClose - 1];

                        if (!double.IsNaN(curClose) && !double.IsNaN(prevClose))
                        {
                            CheckAlerts(_curChannels, tfLabel, prevClose, curClose);
                            _lastAlertBarIndex = _primaryBars.Count;
                        }
                    }

                    // Draw Channels (Extended fully into future with clean single/rectangle rendering)
                    if (ShowChannels)
                    {
                        DateTime lastTime = (Bars != null && Bars.Count > 0) ? Bars.OpenTimes[Bars.Count - 1] : DateTime.UtcNow;
                        DateTime endTime = lastTime.AddYears(5);
                        DateTime startTime = (Bars != null && Bars.Count > 0) ? Bars.OpenTimes[0] : DateTime.UtcNow.AddYears(-1);

                        DrawChannels(_curChannels, "cur", livePrice, startTime, endTime);
                    }
                }
            }

            // Draw Pivot Points
            if (ShowPivotPoints && _isPrimarySameAsChart)
            {
                DrawPivotLabels(index - 1);
            }

            // Multi-Timeframe HUD & Cleanup (on forming/latest bar)
            if (Bars != null && index == Bars.Count - 1)
            {
                if (ShowHud)
                {
                    UpdateHud(index);
                }
                else
                {
                    Chart.RemoveObject($"{HudPrefix}ATR");
                    Chart.RemoveObject($"{HudPrefix}REGIME");
                    Chart.RemoveObject($"{HudPrefix}SR");
                }

                CleanupStaleObjects();
            }
        }

        private void EnsureDailyTrendCache()
        {
            if (_dailyBars == null || _dailyBars.Count < FastEmaPeriod)
            {
                if (_dailyBars != null && _dailyBars != Bars) _dailyBars.LoadMoreHistory();
                return;
            }

            int n = _dailyBars.Count;

            // Proactively request more daily history if below 300 bars on intraday chart
            if (n < 300 && _dailyBars != Bars)
            {
                _dailyBars.LoadMoreHistory();
            }

            if (_dailyCachedCount != n)
            {
                double[] closes = new double[n];
                for (int i = 0; i < n; i++) closes[i] = _dailyBars.ClosePrices[i];

                // Fast EMA (21)
                _dailyEmaFastCache = SREngine.ComputeEma(closes, FastEmaPeriod);

                // Slow EMA (50) - independent check
                if (n >= SlowEmaPeriod)
                {
                    _dailyEmaSlowCache = SREngine.ComputeEma(closes, SlowEmaPeriod);
                }
                else
                {
                    _dailyEmaSlowCache = Array.Empty<double>();
                }

                // Daily 200 SMA - independent check
                if (n >= DailySmaPeriod)
                {
                    _dailySma200Cache = SREngine.ComputeSma(closes, DailySmaPeriod);
                }
                else
                {
                    _dailySma200Cache = Array.Empty<double>();
                }

                _dailyCachedCount = n;
            }

            if (n >= 2)
            {
                double lastClose = (Bars != null && Bars.Count > 0) ? Bars.ClosePrices.LastValue : _dailyBars.ClosePrices.LastValue;

                if (_dailyEmaFastCache.Length == n && !double.IsNaN(_dailyEmaFastCache[n - 2]))
                {
                    double kFast = 2.0 / (FastEmaPeriod + 1.0);
                    _liveDailyEmaFast = (lastClose * kFast) + (_dailyEmaFastCache[n - 2] * (1.0 - kFast));
                }

                if (_dailyEmaSlowCache.Length == n && !double.IsNaN(_dailyEmaSlowCache[n - 2]))
                {
                    double kSlow = 2.0 / (SlowEmaPeriod + 1.0);
                    _liveDailyEmaSlow = (lastClose * kSlow) + (_dailyEmaSlowCache[n - 2] * (1.0 - kSlow));
                }

                if (n >= DailySmaPeriod)
                {
                    double sum = 0.0;
                    for (int k = n - DailySmaPeriod; k < n - 1; k++)
                        sum += _dailyBars.ClosePrices[k];
                    sum += lastClose;
                    _liveDailySma200 = sum / DailySmaPeriod;
                }
            }
        }

        private void EnsureH1Cache()
        {
            if (_atr1hBars == null || _atr1hBars.Count < H1FastEmaPeriod) return;
            int n = _atr1hBars.Count;
            if (_h1CachedCount != n)
            {
                double[] closes = new double[n];
                for (int i = 0; i < n; i++) closes[i] = _atr1hBars.ClosePrices[i];
                _h1EmaCache = SREngine.ComputeEma(closes, H1FastEmaPeriod);
                _h1CachedCount = n;
            }
            else if (n >= 2 && _h1EmaCache.Length == n)
            {
                // Compute the live forming-bar EMA into a separate field.
                // Do NOT mutate cache[n-1] in place — prevents the forming-bar
                // value from leaking into historical chart bars.
                double lastClose = _atr1hBars.ClosePrices.LastValue;
                double k = 2.0 / (H1FastEmaPeriod + 1.0);
                if (!double.IsNaN(_h1EmaCache[n - 2]))
                    _liveH1Ema = (lastClose * k) + (_h1EmaCache[n - 2] * (1.0 - k));
            }
        }

        private static int FindParentBarIndex(Bars targetBars, DateTime barTime)
        {
            if (targetBars == null) return -1;
            int count = targetBars.Count;
            if (count == 0) return -1;
            if (barTime >= targetBars.OpenTimes[count - 1]) return count - 1;
            // Out of loaded history range (barTime before earliest secondary bar):
            // return -1 so the caller writes NaN instead of clamping to index 0,
            // which would render every old chart bar at the same EMA value (flat
            // horizontal line). OnSecondaryHistoryLoaded recomputes the full
            // series once more history arrives.
            if (barTime < targetBars.OpenTimes[0]) return -1;

            int low = 0;
            int high = count - 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                DateTime midTime = targetBars.OpenTimes[mid];
                if (midTime == barTime) return mid;
                if (midTime < barTime)
                {
                    if (mid == count - 1 || targetBars.OpenTimes[mid + 1] > barTime)
                        return mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return -1;
        }

        private void UpdateHud(int index)
        {
            DateTime barTime = Bars.OpenTimes[index];

            // 1. Current Chart Timeframe ATR
            double atrChartValue = (index >= 0 && _atrChart != null && index < _atrChart.Result.Count)
                ? _atrChart.Result[index]
                : double.NaN;

            string chartTfLabel = TimeFrame.Name;
            string chartPriceText = double.IsNaN(atrChartValue) ? "N/A" : atrChartValue.ToString($"F{Symbol.Digits}");
            string chartAtrStr = $"{chartTfLabel} ATR({AtrPeriod}): {chartPriceText}";
            if (ShowAtrPips && Symbol.PipSize > 0 && !double.IsNaN(atrChartValue))
            {
                double chartAtrPips = atrChartValue / Symbol.PipSize;
                chartAtrStr += $" ({chartAtrPips:F1}p)";
            }

            // 2. Daily ATR (last closed daily bar — used by Trade Manager for SL/TP bracket)
            double atrDailyValue = double.NaN;
            if (_atrDaily != null && _dailyBars != null && _dailyBars.Count > AtrPeriod)
            {
                int dailyIdx = FindParentBarIndex(_dailyBars, barTime);
                if (dailyIdx > 0 && index == Bars.Count - 1) dailyIdx--;
                else if (dailyIdx >= _dailyBars.Count) dailyIdx = _dailyBars.Count - 1;

                if (dailyIdx >= 0 && dailyIdx < _dailyBars.Count)
                {
                    atrDailyValue = _atrDaily.Result[dailyIdx];
                }
            }

            string dailyPriceText = double.IsNaN(atrDailyValue) ? "N/A" : atrDailyValue.ToString($"F{Symbol.Digits}");
            string dailyAtrStr = $"Daily ATR({AtrPeriod}): {dailyPriceText}";
            if (ShowAtrPips && Symbol.PipSize > 0 && !double.IsNaN(atrDailyValue))
            {
                double dailyAtrPips = atrDailyValue / Symbol.PipSize;
                dailyAtrStr += $" ({dailyAtrPips:F1}p)";
            }

            string atrLine;
            if (!string.Equals(chartTfLabel, "Daily", StringComparison.OrdinalIgnoreCase) &&
                TimeFrame != TimeFrame.Daily)
            {
                atrLine = $"{chartAtrStr}  |  {dailyAtrStr}";
            }
            else
            {
                atrLine = dailyAtrStr;
            }

            Color atrColor = Color.DeepSkyBlue;
            if (ShowSpreadGuard && !double.IsNaN(atrDailyValue) && atrDailyValue > 0)
            {
                double spread = Symbol.Spread;
                double maxSpreadThreshold = MaxSpreadAtrFraction * atrDailyValue;
                bool isSpreadSafe = spread <= maxSpreadThreshold;
                double spreadRatioPct = (spread / atrDailyValue) * 100.0;
                string statusText = isSpreadSafe ? "SPREAD OK" : "SPREAD TE HOOG";
                atrColor = isSpreadSafe ? Color.LimeGreen : Color.Tomato;

                atrLine = $"[ {statusText} ] Spread: {spread:F4} (Max: {maxSpreadThreshold:F4} | {spreadRatioPct:F1}% Daily ATR)  |  " + atrLine;
            }

            // Clean up legacy Daily RSI and Daily Bias text objects from top-right corner
            Chart.RemoveObject($"{HudPrefix}RSI");
            Chart.RemoveObject($"{HudPrefix}REGIME");

            // 3. Nearest S/R Zone status
            double livePrice = (Bars != null && Bars.Count > 0) ? Bars.ClosePrices.LastValue : Symbol.Bid;
            string srLine = "S/R: Scanning...";
            Color srColor = Color.White;

            if (_curChannels != null && _curChannels.Count > 0)
            {
                var (res, sup, inside) = SREngine.SelectNearestChannels(_curChannels, livePrice, 1);
                string resStr = "None";
                string supStr = "None";

                if (res.Count > 0)
                {
                    var r = res[0];
                    double distPips = (r.Low - livePrice) / (Symbol.PipSize > 0 ? Symbol.PipSize : 1.0);
                    resStr = $"[{r.Low:F2}-{r.High:F2}] (+{distPips:F0}p)";
                }
                if (sup.Count > 0)
                {
                    var s = sup[0];
                    double distPips = (livePrice - s.High) / (Symbol.PipSize > 0 ? Symbol.PipSize : 1.0);
                    supStr = $"[{s.Low:F2}-{s.High:F2}] (-{distPips:F0}p)";
                }

                srLine = $"Nearest Res: {resStr} | Nearest Sup: {supStr}";
                if (inside.Count > 0)
                {
                    srLine = $"INSIDE ZONE [{inside[0].Low:F2}-{inside[0].High:F2}] | " + srLine;
                    srColor = Color.Gold;
                }
            }

            // Draw HUD
            Chart.DrawStaticText($"{HudPrefix}ATR", atrLine, VerticalAlignment.Top, HorizontalAlignment.Right, atrColor);

            if (ShowSrInHud)
            {
                Chart.DrawStaticText($"{HudPrefix}SR", $"\n{srLine}", VerticalAlignment.Top, HorizontalAlignment.Right, srColor);
            }
            else
            {
                Chart.RemoveObject($"{HudPrefix}SR");
            }
        }

        private void DrawChannels(List<SRChannel> channels, string tag, double price, DateTime startTime, DateTime endTime)
        {
            if (channels == null || channels.Count == 0) return;

            for (int i = 0; i < channels.Count; i++)
            {
                var ch = channels[i];
                string rectName = $"{Prefix}{tag}_RECT_{i}";
                string lineTopName = $"{Prefix}{tag}_LINETOP_{i}";
                string lineBotName = $"{Prefix}{tag}_LINEBOT_{i}";
                string lineMidName = $"{Prefix}{tag}_LINEMID_{i}";

                // Determine Zone State & Color
                Color baseColor;
                if (price > ch.High)
                {
                    baseColor = SupColor; // Support
                }
                else if (price < ch.Low)
                {
                    baseColor = ResColor; // Resistance
                }
                else
                {
                    baseColor = InChannelColor; // Inside channel
                }

                Color fillColor = Color.FromArgb(ZoneOpacity, baseColor.R, baseColor.G, baseColor.B);

                bool isSingleLine = Math.Abs(ch.High - ch.Low) < 1e-5;

                if (isSingleLine)
                {
                    // 1. Clean up unused zone rectangle and border lines
                    Chart.RemoveObject(rectName);
                    Chart.RemoveObject(lineTopName);
                    Chart.RemoveObject(lineBotName);

                    // 2. Draw single horizontal level
                    if (Chart.FindObject(lineMidName) is ChartTrendLine line)
                    {
                        line.Time1 = startTime;
                        line.Y1 = ch.Mid;
                        line.Time2 = endTime;
                        line.Y2 = ch.Mid;
                        line.Color = baseColor;
                        line.Thickness = 2;
                        line.LineStyle = LineStyle.Solid;
                    }
                    else
                    {
                        Chart.DrawTrendLine(lineMidName, startTime, ch.Mid, endTime, ch.Mid, baseColor, 2, LineStyle.Solid);
                    }
                }
                else
                {
                    // 1. Clean up unused single midline (prevents phantom line in middle of zone!)
                    Chart.RemoveObject(lineMidName);

                    // 2. Shaded rectangle zone
                    if (Chart.FindObject(rectName) is ChartRectangle rect)
                    {
                        rect.Time1 = startTime;
                        rect.Y1 = ch.High;
                        rect.Time2 = endTime;
                        rect.Y2 = ch.Low;
                        rect.Color = fillColor;
                        rect.IsFilled = true;
                    }
                    else
                    {
                        var newRect = Chart.DrawRectangle(rectName, startTime, ch.High, endTime, ch.Low, fillColor);
                        newRect.IsFilled = true;
                    }

                    // 3. Top border line
                    if (Chart.FindObject(lineTopName) is ChartTrendLine lTop)
                    {
                        lTop.Time1 = startTime;
                        lTop.Y1 = ch.High;
                        lTop.Time2 = endTime;
                        lTop.Y2 = ch.High;
                        lTop.Color = baseColor;
                        lTop.Thickness = 1;
                        lTop.LineStyle = LineStyle.Solid;
                    }
                    else
                    {
                        Chart.DrawTrendLine(lineTopName, startTime, ch.High, endTime, ch.High, baseColor, 1, LineStyle.Solid);
                    }

                    // 4. Bottom border line
                    if (Chart.FindObject(lineBotName) is ChartTrendLine lBot)
                    {
                        lBot.Time1 = startTime;
                        lBot.Y1 = ch.Low;
                        lBot.Time2 = endTime;
                        lBot.Y2 = ch.Low;
                        lBot.Color = baseColor;
                        lBot.Thickness = 1;
                        lBot.LineStyle = LineStyle.Solid;
                    }
                    else
                    {
                        Chart.DrawTrendLine(lineBotName, startTime, ch.Low, endTime, ch.Low, baseColor, 1, LineStyle.Solid);
                    }
                }
            }
        }

        private void DrawPivotLabels(int maxIndex)
        {
            int startIdx = Math.Max(0, maxIndex - Loopback);
            for (int i = startIdx + PivotPeriod; i <= maxIndex - PivotPeriod; i++)
            {
                double hi = (SourceMode == PivotSourceMode.HighLow) ? Bars.HighPrices[i] : Math.Max(Bars.ClosePrices[i], Bars.OpenPrices[i]);
                double lo = (SourceMode == PivotSourceMode.HighLow) ? Bars.LowPrices[i] : Math.Min(Bars.ClosePrices[i], Bars.OpenPrices[i]);

                if (double.IsNaN(hi) || double.IsNaN(lo)) continue;

                bool isPh = true;
                for (int j = 1; j <= PivotPeriod; j++)
                {
                    double vL = (SourceMode == PivotSourceMode.HighLow) ? Bars.HighPrices[i - j] : Math.Max(Bars.ClosePrices[i - j], Bars.OpenPrices[i - j]);
                    double vR = (SourceMode == PivotSourceMode.HighLow) ? Bars.HighPrices[i + j] : Math.Max(Bars.ClosePrices[i + j], Bars.OpenPrices[i + j]);
                    if (double.IsNaN(vL) || double.IsNaN(vR) || hi < vL || hi <= vR) { isPh = false; break; }
                }

                if (isPh)
                {
                    string hName = $"{Prefix}PP_H_{i}";
                    Chart.DrawText(hName, "H", Bars.OpenTimes[i], Bars.HighPrices[i] + Symbol.PipSize * 2, ResColor);
                }

                bool isPl = true;
                for (int j = 1; j <= PivotPeriod; j++)
                {
                    double vL = (SourceMode == PivotSourceMode.HighLow) ? Bars.LowPrices[i - j] : Math.Min(Bars.ClosePrices[i - j], Bars.OpenPrices[i - j]);
                    double vR = (SourceMode == PivotSourceMode.HighLow) ? Bars.LowPrices[i + j] : Math.Min(Bars.ClosePrices[i + j], Bars.OpenPrices[i + j]);
                    if (double.IsNaN(vL) || double.IsNaN(vR) || lo > vL || lo >= vR) { isPl = false; break; }
                }

                if (isPl)
                {
                    string lName = $"{Prefix}PP_L_{i}";
                    Chart.DrawText(lName, "L", Bars.OpenTimes[i], Bars.LowPrices[i] - Symbol.PipSize * 2, SupColor);
                }
            }
        }

        private void CheckAlerts(List<SRChannel> channels, string tfLabel, double prevClose, double curClose)
        {
            if (channels == null || channels.Count == 0) return;

            if (AlertBreakout)
            {
                var (brokenRes, brokenSup, ch) = SREngine.CheckBreakout(channels, prevClose, curClose);
                if (brokenRes)
                {
                    TriggerAlert($"[S/R Breakout] {SymbolName} ({TimeFrame.Name}) broken ABOVE {tfLabel} Resistance Zone [{ch.Low:F5} - {ch.High:F5}]");
                }
                else if (brokenSup)
                {
                    TriggerAlert($"[S/R Breakout] {SymbolName} ({TimeFrame.Name}) broken BELOW {tfLabel} Support Zone [{ch.Low:F5} - {ch.High:F5}]");
                }
            }

            if (AlertTouch)
            {
                foreach (var ch in channels)
                {
                    bool wasIn = prevClose >= ch.Low && prevClose <= ch.High;
                    bool isIn = curClose >= ch.Low && curClose <= ch.High;
                    if (!wasIn && isIn)
                    {
                        TriggerAlert($"[S/R Touch] {SymbolName} ({TimeFrame.Name}) entered {tfLabel} Zone [{ch.Low:F5} - {ch.High:F5}] (Pivots: {ch.PivotCount}, Touches: {ch.BarTouchCount})");
                    }
                }
            }
        }

        private void TriggerAlert(string msg)
        {
            if (AlertSound) Notifications.PlaySound(SoundType.Announcement);
            if (AlertPopup) Notifications.ShowPopup("S/R Alert", msg, PopupNotificationState.Information);
            Print(msg);
        }

        private void CleanupStaleObjects()
        {
            var keep = new HashSet<string>();
            if (ShowChannels)
            {
                for (int i = 0; i < _curChannels.Count; i++)
                {
                    var ch = _curChannels[i];
                    if (Math.Abs(ch.High - ch.Low) < 1e-5)
                    {
                        keep.Add($"{Prefix}cur_LINEMID_{i}");
                    }
                    else
                    {
                        keep.Add($"{Prefix}cur_RECT_{i}");
                        keep.Add($"{Prefix}cur_LINETOP_{i}");
                        keep.Add($"{Prefix}cur_LINEBOT_{i}");
                    }
                }
            }

            if (ShowPivotPoints)
            {
                int startIdx = Math.Max(0, Bars.Count - 1 - Loopback);
                for (int i = startIdx; i <= Bars.Count - 1; i++)
                {
                    keep.Add($"{Prefix}PP_H_{i}");
                    keep.Add($"{Prefix}PP_L_{i}");
                }
            }

            var toRemove = new List<string>();
            foreach (var obj in Chart.Objects)
            {
                if (obj.Name.StartsWith(Prefix, StringComparison.Ordinal) && !keep.Contains(obj.Name))
                {
                    toRemove.Add(obj.Name);
                }
            }
            foreach (var name in toRemove)
            {
                Chart.RemoveObject(name);
            }
        }

        protected override void OnDestroy()
        {
            Timer.Stop();
            Bars.BarOpened -= OnBarOpened;
            if (!_isPrimarySameAsChart && _primaryBars != null)
            {
                _primaryBars.HistoryLoaded -= OnPrimaryHistoryLoaded;
                _primaryBars.BarOpened -= OnPrimaryBarOpened;
            }

            if (_dailyBars != null && _dailyBars != Bars)
            {
                _dailyBars.HistoryLoaded -= OnDailyHistoryLoaded;
                _dailyBars.BarOpened -= OnDailyBarOpened;
            }

            if (_atr1hBars != null && _atr1hBars != Bars) _atr1hBars.HistoryLoaded -= OnSecondaryHistoryLoaded;

            var toRemove = new List<string>();
            foreach (var obj in Chart.Objects)
            {
                if (obj.Name.StartsWith(Prefix, StringComparison.Ordinal) ||
                    obj.Name.StartsWith(HudPrefix, StringComparison.Ordinal))
                {
                    toRemove.Add(obj.Name);
                }
            }
            foreach (var name in toRemove)
            {
                Chart.RemoveObject(name);
            }
        }
    }
}
