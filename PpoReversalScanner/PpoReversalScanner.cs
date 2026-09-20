using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo
{
    /// <summary>
    /// PPO Reversal &amp; Continuation Scanner for the cTrader 2.0 suite (Daily Close Architecture):
    /// 1. PPO (16, 32, 9) Cross Trigger (Zero-Line Directional Classifier, Unconstrained Corridor):
    ///    - Short Reversal (PPO > 0): Fading local tops / blow-offs.
    ///    - Short Continuation (PPO <= 0): Zero-line rejection in macro downtrends.
    ///    - Long Reversal (PPO < 0): Wholesale bottom capitulation / Wyckoff Springs (unconstrained deep negatives).
    ///    - Long Continuation (PPO >= 0): Zero-line bounce in dominant bull trends.
    /// 2. 7-Day RSI(9) Footprint Matrix: Scans closed bars [1..7] for proven extremes:
    ///    - Long Continuation: MaxRSI >= 68.0 (Power Thrust)
    ///    - Long Reversal: MinRSI <= 32.0 (Wyckoff Capitulation Dip)
    ///    - Short Continuation: MinRSI <= 32.0 (Breakdown Dump)
    ///    - Short Reversal: MaxRSI >= 68.0 (Blow-Off Climax)
    ///    - Recency Check resolves dual-touch ambiguity (most recent extreme dictates regime).
    /// 3. Close Location Value (CLV / LKV): Gatekeeper requiring Longs >= 0.65 (top 35%) and Shorts <= 0.35 (bottom 35%).
    /// 4. Execution Timing: Evaluated at 2:15 PM EST (45m before daily cash close sweet spot) &amp; EOD closed-bar scans.
    /// 5. Shared Risk Baselines: Hard VIX-Stop (&lt; 25), Benchmark Wind (SPY &gt;= 50 SMA for Longs),
    ///    Daily 200 SMA secular filter (+/- 0.5 ATR neutral zone), Daily ATR(14) &gt;= $0.50, Rel ATR &gt;= 1.0%,
    ///    No Disaster Gap <= 2.0x ATR, Max Bar Expansion (standard disabled for retrace/displacement).
    /// 6. Campaign Sizing: 100% Single Market Entry, Hard SL at 1.0 ATR (1.0R), Profit Guard at +1.25 ATR,
    ///    Take Profit at +2.50 ATR (2.5R), Day 5 Zombie Exit / Ratchet SL at 14:15 EST.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class PpoReversalScanner : Robot
    {
        // =========================================================================
        // --- 1. General Scan Setup & Execution Timing ---
        // =========================================================================
        [Parameter("Scan Direction", Group = "1. Scan Setup", DefaultValue = ScanAllowedDirection.Both)]
        public ScanAllowedDirection AllowedDirection { get; set; } = ScanAllowedDirection.Both;

        [Parameter("Watchlist Name", Group = "1. Scan Setup", DefaultValue = "Screener")]
        public string WatchlistName { get; set; } = "Screener";

        [Parameter("Schedule Mode", Group = "1. Scan Setup", DefaultValue = ScanScheduleMode.Hourly)]
        public ScanScheduleMode ScheduleMode { get; set; } = ScanScheduleMode.Hourly;

        [Parameter("Daily Close Hour (ET)", Group = "1. Scan Setup", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int DailyCloseHourEt { get; set; } = 16;

        [Parameter("Scan TimeFrame", Group = "1. Scan Setup", DefaultValue = ScannerTimeFrame.Daily)]
        public ScannerTimeFrame ScanTimeFrame { get; set; } = ScannerTimeFrame.Daily;

        public TimeFrame ResolvedScanTimeFrame => ScanTimeFrame switch
        {
            ScannerTimeFrame.Daily => TimeFrame.Daily,
            ScannerTimeFrame.Hour4 => TimeFrame.Hour4,
            ScannerTimeFrame.Hour => TimeFrame.Hour,
            ScannerTimeFrame.Minute30 => TimeFrame.Minute30,
            ScannerTimeFrame.Minute15 => TimeFrame.Minute15,
            _ => TimeFrame.Daily
        };

        [Parameter("Scan Immediately On Start", Group = "1. Scan Setup", DefaultValue = true)]
        public bool ScanOnStart { get; set; } = true;

        [Parameter("Custom Interval (sec)", Group = "1. Scan Setup", DefaultValue = 900, MinValue = 60, Step = 60)]
        public int ScanIntervalSeconds { get; set; } = 900;

        [Parameter("Symbols per Batch Tick", Group = "1. Scan Setup", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int BatchSize { get; set; } = 20;

        [Parameter("Min Bars to Scan", Group = "1. Scan Setup", DefaultValue = 250, MinValue = 100)]
        public int MinBarsToScan { get; set; } = 250;

        [Parameter("Max Setup Age (Trading Days, 0 = Today Only)", Group = "1. Scan Setup", DefaultValue = 1, MinValue = 0, MaxValue = 5)]
        public int MaxSetupAgeTradingDays { get; set; } = 1;

        // =========================================================================
        // --- 2. PPO Core Parameters (Pine Script v6 Aligned) ---
        // =========================================================================
        [Parameter("PPO Fast Period", Group = "2. PPO Parameters", DefaultValue = 16, MinValue = 2)]
        public int PpoFastPeriod { get; set; } = 16;

        [Parameter("PPO Slow Period", Group = "2. PPO Parameters", DefaultValue = 32, MinValue = 5)]
        public int PpoSlowPeriod { get; set; } = 32;

        [Parameter("PPO Signal Period", Group = "2. PPO Parameters", DefaultValue = 9, MinValue = 1)]
        public int PpoSignalPeriod { get; set; } = 9;

        [Parameter("Oscillator MA Type", Group = "2. PPO Parameters", DefaultValue = PpoMaType.Exponential)]
        public PpoMaType OscMaType { get; set; } = PpoMaType.Exponential;

        [Parameter("Signal MA Type", Group = "2. PPO Parameters", DefaultValue = PpoMaType.Exponential)]
        public PpoMaType SigMaType { get; set; } = PpoMaType.Exponential;

        // =========================================================================
        // =========================================================================
        // --- 3. RSI Footprint & Polarity Matrix ---
        // =========================================================================
        [Parameter("Daily RSI Period", Group = "3. RSI Footprint Matrix", DefaultValue = 10, MinValue = 2)]
        public int DailyRsiPeriod { get; set; } = 10;

        public int DailyRsi14Period
        {
            get => DailyRsiPeriod;
            set => DailyRsiPeriod = value;
        }

        [Parameter("RSI Footprint Lookback (Days)", Group = "3. RSI Footprint Matrix", DefaultValue = 10, MinValue = 2, MaxValue = 60)]
        public int ReversalCatalystLookbackBars { get; set; } = 10;

        [Parameter("Long Continuation Max Cooling RSI", Group = "3. RSI Footprint Matrix", DefaultValue = 45.0, MinValue = 35.0, MaxValue = 65.0, Step = 1.0)]
        public double LongContinuationRsiMin { get; set; } = 45.0;

        [Parameter("Long Reversal Max Capitulation Dip", Group = "3. RSI Footprint Matrix", DefaultValue = 35.0, MinValue = 15.0, MaxValue = 45.0, Step = 1.0)]
        public double LongReversalRsiMax { get; set; } = 35.0;

        [Parameter("Short Continuation Min Bounce RSI", Group = "3. RSI Footprint Matrix", DefaultValue = 55.0, MinValue = 35.0, MaxValue = 65.0, Step = 1.0)]
        public double ShortContinuationRsiMax { get; set; } = 55.0;

        [Parameter("Short Reversal Min Blow-Off Peak", Group = "3. RSI Footprint Matrix", DefaultValue = 65.0, MinValue = 55.0, MaxValue = 85.0, Step = 1.0)]
        public double ShortReversalRsiMin { get; set; } = 65.0;

        // =========================================================================
        // --- 4. Benchmark Wind & Risk Baselines ---
        // =========================================================================
        [Parameter("Require Benchmark Wind (SPY for Longs)", Group = "4. Risk & Benchmark Filters", DefaultValue = true)]
        public bool RequireBenchmarkWind { get; set; } = true;

        [Parameter("Benchmark Symbol", Group = "4. Risk & Benchmark Filters", DefaultValue = "SPY.US")]
        public string BenchmarkSymbol { get; set; } = "SPY.US";

        [Parameter("SPY 50 SMA Period", Group = "4. Risk & Benchmark Filters", DefaultValue = 50, MinValue = 10)]
        public int SpySmaPeriod { get; set; } = 50;

        [Parameter("Require 200 SMA Filter (Strict: Long > 200 SMA, Short < 200 SMA)", Group = "4. Risk & Benchmark Filters", DefaultValue = true)]
        public bool Require200SmaFilter { get; set; } = true;

        [Parameter("Daily 200 SMA Period", Group = "4. Risk & Benchmark Filters", DefaultValue = 200, MinValue = 20)]
        public int Sma200Period { get; set; } = 200;

        [Parameter("200 SMA Neutrality Buffer (x ATR, 0 = Off)", Group = "4. Risk & Benchmark Filters", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 2.0, Step = 0.05)]
        public double Sma200BufferAtr { get; set; } = 0.0;

        [Parameter("Require Asymmetric 50 EMA Filter", Group = "4. Risk & Benchmark Filters", DefaultValue = false)]
        public bool RequireAsymmetric50EmaFilter { get; set; } = false;

        [Parameter("Daily 50 EMA Period", Group = "4. Risk & Benchmark Filters", DefaultValue = 50, MinValue = 10)]
        public int Ema50Period { get; set; } = 50;

        [Parameter("Require VIX Stop (< 25)", Group = "4. Risk & Benchmark Filters", DefaultValue = true)]
        public bool RequireVixFilter { get; set; } = true;

        [Parameter("VIX Symbol", Group = "4. Risk & Benchmark Filters", DefaultValue = "VIX")]
        public string VixSymbol { get; set; } = "VIX";

        [Parameter("Max VIX Threshold", Group = "4. Risk & Benchmark Filters", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0, Step = 0.5)]
        public double MaxVixThreshold { get; set; } = 25.0;


        [Parameter("Require Relative Volatility Floor", Group = "4. Risk & Benchmark Filters", DefaultValue = true)]
        public bool RequireRelativeVolatilityFloor { get; set; } = true;

        [Parameter("Min Relative ATR (% of Price)", Group = "4. Risk & Benchmark Filters", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double MinRelativeAtrPct { get; set; } = 1.0;

        [Parameter("Max Disaster Gap ATR", Group = "4. Risk & Benchmark Filters", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1)]
        public double MaxDisasterGapAtr { get; set; } = 2.0;

        [Parameter("Require Max Bar Expansion Filter", Group = "4. Risk & Benchmark Filters", DefaultValue = false)]
        public bool RequireMaxBarExpansionFilter { get; set; } = false;

        [Parameter("Max Bar Expansion (x ATR)", Group = "4. Risk & Benchmark Filters", DefaultValue = 2.50, MinValue = 0.5, MaxValue = 5.0, Step = 0.05)]
        public double MaxBarExpansionAtr { get; set; } = 2.50;

        [Parameter("Require Close / Live Location Value (CLV/LKV)", Group = "4. Risk & Benchmark Filters", DefaultValue = true)]
        public bool RequireClvFilter { get; set; } = true;

        [Parameter("Min CLV/LKV for Longs (Top %)", Group = "4. Risk & Benchmark Filters", DefaultValue = 0.65, MinValue = 0.50, MaxValue = 0.95, Step = 0.05)]
        public double MinClvLong { get; set; } = 0.65;

        [Parameter("Max CLV/LKV for Shorts (Bottom %)", Group = "4. Risk & Benchmark Filters", DefaultValue = 0.35, MinValue = 0.05, MaxValue = 0.50, Step = 0.05)]
        public double MaxClvShort { get; set; } = 0.35;

        // =========================================================================
        // --- 5. Campaign Sizing (100% Single Market Entry) ---
        // =========================================================================
        [Parameter("Stop Loss ATR Multiple", Group = "5. Sizing & Risk Bracket", DefaultValue = 1.0, MinValue = 0.25, MaxValue = 3.0, Step = 0.05)]
        public double SlAtrMultiple { get; set; } = 1.0;

        [Parameter("Take Profit ATR Multiple", Group = "5. Sizing & Risk Bracket", DefaultValue = 2.50, MinValue = 0.5, MaxValue = 6.0, Step = 0.1)]
        public double PtAtrMultiple { get; set; } = 2.50;

        [Parameter("Profit Guard Trigger (ATR)", Group = "5. Sizing & Risk Bracket", DefaultValue = 1.25, MinValue = 0.5, MaxValue = 3.0, Step = 0.1)]
        public double ProfitGuardAtrMultiple { get; set; } = 1.25;

        // =========================================================================
        // --- 6. Alerts & Notifications ---
        // =========================================================================
        [Parameter("Alert Sound", Group = "6. Alerts", DefaultValue = true)]
        public bool AlertSound { get; set; } = true;

        [Parameter("Alert Popup", Group = "6. Alerts", DefaultValue = true)]
        public bool AlertPopup { get; set; } = true;

        // Active Armed Setup Info
        public class ArmedPpoSetupInfo
        {
            public string Symbol { get; set; } = "";
            public string Direction { get; set; } = "BUY";
            public PpoSetupType SetupType { get; set; }
            public string SetupDescription { get; set; } = "";
            public string TimingTag { get; set; } = "[LIVE BAR]"; // [LIVE BAR] for intraday cross, [CLOSED BAR] for EOD close cross
            public double PpoLine { get; set; }
            public double SignalLine { get; set; }
            public double Histogram { get; set; }
            public double CurrentRsi14 { get; set; }
            public double Bar1Rsi14 { get; set; }
            public double Clv { get; set; } = double.NaN;
            public double RsDelta20 { get; set; } = double.NaN;
            public double RetracementLimitPrice { get; set; } = double.NaN;
            public double FootprintExtremeRsi14 { get; set; }
            public int BarsSinceExtreme { get; set; }
            public double DailyAtr { get; set; }
            public double Sma200 { get; set; }
            public double Daily50Ema { get; set; }
            public double LivePrice { get; set; }
            public double EntryPrice { get; set; }
            public double StopLossPrice { get; set; }
            public double TakeProfitPrice { get; set; }
            public string RsiDivergenceStatus { get; set; } = "None";
            public string RsiDivergenceShortTag { get; set; } = "";
            public DateTime SignalBarTime { get; set; }
            public DateTime FirstDetected { get; set; }
            public DateTime LastSeenTime { get; set; }
        }

        // State Tracking
        private readonly List<string> _watchlistSymbols = new();
        private readonly Dictionary<string, ArmedPpoSetupInfo> _activeArmedSetups = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentAlertMessages = new();
        private Stopwatch _passStopwatch = new();
        private DateTime _lastScanTime = DateTime.MinValue;
        private DateTime _nextScanTime = DateTime.MinValue;
        private int _scanPassCount;
        private int _totalAlertsFired;
        private int _currentBatchIndex;
        private int _currentPassScanned;
        private int _currentPassSkipped;
        private int _currentPassAlerts;
        private readonly Dictionary<string, int> _currentPassRejects = new();
        private bool _isPassInProgress;
        private bool _isStopped;
        private string _resolvedBenchmarkSymbol = "SPY.US";
        private string _resolvedVixSymbol = "VIX";
        private (bool Passes, double LiveSpy, double Sma50, string ResolvedSymbol, string Detail) _benchmarkWindLongResult = (true, double.NaN, double.NaN, "SPY.US", "Pending first check");
        private (bool Passes, double LiveVix, string ResolvedSymbol, string Detail) _vixHaltResult = (true, double.NaN, "VIX", "Pending first check");
        private double[]? _cachedBenchDailyCloses;
        private DateTime _lastMacroCheckTime = DateTime.MinValue;
        private DateTime _lastHudUpdateTime = DateTime.MinValue;
        // SPY 50 SMA cache: only recompute when ET date changes
        private double _cachedSpy50Sma = double.NaN;
        private DateTime _lastSpySmaDate = DateTime.MinValue;
        private const string HudTableName = "PPO_SCANNER_HUD";
        private Button? _scanNowButton;

        protected override void OnStart()
        {
            _isStopped = false;
            _isPassInProgress = false;
            _activeArmedSetups.Clear();
            _recentAlertMessages.Clear();
            _scanPassCount = 0;
            _totalAlertsFired = 0;
            _currentBatchIndex = 0;
            _currentPassScanned = 0;
            _currentPassSkipped = 0;
            _currentPassAlerts = 0;

            // 1. Resolve Broker Symbol Conventions (e.g. US equities have '.US' suffix, VIX / VIXY.US)
            _resolvedBenchmarkSymbol = ResolveSymbolName(BenchmarkSymbol, "SPY.US", "SPY", "SPY.ETF");
            _resolvedVixSymbol = ResolveSymbolName(VixSymbol, "VIX", "VIXY.US", ".VIX", "VOLX", "VXX.US");

            // 2. Query initial live values immediately on startup
            _vixHaltResult = CheckVixGate();
            _benchmarkWindLongResult = CheckBenchmarkWind();

            LoadWatchlist();
            CreateScanButton();

            string liveVixStr = !double.IsNaN(_vixHaltResult.LiveVix) ? _vixHaltResult.LiveVix.ToString("F2") : "N/A";
            string liveSpyStr = !double.IsNaN(_benchmarkWindLongResult.LiveSpy) ? $"${_benchmarkWindLongResult.LiveSpy:F2}" : "N/A";
            string spy50SmaStr = !double.IsNaN(_benchmarkWindLongResult.Sma50) ? $"${_benchmarkWindLongResult.Sma50:F2}" : "N/A";

            Print($"[PpoReversalScanner] Started. Watchlist '{WatchlistName}' loaded with {_watchlistSymbols.Count} symbols.");
            Print($"[PpoReversalScanner] Schedule Mode: {ScheduleMode} | Scan TimeFrame: {ScanTimeFrame} (Evaluates last completed closed bar).");
            Print($"[PpoReversalScanner] PPO Config ({ScanTimeFrame}): ({PpoFastPeriod}, {PpoSlowPeriod}, {PpoSignalPeriod}) | Unconstrained.");
            Print($"[PpoReversalScanner] RSI({DailyRsiPeriod}) Footprint Matrix ({ScanTimeFrame}): {ScaleLookbackToScanBars(ReversalCatalystLookbackBars)}b = {ReversalCatalystLookbackBars}d Catalyst Memory (Spring <= {LongReversalRsiMax:F0}, Thrust >= {LongContinuationRsiMin:F0}) with Recency Check.");
            Print($"[PpoReversalScanner] Risk Filters (Daily): Rel ATR Floor >={MinRelativeAtrPct:F1}% | Disaster Gap <= {MaxDisasterGapAtr:F1}x ATR | Max Bar Expansion: {(RequireMaxBarExpansionFilter ? $"<= {MaxBarExpansionAtr:F2} ATR" : "DISABLED (Retrace/Displacement Friendly)")}.");
            Print($"[PpoReversalScanner] Moving Average Filters (Daily): 200 SMA {(Require200SmaFilter ? $"STRICT MACRO (Longs > 200 SMA, Shorts < 200 SMA, Period={Sma200Period})" : "DISABLED")}.");
            Print($"[PpoReversalScanner] Benchmark Wind: {(RequireBenchmarkWind ? $"ENABLED (Symbol='{_resolvedBenchmarkSymbol}', Live SPY={liveSpyStr}, 50 SMA={spy50SmaStr}, Longs Allowed={_benchmarkWindLongResult.Passes})" : "DISABLED")}.");
            Print($"[PpoReversalScanner] Hard VIX Stop: {(RequireVixFilter ? $"ENABLED (Symbol='{_resolvedVixSymbol}', Live VIX={liveVixStr}, Threshold < {MaxVixThreshold:F1}, Trade Allowed={_vixHaltResult.Passes})" : "DISABLED")}.");
            Print($"[PpoReversalScanner] Campaign Sizing: 68.18% Retracement Limit Entry | Hard SL {SlAtrMultiple:F2}x ATR (1.0R) | TP +{PtAtrMultiple:F2}x ATR (2.0R) | Profit Guard +{ProfitGuardAtrMultiple:F2}x ATR | Zombie Exit Day 5.");

            DrawHud(0, _watchlistSymbols.Count, 0, "Starting scan pass #1...");

            if (ScanOnStart)
            {
                _nextScanTime = Server.TimeInUtc;
            }
            else
            {
                _nextScanTime = CalculateNextScanTime(Server.TimeInUtc);
            }
            Timer.Start(TimeSpan.FromMilliseconds(100));
        }

        protected override void OnTimer()
        {
            if (_isStopped) return;

            DateTime now = Server.TimeInUtc;

            if (_isPassInProgress)
            {
                ProcessNextBatch();
            }
            else if (now >= _nextScanTime)
            {
                StartScanPass();
            }
            else
            {
                // Periodically keep live VIX and SPY values fresh during idle (every 10 seconds)
                if ((now - _lastMacroCheckTime).TotalSeconds >= 10.0)
                {
                    _lastMacroCheckTime = now;
                    if (RequireVixFilter) _vixHaltResult = CheckVixGate();
                    if (RequireBenchmarkWind) _benchmarkWindLongResult = CheckBenchmarkWind();
                }

                var remaining = _nextScanTime - now;
                if (remaining.TotalSeconds > 0 && ScheduleMode != ScanScheduleMode.ManualOnly)
                {
                    // Throttle HUD redraw to once per second during idle countdown
                    if ((now - _lastHudUpdateTime).TotalSeconds >= 1.0)
                    {
                        _lastHudUpdateTime = now;
                        int mins = (int)remaining.TotalMinutes;
                        int secs = remaining.Seconds;
                        string countdown = (mins > 0) ? $"{mins}m {secs:D2}s" : $"{secs}s";
                        string targetStr = _nextScanTime.ToString("HH:mm:ss") + " UTC";
                        string modeStr = ScheduleMode switch
                        {
                            ScanScheduleMode.Hourly => "Hourly (:00:02 UTC)",
                            ScanScheduleMode.DailyAfterClose => $"Daily ({DailyCloseHourEt}:00:02 ET Close)",
                            ScanScheduleMode.Every15Minutes => "Quarter-Hour (:15/:30/:45:02)",
                            ScanScheduleMode.CustomInterval => $"Every {ScanIntervalSeconds}s",
                            ScanScheduleMode.ManualOnly => "Manual Only",
                            _ => "Next scan"
                        };
                        string status = $"Idle ({modeStr} at {targetStr} — in {countdown})";
                        DrawHud(_currentPassScanned, _watchlistSymbols.Count, _currentPassAlerts, status);
                    }
                }
            }
        }

        protected override void OnStop()
        {
            _isStopped = true;
            Timer.Stop();
            Chart.RemoveObject(HudTableName);
            if (_scanNowButton != null)
            {
                _scanNowButton.Click -= OnScanNowButtonClick;
                Chart.RemoveControl(_scanNowButton);
                _scanNowButton = null;
            }
            Print($"[PpoReversalScanner] Stopped. Total alerts fired: {_totalAlertsFired}. Active setups: {_activeArmedSetups.Count}.");
        }

        private void CreateScanButton()
        {
            try
            {
                _scanNowButton = new Button
                {
                    Text = "▶ SCAN NOW",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 8, 12, 0),
                    Padding = new Thickness(14, 7, 14, 7),
                    BackgroundColor = Color.FromHex("#1E293B"),
                    ForegroundColor = Color.White
                };
                _scanNowButton.Click += OnScanNowButtonClick;
                Chart.AddControl(_scanNowButton);
            }
            catch (Exception ex)
            {
                Print($"[PpoReversalScanner] Note: Could not attach on-chart button: {ex.Message}");
            }
        }

        private void OnScanNowButtonClick(ButtonClickEventArgs obj)
        {
            TriggerManualScan("On-Chart Button Click");
        }

        public void TriggerManualScan(string source = "Manual")
        {
            if (_isPassInProgress)
            {
                Print($"[PpoReversalScanner] Scan pass already in progress. Ignoring request from {source}.");
                return;
            }

            Print($"[PpoReversalScanner] Triggering scan pass immediately ({source})!");
            StartScanPass();
        }

        private void LoadWatchlist()
        {
            _watchlistSymbols.Clear();
            try
            {
                var wl = Watchlists.FirstOrDefault(w => string.Equals(w.Name, WatchlistName, StringComparison.OrdinalIgnoreCase));
                if (wl != null && wl.SymbolNames.Any())
                {
                    foreach (var s in wl.SymbolNames)
                    {
                        if (!string.IsNullOrWhiteSpace(s) && !_watchlistSymbols.Contains(s))
                            _watchlistSymbols.Add(s);
                    }
                }
                else
                {
                    var availableNames = Watchlists.Select(w => $"'{w.Name}' ({w.SymbolNames.Count})").ToArray();
                    string availStr = availableNames.Length > 0 ? string.Join(", ", availableNames) : "None";
                    Print($"[PpoReversalScanner] Watchlist '{WatchlistName}' {(wl == null ? "not found" : "is empty")}. Available: {availStr}. Falling back to '{SymbolName}'.");
                    _watchlistSymbols.Add(SymbolName);
                }
            }
            catch (Exception ex)
            {
                Print($"[PpoReversalScanner] Error loading watchlist: {ex.Message}. Falling back to '{SymbolName}'.");
                _watchlistSymbols.Add(SymbolName);
            }
        }

        private string ResolveSymbolName(string preferred, params string[] fallbacks)
        {
            if (IsSymbolValid(preferred)) return preferred;
            foreach (var c in fallbacks)
            {
                if (!string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase) && IsSymbolValid(c))
                {
                    Print($"[PpoReversalScanner] Preferred symbol '{preferred}' not found on broker. Auto-resolved to '{c}'.");
                    return c;
                }
            }
            return preferred;
        }

        private bool IsSymbolValid(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            try
            {
                var s = Symbols.GetSymbol(name);
                if (s != null) return true;
            }
            catch { }
            try
            {
                var b = MarketData.GetBars(ResolvedScanTimeFrame, name);
                if (b != null && b.Count > 0) return true;
            }
            catch { }
            return false;
        }

        private static readonly HashSet<string> StandardForexPairs = new(StringComparer.OrdinalIgnoreCase)
        {
            "EURUSD", "GBPUSD", "USDJPY", "USDCHF", "USDCAD", "AUDUSD", "NZDUSD",
            "EURGBP", "EURJPY", "EURCHF", "EURCAD", "EURAUD", "EURNZD",
            "GBPJPY", "GBPCHF", "GBPCAD", "GBPAUD", "GBPNZD",
            "AUDJPY", "AUDCHF", "AUDCAD", "AUDNZD",
            "NZDJPY", "NZDCHF", "NZDCAD",
            "CADJPY", "CADCHF", "CHFJPY"
        };

        private static readonly HashSet<string> MajorCurrencies = new(StringComparer.OrdinalIgnoreCase)
        {
            "USD", "EUR", "GBP", "JPY", "CHF", "AUD", "CAD", "NZD", "SEK", "NOK", "DKK", "SGD", "HKD", "ZAR", "MXN", "TRY", "PLN", "CNH", "HUF", "CZK"
        };

        /// <summary>
        /// Returns true if the symbol is a standard Forex currency pair (e.g. EURUSD, GBPJPY, EUR/USD).
        /// Covers all 28 G8 Majors & Minors directly with instant O(1) lookup.
        /// Forex pairs naturally trade with daily ATR of 0.4% - 0.7% of price and are exempt from the 1.0% equity volatility floor.
        /// </summary>
        private bool IsForexSymbol(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return false;

            // 1. Instant check against all 28 standard major & minor pairs (e.g. EURUSD, EURUSD.pro, EUR/USD)
            string clean = symbolName.Replace("/", "").Replace(".", "").Replace("_", "").Trim();
            if (clean.Length >= 6)
            {
                string pair = clean.Substring(0, 6);
                if (StandardForexPairs.Contains(pair)) return true;
            }

            // 2. Check broker's official BaseAsset / QuoteAsset classification
            try
            {
                var s = Symbols.GetSymbol(symbolName);
                if (s != null && s.BaseAsset != null && s.QuoteAsset != null)
                {
                    if (MajorCurrencies.Contains(s.BaseAsset.Name) && MajorCurrencies.Contains(s.QuoteAsset.Name))
                        return true;
                }
            }
            catch { }

            // 3. Fallback: Parse ISO 3-letter currency codes
            if (clean.Length >= 6)
            {
                string baseCur = clean.Substring(0, 3);
                string quoteCur = clean.Substring(3, 3);
                if (MajorCurrencies.Contains(baseCur) && MajorCurrencies.Contains(quoteCur))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns true if the symbol is a US equity (ends with ".US").
        /// VIX and SPY macro gates apply only to US equities; FX, metals, commodities and crypto are exempt.
        /// </summary>
        private static bool IsUsEquitySymbol(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return false;
            return symbolName.EndsWith(".US", StringComparison.OrdinalIgnoreCase);
        }

        private void StartScanPass()
        {
            if (_isStopped || _watchlistSymbols.Count == 0) return;

            if (_scanNowButton != null)
            {
                _scanNowButton.Text = "⏳ SCANNING...";
                _scanNowButton.BackgroundColor = Color.FromHex("#D97706");
            }

            _isPassInProgress = true;
            _currentBatchIndex = 0;
            _currentPassScanned = 0;
            _currentPassSkipped = 0;
            _currentPassAlerts = 0;
            _currentPassRejects.Clear();
            _passStopwatch.Restart();

            // Check Hard VIX-Stop (< 25) with live VIX quote (applies to US equities only)
            _vixHaltResult = CheckVixGate();
            if (RequireVixFilter && !_vixHaltResult.Passes)
            {
                Print($"[PpoReversalScanner] [VIX HALT ACTIVE] {_vixHaltResult.Detail}. US EQUITY TRADES (LONGS & SHORTS) BLOCKED! FX/Metals/Crypto exempt.");
            }

            // Check Benchmark Wind (SPY) for Longs with live SPY quote vs 50 SMA (applies to US equities only)
            _benchmarkWindLongResult = CheckBenchmarkWind();

            // Pre-cache Benchmark 20-day Closes for Relative Strength (RS vs SPX) ranking
            UpdateBenchmarkDailyCloses();

            if (RequireBenchmarkWind && AllowedDirection == ScanAllowedDirection.LongOnly && !_benchmarkWindLongResult.Passes)
            {
                Print($"[PpoReversalScanner] [BENCHMARK WIND INACTIVE FOR LONGS] {_benchmarkWindLongResult.Detail}. US equity long scanning halted this pass. FX/Metals/Crypto exempt.");
            }
            else if (RequireBenchmarkWind && !_benchmarkWindLongResult.Passes)
            {
                Print($"[PpoReversalScanner] [BENCHMARK WIND] SPY in Bear Market breakdown ({_benchmarkWindLongResult.Detail}). US EQUITY LONGS BLOCKED! Shorts permitted. FX/Metals/Crypto exempt.");
            }

            DrawHud(0, _watchlistSymbols.Count, 0, "Scanning in progress...");
        }

        private void UpdateBenchmarkDailyCloses()
        {
            try
            {
                var spyBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedBenchmarkSymbol, 50);
                if (spyBars != null && spyBars.Count >= 25)
                {
                    _cachedBenchDailyCloses = new double[spyBars.Count];
                    for (int i = 0; i < spyBars.Count; i++)
                        _cachedBenchDailyCloses[i] = spyBars.ClosePrices[i];
                }
                else
                {
                    _cachedBenchDailyCloses = null;
                }
            }
            catch (Exception ex)
            {
                _cachedBenchDailyCloses = null;
                Print($"[PpoReversalScanner] Note: Benchmark closes cache error ({_resolvedBenchmarkSymbol}): {ex.Message}");
            }
        }

        private (bool Passes, double LiveVix, string ResolvedSymbol, string Detail) CheckVixGate()
        {
            if (!RequireVixFilter)
                return (true, double.NaN, _resolvedVixSymbol, "VIX filter disabled (Bypassed)");

            try
            {
                // 1. Try live price from resolved symbol
                double liveVix = GetLivePrice(_resolvedVixSymbol);
                if (!double.IsNaN(liveVix) && liveVix > 0)
                {
                    var eval = PlaybookEngine.EvaluateVixHalt(liveVix, MaxVixThreshold);
                    return (eval.Passes, liveVix, _resolvedVixSymbol, eval.Reason);
                }

                // 2. Try Daily bars
                var vixBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedVixSymbol, 5);
                if (vixBars != null && vixBars.Count > 0)
                {
                    double closeVix = vixBars.ClosePrices.LastValue;
                    if (!double.IsNaN(closeVix) && closeVix > 0)
                    {
                        var eval = PlaybookEngine.EvaluateVixHalt(closeVix, MaxVixThreshold);
                        return (eval.Passes, closeVix, _resolvedVixSymbol, eval.Reason);
                    }
                }

                return (true, double.NaN, _resolvedVixSymbol, $"VIX data unavailable for '{_resolvedVixSymbol}' — gate bypassed");
            }
            catch (Exception ex)
            {
                return (true, double.NaN, _resolvedVixSymbol, $"VIX check error ({_resolvedVixSymbol}): {ex.Message} — gate bypassed");
            }
        }

        private (bool Passes, double LiveSpy, double Sma50, string ResolvedSymbol, string Detail) CheckBenchmarkWind()
        {
            if (!RequireBenchmarkWind)
                return (true, double.NaN, double.NaN, _resolvedBenchmarkSymbol, "Benchmark Wind disabled (Bypassed)");

            try
            {
                // Refresh SPY 50 SMA only once per ET trading day
                DateTime todayEt = PpoEngine.ConvertUtcToEt(Server.TimeInUtc).Date;
                if (_lastSpySmaDate != todayEt || double.IsNaN(_cachedSpy50Sma))
                {
                    int minSpyBars = SpySmaPeriod + 20;
                    var spyBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedBenchmarkSymbol, minSpyBars);
                    if (spyBars == null || spyBars.Count < minSpyBars)
                    {
                        return (true, double.NaN, double.NaN, _resolvedBenchmarkSymbol, $"SPY data unavailable ({_resolvedBenchmarkSymbol} bars < {minSpyBars}) — filter bypassed");
                    }

                    int n = spyBars.Count;
                    int evalIdx = n - 1;
                    int lastClosed = PpoEngine.GetLastCompletedDailyBarIndex(
                        spyBars.OpenTimes.LastValue, n, Server.TimeInUtc);
                    if (lastClosed < 0) lastClosed = n - 2;
                    if (lastClosed < 0) lastClosed = 0;

                    int startIdx = lastClosed - SpySmaPeriod + 1;
                    if (startIdx < 0) startIdx = 0;

                    double sum50 = 0.0;
                    int count50 = 0;
                    for (int i = startIdx; i <= lastClosed; i++)
                    {
                        sum50 += spyBars.ClosePrices[i];
                        count50++;
                    }
                    _cachedSpy50Sma = count50 > 0 ? sum50 / count50 : double.NaN;
                    _lastSpySmaDate = todayEt;
                }

                double liveSpy = GetLivePrice(_resolvedBenchmarkSymbol);
                if (double.IsNaN(liveSpy) || liveSpy <= 0)
                {
                    var spyBars = MarketData.GetBars(TimeFrame.Daily, _resolvedBenchmarkSymbol);
                    if (spyBars != null && spyBars.Count > 0)
                        liveSpy = spyBars.ClosePrices.LastValue;
                }

                if (double.IsNaN(liveSpy) || liveSpy <= 0)
                    return (true, double.NaN, _cachedSpy50Sma, _resolvedBenchmarkSymbol, "SPY live price unavailable — filter bypassed");

                var eval = PlaybookEngine.EvaluateBenchmarkWind(liveSpy, _cachedSpy50Sma, isShort: false);
                return (eval.IsPermitted, liveSpy, _cachedSpy50Sma, _resolvedBenchmarkSymbol, eval.Reason);
            }
            catch (Exception ex)
            {
                return (true, double.NaN, double.NaN, _resolvedBenchmarkSymbol, $"SPY check error: {ex.Message} — filter bypassed");
            }
        }

        private void ProcessNextBatch()
        {
            int total = _watchlistSymbols.Count;
            if (_currentBatchIndex >= total)
            {
                FinishScanPass();
                return;
            }

            int batchEnd = Math.Min(_currentBatchIndex + BatchSize, total);
            for (int i = _currentBatchIndex; i < batchEnd; i++)
            {
                string sym = _watchlistSymbols[i];
                try
                {
                    var res = ScanSymbol(sym);
                    if (res.Scanned) _currentPassScanned++;
                    else _currentPassSkipped++;
                    if (res.AlertFired) _currentPassAlerts++;
                }
                catch (Exception ex)
                {
                    Print($"[PpoReversalScanner] Error scanning '{sym}': {ex.Message}");
                    _currentPassSkipped++;
                }
            }

            _currentBatchIndex = batchEnd;
            string progress = GetProgressBar(_currentBatchIndex, total);
            DrawHud(_currentBatchIndex, total, _currentPassAlerts, $"Scanning: {progress} ({_currentBatchIndex}/{total})");
        }

        private void FinishScanPass()
        {
            _isPassInProgress = false;
            _passStopwatch.Stop();
            _scanPassCount++;
            _lastScanTime = Server.TimeInUtc;
            _nextScanTime = CalculateNextScanTime(_lastScanTime);

            if (_scanNowButton != null)
            {
                _scanNowButton.Text = "▶ SCAN NOW";
                _scanNowButton.BackgroundColor = Color.FromHex("#1E293B");
            }

            int total = _watchlistSymbols.Count;
            string nextScanDesc = ScheduleMode switch
            {
                ScanScheduleMode.Hourly => $"at {_nextScanTime:HH:mm:ss} UTC (Top of the Hour + 2s)",
                ScanScheduleMode.DailyAfterClose => $"at {_nextScanTime:HH:mm:ss} UTC ({DailyCloseHourEt}:00 ET Market Close + 2s)",
                ScanScheduleMode.Every15Minutes => $"at {_nextScanTime:HH:mm:ss} UTC (15m mark + 2s)",
                ScanScheduleMode.ManualOnly => "Manual Only (Click '▶ SCAN NOW')",
                _ => $"in {ScanIntervalSeconds}s (at {_nextScanTime:HH:mm:ss} UTC)"
            };

            Print($"[PpoReversalScanner] Pass #{_scanPassCount} complete: {_currentPassScanned}/{total} scanned ({_currentPassSkipped} skipped), {_activeArmedSetups.Count} active setups. Next scan {nextScanDesc}.");

            if (_currentPassRejects.Count > 0)
            {
                int totalRejected = 0;
                var parts = new List<string>();
                foreach (var kv in _currentPassRejects.OrderByDescending(x => x.Value))
                {
                    totalRejected += kv.Value;
                    parts.Add($"{kv.Value}x {kv.Key}");
                }
                Print($"[PpoReversalScanner] {totalRejected} rejections: {string.Join(" | ", parts)}.");
            }

            DrawHud(_currentPassScanned, total, _currentPassAlerts);
        }

        private DateTime CalculateNextScanTime(DateTime fromUtc) =>
            PpoEngine.CalculateNextScanTime(ScheduleMode, ScanIntervalSeconds, fromUtc, DailyCloseHourEt);

        public static DateTime CalculateNextScanTime(ScanScheduleMode mode, int customIntervalSeconds, DateTime fromUtc, int dailyCloseHourEt = 16) =>
            PpoEngine.CalculateNextScanTime(mode, customIntervalSeconds, fromUtc, dailyCloseHourEt);

        /// <summary>
        /// Converts a lookback expressed in trading days to scan-timeframe bars.
        /// Daily → 1 bar/day; 4h → 6 bars/day (24h/4h); 1h → 24; 30m → 48; 15m → 96.
        /// For session-limited markets (US equities) this overestimates slightly
        /// (more history, which is conservative for RSI footprint scanning).
        /// </summary>
        private int ScaleLookbackToScanBars(int lookbackDays)
        {
            if (ScanTimeFrame == ScannerTimeFrame.Daily) return lookbackDays;
            double hoursPerBar = ScanTimeFrame switch
            {
                ScannerTimeFrame.Hour4 => 4.0,
                ScannerTimeFrame.Hour => 1.0,
                ScannerTimeFrame.Minute30 => 0.5,
                ScannerTimeFrame.Minute15 => 0.25,
                _ => 24.0
            };
            return (int)Math.Ceiling(lookbackDays * 24.0 / hoursPerBar);
        }

        private Bars? EnsureBarsLoaded(TimeFrame timeFrame, string symbolName, int minBars)
        {
            try
            {
                var bars = MarketData.GetBars(timeFrame, symbolName);
                if (bars == null) return null;

                if (bars.Count < minBars)
                {
                    int guard = 0;
                    while (bars.Count < minBars && guard++ < 50)
                    {
                        int loaded = bars.LoadMoreHistory();
                        if (loaded <= 0) break;
                    }

                    if (bars.Count < minBars)
                    {
                        try { bars.LoadMoreHistoryAsync(); } catch { }
                    }

                    int minUsable = Math.Max(minBars / 2, 25);
                    if (bars.Count >= minUsable) return bars;
                    return null;
                }

                return bars;
            }
            catch
            {
                return null;
            }
        }

        private (bool Scanned, bool AlertFired) ScanSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName)) return (false, false);

            var res = ScanSymbolTimeframe(symbolName);
            bool alertFired = false;

            if (res.AlertFired && res.Setup != null)
            {
                if (_activeArmedSetups.TryGetValue(symbolName, out var existing))
                {
                    res.Setup.FirstDetected = existing.FirstDetected;
                }
                _activeArmedSetups[symbolName] = res.Setup;
                Notify(symbolName, res.BarTime, res.AlertKey, res.AlertMsg, res.Setup);
                alertFired = true;
            }
            else
            {
                // Target bar does not have a qualifying setup.
                // If this symbol was previously in active armed setups, remove it immediately to avoid stale setups.
                if (_activeArmedSetups.Remove(symbolName))
                {
                    Print($"[PpoReversalScanner] {symbolName} no longer has an active setup on the target bar. Removed from active candidates.");
                }
            }

            return (res.Scanned, alertFired);
        }

        private (bool Scanned, bool AlertFired, string AlertKey, string AlertMsg, DateTime BarTime, ArmedPpoSetupInfo? Setup) ScanSymbolTimeframe(string symbolName)
        {
            // --- Momentum bars (scan timeframe: 4h) for PPO + RSI(14) ---
            int scanLookback = ScaleLookbackToScanBars(ReversalCatalystLookbackBars);
            int requiredScanBars = Math.Max(MinBarsToScan, PpoSlowPeriod + PpoSignalPeriod + scanLookback + 50);
            var bars = EnsureBarsLoaded(ResolvedScanTimeFrame, symbolName, requiredScanBars);
            if (bars == null || bars.Count < PpoSlowPeriod + PpoSignalPeriod + 10)
                return (false, false, "", "", DateTime.MinValue, null);

            int n = bars.Count;
            double livePrice = GetLivePrice(symbolName);
            if (double.IsNaN(livePrice) || livePrice <= 0)
            {
                livePrice = bars.ClosePrices.LastValue;
                if (double.IsNaN(livePrice) || livePrice <= 0)
                    livePrice = bars.ClosePrices[n - 1];
            }

            // --- Scan-timeframe (4h) arrays: momentum ---
            double[] closes = new double[n];
            double[] highs = new double[n];
            double[] lows = new double[n];

            for (int i = 0; i < n; i++)
            {
                closes[i] = bars.ClosePrices[i];
                highs[i] = bars.HighPrices[i];
                lows[i] = bars.LowPrices[i];
            }

            // --- Daily bars: filters (SMA200, EMA50, ATR14, CLV, disaster gap, RS delta) ---
            int requiredDailyBars = Math.Max(Sma200Period + 20, 250);
            var dailyBars = EnsureBarsLoaded(TimeFrame.Daily, symbolName, requiredDailyBars);

            double[] dailyCloses = Array.Empty<double>();
            double[] dailyHighs = Array.Empty<double>();
            double[] dailyLows = Array.Empty<double>();
            double[] dailyOpens = Array.Empty<double>();
            double[] dailyAtrArr = Array.Empty<double>();
            double[] daily200SmaArr = Array.Empty<double>();
            double[] daily50EmaArr = Array.Empty<double>();
            int dailyN = 0;
            int dailyLastClosed = -1;

            if (dailyBars != null && dailyBars.Count >= 20)
            {
                dailyN = dailyBars.Count;
                dailyCloses = new double[dailyN];
                dailyHighs = new double[dailyN];
                dailyLows = new double[dailyN];
                dailyOpens = new double[dailyN];
                for (int i = 0; i < dailyN; i++)
                {
                    dailyCloses[i] = dailyBars.ClosePrices[i];
                    dailyHighs[i] = dailyBars.HighPrices[i];
                    dailyLows[i] = dailyBars.LowPrices[i];
                    dailyOpens[i] = dailyBars.OpenPrices[i];
                }

                dailyAtrArr = PlaybookTradeManagerEngine.ComputeAtr(dailyHighs, dailyLows, dailyCloses, 14);
                daily200SmaArr = PlaybookEngine.ComputeSma(dailyCloses, Sma200Period);
                daily50EmaArr = PlaybookEngine.ComputeEma(dailyCloses, Ema50Period);

                dailyLastClosed = PpoEngine.GetLastCompletedDailyBarIndex(
                    dailyBars.OpenTimes.LastValue, dailyN, Server.TimeInUtc);
            }

            // Daily filter scalars (from last completed daily bar)
            double dailyAtr14 = (dailyLastClosed >= 0 && dailyLastClosed < dailyAtrArr.Length) ? dailyAtrArr[dailyLastClosed] : double.NaN;
            double daily200Sma = (dailyLastClosed >= 0 && dailyLastClosed < daily200SmaArr.Length) ? daily200SmaArr[dailyLastClosed] : double.NaN;
            double daily50Ema = (dailyLastClosed >= 1 && dailyLastClosed < daily50EmaArr.Length) ? daily50EmaArr[dailyLastClosed - 1] : double.NaN;
            double dailyBar1Close = (dailyLastClosed >= 1) ? dailyCloses[dailyLastClosed - 1] : double.NaN;
            double dailyCurrentClose = (dailyLastClosed >= 0) ? dailyCloses[dailyLastClosed] : double.NaN;
            double dailyHigh = (dailyLastClosed >= 0) ? dailyHighs[dailyLastClosed] : double.NaN;
            double dailyLow = (dailyLastClosed >= 0) ? dailyLows[dailyLastClosed] : double.NaN;
            double dailyOpen = (dailyLastClosed >= 0) ? dailyOpens[dailyLastClosed] : double.NaN;
            double dailyPrevClose = (dailyLastClosed >= 1) ? dailyCloses[dailyLastClosed - 1] : double.NaN;

            // Compute 20-day Relative Strength Outperformance Delta vs Benchmark (SPY) over daily closes
            double rsDelta20 = double.NaN;
            if (_cachedBenchDailyCloses != null && _cachedBenchDailyCloses.Length >= 22 && dailyLastClosed >= 20)
            {
                double stockClose1 = dailyCloses[dailyLastClosed - 1];
                double stockClose20 = dailyCloses[dailyLastClosed - 20];

                int benchOffset = 1; // last closed daily bar (not forming)
                int benchIdx1 = _cachedBenchDailyCloses.Length - 2 - (benchOffset - 1);
                int benchIdx20 = _cachedBenchDailyCloses.Length - 21 - (benchOffset - 1);

                if (benchIdx1 >= 0 && benchIdx20 >= 0 && benchIdx1 < _cachedBenchDailyCloses.Length && benchIdx20 < _cachedBenchDailyCloses.Length)
                {
                    double benchClose1 = _cachedBenchDailyCloses[benchIdx1];
                    double benchClose20 = _cachedBenchDailyCloses[benchIdx20];
                    rsDelta20 = PpoEngine.ComputeRelativeStrengthDelta20(stockClose1, stockClose20, benchClose1, benchClose20);
                }
            }

            // Compute Indicators on scan timeframe (4h) — momentum only
            double[] rsi14Arr = PlaybookTradeManagerEngine.ComputeRsiSeries(closes, DailyRsiPeriod);
            var ppoResult = PpoEngine.ComputePpo(closes, PpoFastPeriod, PpoSlowPeriod, PpoSignalPeriod, OscMaType, SigMaType);

            // Local evaluator function for an individual bar index (evalIdx)
            ArmedPpoSetupInfo? TryEvaluateIndex(int evalIdx, bool isLiveBar, out string outAlertKey, out string outAlertMsg, out DateTime outBarTime)
            {
                outAlertKey = "";
                outAlertMsg = "";
                outBarTime = DateTime.MinValue;

                int prevIdx = evalIdx - 1;
                if (evalIdx < PpoSlowPeriod + PpoSignalPeriod || prevIdx < 0)
                    return null;

                DateTime barTime = bars.OpenTimes[evalIdx];

                // 4h signal-bar values (momentum + entry reference)
                double todayClose = closes[evalIdx];
                double todayHigh = highs[evalIdx];
                double todayLow = lows[evalIdx];
                double prevClose = closes[prevIdx];

                // 4h momentum indicators
                double rsi14 = rsi14Arr[evalIdx];
                double prevRsi14 = rsi14Arr[prevIdx];
                double ppoLine = ppoResult.PpoLine[evalIdx];
                double signalLine = ppoResult.SignalLine[evalIdx];

                // Daily filter values (from outer scope — last completed daily bar)
                // dailyAtr14, daily200Sma, daily50Ema, dailyBar1Close, dailyCurrentClose,
                // dailyHigh, dailyLow, dailyOpen, dailyPrevClose, rsDelta20

                if (double.IsNaN(dailyAtr14) || dailyAtr14 <= 0 ||
                    double.IsNaN(rsi14) || double.IsNaN(prevRsi14) ||
                    double.IsNaN(ppoLine) || double.IsNaN(signalLine) ||
                    (Require200SmaFilter && (double.IsNaN(daily200Sma) || daily200Sma <= 0)) ||
                    (RequireAsymmetric50EmaFilter && (double.IsNaN(daily50Ema) || daily50Ema <= 0)))
                {
                    return null;
                }

                // GATE 0: Hard VIX-Stop (< 25): If VIX >= 25, block ALL trades for US equities only.
                // FX, metals, commodities and crypto are exempt from the VIX gate.
                if (RequireVixFilter && IsUsEquitySymbol(symbolName) && !_vixHaltResult.Passes)
                {
                    RecordReject(_vixHaltResult.Detail);
                    return null;
                }


                if (RequireRelativeVolatilityFloor && !IsForexSymbol(symbolName))
                {
                    var relVolCheck = PpoEngine.EvaluateRelativeVolatilityFloor(dailyAtr14, dailyCurrentClose, MinRelativeAtrPct);
                    if (!relVolCheck.Passes)
                    {
                        RecordReject(relVolCheck.Reason);
                        return null;
                    }
                }

                // =====================================================================
                // EVALUATE LONG SETUP
                // =====================================================================
                bool checkLong = AllowedDirection != ScanAllowedDirection.ShortOnly;
                PpoReversalSetupResult? longResult = null;
                if (checkLong)
                {
                    if (RequireBenchmarkWind && IsUsEquitySymbol(symbolName) && !_benchmarkWindLongResult.Passes)
                    {
                        RecordReject(_benchmarkWindLongResult.Detail);
                    }
                    else
                    {
                        var setupRes = PpoEngine.EvaluatePpoReversalSetupRsi14(
                            rsi14Arr,
                            ppoResult.PpoLine,
                            ppoResult.SignalLine,
                            evalIdx,
                            bar1Close: dailyBar1Close,
                            bar150Ema: daily50Ema,
                            dailyAtr: dailyAtr14,
                            currentClose: dailyCurrentClose,
                            daily200Sma: daily200Sma,
                            require200Sma: Require200SmaFilter,
                            isShort: false,
                            bar0High: dailyHigh,
                            bar0Low: dailyLow,
                            requireClv: RequireClvFilter,
                            minClvLong: MinClvLong,
                            maxClvShort: MaxClvShort,
                            reversalCatalystLookbackBars: scanLookback,
                            longReversalRsiMax: LongReversalRsiMax,
                            shortReversalRsiMin: ShortReversalRsiMin,
                            longContinuationRsiMin: LongContinuationRsiMin,
                            shortContinuationRsiMax: ShortContinuationRsiMax,
                            rsDelta20: rsDelta20,
                            require50Ema: RequireAsymmetric50EmaFilter,
                            sma200BufferAtr: Sma200BufferAtr);

                        if (!setupRes.IsTriggered)
                        {
                            RecordReject(setupRes.RejectReason);
                        }
                        else
                        {
                            var gapCheck = PlaybookEngine.EvaluateDisasterGap(dailyPrevClose, dailyOpen, dailyAtr14, MaxDisasterGapAtr, isShort: false);
                            if (!gapCheck.Passes)
                            {
                                RecordReject(gapCheck.Reason);
                            }
                            else if (RequireMaxBarExpansionFilter && !PlaybookEngine.EvaluateMaxBarExpansion(dailyHigh, dailyLow, dailyAtr14, MaxBarExpansionAtr, isShort: false).Passes)
                            {
                                var expCheck = PlaybookEngine.EvaluateMaxBarExpansion(dailyHigh, dailyLow, dailyAtr14, MaxBarExpansionAtr, isShort: false);
                                RecordReject(expCheck.Reason);
                            }
                            else
                            {
                                longResult = setupRes;
                            }
                        }
                    }
                }

                // =====================================================================
                // EVALUATE SHORT SETUP (Shorting permitted in all macro regimes!)
                // =====================================================================
                bool checkShort = AllowedDirection != ScanAllowedDirection.LongOnly;
                PpoReversalSetupResult? shortResult = null;
                if (checkShort && longResult == null)
                {
                    var setupRes = PpoEngine.EvaluatePpoReversalSetupRsi14(
                        rsi14Arr,
                        ppoResult.PpoLine,
                        ppoResult.SignalLine,
                        evalIdx,
                        bar1Close: dailyBar1Close,
                        bar150Ema: daily50Ema,
                        dailyAtr: dailyAtr14,
                        currentClose: dailyCurrentClose,
                        daily200Sma: daily200Sma,
                        require200Sma: Require200SmaFilter,
                        isShort: true,
                        bar0High: dailyHigh,
                        bar0Low: dailyLow,
                        requireClv: RequireClvFilter,
                        minClvLong: MinClvLong,
                        maxClvShort: MaxClvShort,
                        reversalCatalystLookbackBars: scanLookback,
                        longReversalRsiMax: LongReversalRsiMax,
                        shortReversalRsiMin: ShortReversalRsiMin,
                        longContinuationRsiMin: LongContinuationRsiMin,
                        shortContinuationRsiMax: ShortContinuationRsiMax,
                        rsDelta20: rsDelta20,
                        require50Ema: RequireAsymmetric50EmaFilter,
                        sma200BufferAtr: Sma200BufferAtr);

                    if (!setupRes.IsTriggered)
                    {
                        RecordReject(setupRes.RejectReason);
                    }
                    else
                    {
                        var gapCheck = PlaybookEngine.EvaluateDisasterGap(dailyPrevClose, dailyOpen, dailyAtr14, MaxDisasterGapAtr, isShort: true);
                        if (!gapCheck.Passes)
                        {
                            RecordReject(gapCheck.Reason);
                        }
                        else if (RequireMaxBarExpansionFilter && !PlaybookEngine.EvaluateMaxBarExpansion(dailyHigh, dailyLow, dailyAtr14, MaxBarExpansionAtr, isShort: true).Passes)
                        {
                            var expCheck = PlaybookEngine.EvaluateMaxBarExpansion(dailyHigh, dailyLow, dailyAtr14, MaxBarExpansionAtr, isShort: true);
                            RecordReject(expCheck.Reason);
                        }
                        else
                        {
                            shortResult = setupRes;
                        }
                    }
                }

                if (longResult == null && shortResult == null)
                    return null;

                bool isShort = shortResult != null;
                var activeRes = isShort ? shortResult!.Value : longResult!.Value;
                var direction = isShort ? PlaybookTradeManagerEngine.TradeDirection.Sell : PlaybookTradeManagerEngine.TradeDirection.Buy;
                string dirLabel = isShort ? "SELL" : "BUY";
                string timingTag = isLiveBar ? "[LIVE BAR]" : "[CLOSED BAR]";
                string alertType = isShort ? "PPO_SELL_ON_CLOSE" : "PPO_BUY_ON_CLOSE";

                // Compute 100% Single Market Entry Bracket (Hard SL 1.0 ATR, TP 2.50 ATR)
                var (singleSl, singleTp) = PlaybookTradeManagerEngine.CalculateSingleEntryBracket(
                    direction,
                    todayClose,
                    dailyAtr14,
                    slAtrMultiple: SlAtrMultiple,
                    tpAtrMultiple: PtAtrMultiple);

                // Compute Close Location Value (CLV) — daily bar
                double clv = activeRes.Clv;
                if (double.IsNaN(clv))
                {
                    var clvCheck = PpoEngine.EvaluateClv(!isShort, dailyCurrentClose, dailyHigh, dailyLow, MinClvLong, MaxClvShort);
                    clv = clvCheck.Clv;
                }

                // Compute 68.18% Retracement Limit Price (4h signal candle, direction-dependent)
                double eodRetraceLimit = PpoEngine.ComputeRetracementLimit(todayHigh, todayLow, isShort, pct: 0.6818);

                // =====================================================================
                // EVALUATE RSI DIVERGENCE (Exact RsiOscillator Engine)
                // =====================================================================
                string rsiDivSummary = "None";
                string rsiDivShortTag = "";
                try
                {
                    var confirmedDivs = RsiEngine.FindDivergences(
                        highs: highs,
                        lows: lows,
                        closes: closes,
                        rsiValues: rsi14Arr,
                        leftStrength: 4,
                        rightStrength: 2,
                        priceSearchRadius: 2,
                        maxDivBars: scanLookback,
                        minSpan: 3,
                        minOscDelta: 0.5,
                        priceTolerancePct: 0.001,
                        osThreshold: 35.0,
                        obThreshold: 65.0,
                        zoneFilter: ZoneFilterMode.None,
                        adjacentOnly: false,
                        scanDepth: 5,
                        includeHidden: true,
                        includeClassB: true,
                        checkObstacles: true,
                        maxIndex: evalIdx,
                        requirePricePivot: false,
                        pricePivotSource: PricePivotSource.Close);

                    var formingDiv = RsiEngine.FindFormingDivergence(
                        isBullish: !isShort,
                        highs: highs,
                        lows: lows,
                        closes: closes,
                        rsiValues: rsi14Arr,
                        checkIdx: evalIdx,
                        leftStrength: 4,
                        rightStrength: 2,
                        priceSearchRadius: 2,
                        maxDivBars: scanLookback,
                        minSpan: 3,
                        minOscDelta: 0.5,
                        priceTolerancePct: 0.001,
                        osThreshold: 35.0,
                        obThreshold: 65.0,
                        zoneFilter: ZoneFilterMode.None,
                        includeHidden: true,
                        includeClassB: true,
                        checkObstacles: true,
                        requirePricePivot: false,
                        pricePivotSource: PricePivotSource.Close);

                    RsiDivergence? matchingConfirmed = null;
                    if (confirmedDivs != null && confirmedDivs.Count > 0)
                    {
                        // Take the most recent confirmed divergence within the catalyst lookback.
                        // Divergences are returned chronologically (oldest first), so iterate from the end.
                        // The divergence does NOT need to fall on the PPO cross bar exactly.
                        for (int d = confirmedDivs.Count - 1; d >= 0; d--)
                        {
                            var cd = confirmedDivs[d];
                            bool isDivBullish = (cd.Type == DivergenceType.RegularBullish || cd.Type == DivergenceType.HiddenBullish);
                            if (isDivBullish == !isShort && (evalIdx - cd.ConfirmBar <= scanLookback))
                            {
                                matchingConfirmed = cd;
                                break;
                            }
                        }
                    }

                    if (formingDiv.Type != DivergenceType.None)
                    {
                        string classTag = formingDiv.Class == DivergenceClass.ClassB ? "Class B" : (formingDiv.Class == DivergenceClass.Hidden ? "Hidden" : "Class A");
                        rsiDivSummary = $"[FORMING] {formingDiv.Type} ({classTag}, RSI {formingDiv.Rsi1:F1} -> {formingDiv.Rsi2:F1})";
                        rsiDivShortTag = $"[FORMING {formingDiv.Type}]";
                    }
                    else if (matchingConfirmed.HasValue)
                    {
                        var cd = matchingConfirmed.Value;
                        int barsAgo = evalIdx - cd.ConfirmBar;
                        string classTag = cd.Class == DivergenceClass.ClassB ? "Class B" : (cd.Class == DivergenceClass.Hidden ? "Hidden" : "Class A");
                        rsiDivSummary = $"[CONFIRMED] {cd.Type} ({classTag}, RSI {cd.Rsi1:F1} -> {cd.Rsi2:F1}, confirmed {barsAgo}b ago)";
                        rsiDivShortTag = $"[{cd.Type} - {barsAgo}b ago]";
                    }
                }
                catch
                {
                    rsiDivSummary = "None";
                    rsiDivShortTag = "";
                }

                double disasterGapAtr = isShort ? ((dailyOpen - dailyPrevClose) / dailyAtr14) : ((dailyPrevClose - dailyOpen) / dailyAtr14);

                string ppoDetail = $"PPO Line: {activeRes.PpoLine:F2}% | Signal: {activeRes.SignalLine:F2}% | Hist: {activeRes.Histogram:F2}% [{activeRes.SetupDescription}]";
                string tfUnit = ScanTimeFrame == ScannerTimeFrame.Daily ? "d" : "b";
                string tfLabel = ScanTimeFrame == ScannerTimeFrame.Daily ? "Daily" : $"{ScanTimeFrame}";
                string footprintDesc = activeRes.SetupType switch
                {
                    PpoSetupType.LongTerminalShakeout =>
                        $"{scanLookback}{tfUnit} Min RSI: {activeRes.FootprintExtremeRsi14:F1} <= {LongReversalRsiMax:F1} (Capitulation Dip)",
                    PpoSetupType.LongBullFlagExpansion =>
                        $"{scanLookback}{tfUnit} Min RSI: {activeRes.FootprintExtremeRsi14:F1} <= {LongContinuationRsiMin:F1} (Bull Flag Pullback)",
                    PpoSetupType.ShortUpthrust =>
                        $"{scanLookback}{tfUnit} Max RSI: {activeRes.FootprintExtremeRsi14:F1} >= {ShortReversalRsiMin:F1} (Blow-Off Climax)",
                    PpoSetupType.ShortBearFlagBreakdown =>
                        $"{scanLookback}{tfUnit} Max RSI: {activeRes.FootprintExtremeRsi14:F1} >= {ShortContinuationRsiMax:F1} (Bear Flag Relief Bounce)",
                    _ => $"{scanLookback}{tfUnit} Extreme RSI: {activeRes.FootprintExtremeRsi14:F1}"
                };
                string rsiDetail = $"{tfLabel} RSI({DailyRsiPeriod})[1]: {prevRsi14:F1} | Footprint: {footprintDesc}";

                double barRangeAtr = (dailyHigh - dailyLow) / dailyAtr14;
                string expDetail = RequireMaxBarExpansionFilter
                    ? $" | Bar Expansion: {barRangeAtr:F2} ATR (Daily High-Low <= {MaxBarExpansionAtr:F2} ATR)"
                    : "";
                string clvLabel = isLiveBar ? "LKV" : "CLV";
                string clvDetail = $"{clvLabel}: {clv:F2} ({(isShort ? $"<= {MaxClvShort:F2} Bottom 35%" : $">= {MinClvLong:F2} Top 35%")})";
                string riskDetail = $"No Disaster Gap ({disasterGapAtr:F2} ATR <= {MaxDisasterGapAtr:F1} ATR){expDetail} | {clvDetail}";

                string rsDeltaStr = !double.IsNaN(rsDelta20) ? $"{(rsDelta20 >= 0 ? "+" : "")}{rsDelta20:F1}%" : "N/A";
                string clvStr = !double.IsNaN(clv) ? $"{clv:F2}" : "N/A";

                string dateFmt = ScanTimeFrame == ScannerTimeFrame.Daily ? "yyyyMMdd" : "yyyyMMdd_HHmm";
                outAlertKey = $"{symbolName}_{alertType}_{barTime.ToString(dateFmt)}";
                outAlertMsg =
                    $"[{symbolName}] {alertType} {timingTag} [{activeRes.SetupDescription}]\n" +
                    $"4h Close: {todayClose:F2} | Daily ATR: {dailyAtr14:F2} | Daily Range: {barRangeAtr:F2} ATR\n" +
                    $"--> 68.18% Retrace Limit (4h): ${eodRetraceLimit:F2} | SL: ${singleSl:F2} | TP: ${singleTp:F2}\n" +
                    $"--> PPO (4h): {ppoDetail}\n" +
                    $"--> RSI Catalyst (4h): {rsiDetail}\n" +
                    (!string.IsNullOrEmpty(rsiDivShortTag) ? $"--> RSI Divergence (4h): {rsiDivSummary}\n" : "") +
                    $"--> Risk Filters (Daily): {riskDetail}\n" +
                    $"--> Tie-Breaker Metrics: CLV: {clvStr} (Passed Gate {(isShort ? $"<= {MaxClvShort:F2}" : $">= {MinClvLong:F2}")}) | RS vs SPX (20d): {rsDeltaStr} (Informative RS ranking — no hard exclusion)";

                outBarTime = barTime;

                return new ArmedPpoSetupInfo
                {
                    Symbol = symbolName,
                    Direction = dirLabel,
                    SetupType = activeRes.SetupType,
                    SetupDescription = activeRes.SetupDescription,
                    TimingTag = timingTag,
                    PpoLine = activeRes.PpoLine,
                    SignalLine = activeRes.SignalLine,
                    Histogram = activeRes.Histogram,
                    CurrentRsi14 = activeRes.CurrentRsi14,
                    Bar1Rsi14 = prevRsi14,
                    Clv = clv,
                    RsDelta20 = rsDelta20,
                    RetracementLimitPrice = eodRetraceLimit,
                    FootprintExtremeRsi14 = activeRes.FootprintExtremeRsi14,
                    BarsSinceExtreme = activeRes.BarsSinceExtreme,
                    DailyAtr = dailyAtr14,
                    Sma200 = daily200Sma,
                    Daily50Ema = daily50Ema,
                    LivePrice = livePrice,
                    EntryPrice = todayClose,
                    StopLossPrice = singleSl,
                    TakeProfitPrice = singleTp,
                    RsiDivergenceStatus = rsiDivSummary,
                    RsiDivergenceShortTag = rsiDivShortTag,
                    SignalBarTime = barTime,
                    FirstDetected = Server.TimeInUtc,
                    LastSeenTime = Server.TimeInUtc
                };
            }

            ArmedPpoSetupInfo? setup = null;
            string alertKey = "";
            string alertMsg = "";
            DateTime barTime = DateTime.MinValue;

            // Multi-asset target bar resolution (Crypto 24/7, FX, Metals, Commodities, US Equities):
            // - When market is actively trading (isMarketOpen): bar n - 1 is live/forming -> target completed bar is n - 2.
            // - When market is closed (break/weekend/after-hours): bar n - 1 is the completed session bar -> target is n - 1.
            int targetIdx;
            if (ScanTimeFrame == ScannerTimeFrame.Daily && IsUsEquitySymbol(symbolName))
            {
                targetIdx = PpoEngine.GetLastCompletedDailyBarIndex(bars.OpenTimes[n - 1], n, Server.TimeInUtc);
            }
            else
            {
                var scannedSym = Symbols.GetSymbol(symbolName);
                bool isMarketOpen = scannedSym != null && scannedSym.MarketHours.IsOpened();
                targetIdx = isMarketOpen ? (n - 2) : (n - 1);

                // Protect against unformed future bar or flat phantom rollover bar when market is closed:
                if (targetIdx == n - 1 && n >= 2)
                {
                    if (bars.OpenTimes[n - 1] > Server.TimeInUtc ||
                        (bars.TickVolumes[n - 1] == 0 &&
                         bars.OpenPrices[n - 1] == bars.ClosePrices[n - 1] &&
                         bars.HighPrices[n - 1] == bars.LowPrices[n - 1]))
                    {
                        targetIdx = n - 2;
                    }
                }
            }

            if (targetIdx >= 0 && targetIdx < n)
            {
                setup = TryEvaluateIndex(targetIdx, isLiveBar: false, out alertKey, out alertMsg, out barTime);
            }

            if (setup != null)
            {
                // Setup age filter: reject setups older than MaxSetupAgeTradingDays trading days
                if (MaxSetupAgeTradingDays >= 0 && barTime != DateTime.MinValue)
                {
                    var openTimesList = new List<DateTime>(n);
                    for (int i = 0; i < n; i++) openTimesList.Add(bars.OpenTimes[i]);
                    int ageDays = PlaybookTradeManagerEngine.CalculateTradingDaysElapsed(barTime, Server.TimeInUtc, openTimesList);
                    if (ageDays > MaxSetupAgeTradingDays)
                    {
                        return (true, false, "", "", barTime, null);
                    }
                }

                return (true, true, alertKey, alertMsg, barTime, setup);
            }

            DateTime fallbackBarTime = bars.OpenTimes[n - 1];
            return (true, false, "", "", fallbackBarTime, null);
        }

        private void RecordReject(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;

            string key = reason;
 if (reason.StartsWith("Low Relative Volatility", StringComparison.OrdinalIgnoreCase))
            {
                key = $"Low Relative Volatility (< {MinRelativeAtrPct:F1}% ATR of price)";
            }
            else if (reason.StartsWith("No Bull Cross", StringComparison.OrdinalIgnoreCase))
            {
                key = "No Bull Cross (Histogram / Momentum timing)";
            }
            else if (reason.StartsWith("No Bear Cross", StringComparison.OrdinalIgnoreCase))
            {
                key = "No Bear Cross (Histogram / Momentum timing)";
            }
            else if (reason.StartsWith("Macro Drag", StringComparison.OrdinalIgnoreCase))
            {
                key = "Macro Drag (Bear Market Benchmark: SPY < 50 SMA) [Longs Blocked]";
            }

            _currentPassRejects.TryGetValue(key, out int cnt);
            _currentPassRejects[key] = cnt + 1;
        }

        private double GetLivePrice(string symbolName)
        {
            try
            {
                var sym = Symbols.GetSymbol(symbolName);
                if (sym != null && sym.Bid > 0 && sym.Ask > 0)
                    return (sym.Bid + sym.Ask) / 2.0;
            }
            catch { }

            try
            {
                var dBars = MarketData.GetBars(ResolvedScanTimeFrame, symbolName);
                if (dBars != null && dBars.Count > 0)
                    return dBars.ClosePrices.LastValue;
            }
            catch { }

            return double.NaN;
        }

        private void Notify(string symbolName, DateTime barTime, string alertType, string detail, ArmedPpoSetupInfo? setup = null)
        {
            DateTime now = Server.TimeInUtc;
            _totalAlertsFired++;

            string rsTag = (setup != null && !double.IsNaN(setup.RsDelta20))
                ? $"RS: {(setup.RsDelta20 >= 0 ? "+" : "")}{setup.RsDelta20:F1}%"
                : "";
            string clvTag = (setup != null && !double.IsNaN(setup.Clv))
                ? $"CLV: {setup.Clv:F2}"
                : "";
            string tieBreakerTag = "";
            if (!string.IsNullOrEmpty(rsTag) && !string.IsNullOrEmpty(clvTag))
                tieBreakerTag = $" ({rsTag} | {clvTag})";
            else if (!string.IsNullOrEmpty(rsTag))
                tieBreakerTag = $" ({rsTag})";
            else if (!string.IsNullOrEmpty(clvTag))
                tieBreakerTag = $" ({clvTag})";

            string dateTag = barTime != DateTime.MinValue ? barTime.ToString("yyyyMMdd") : now.ToString("yyyyMMdd");
            string displayType = alertType.Replace($"{symbolName}_", "").Replace($"_{dateTag}", "");
            _recentAlertMessages.Insert(0, $"[{now:HH:mm}] {symbolName} {displayType}{tieBreakerTag}");
            if (_recentAlertMessages.Count > 5) _recentAlertMessages.RemoveAt(_recentAlertMessages.Count - 1);

            if (AlertSound)
                Notifications.PlaySound(SoundType.Announcement);

            if (AlertPopup)
                Notifications.ShowPopup("PPO Reversal Setup", detail, PopupNotificationState.Information);

            Print($"[PpoReversalScanner Alert]\n{detail}");
        }

        private string GetProgressBar(int current, int total, int barLength = 10)
        {
            if (total <= 0) return "[..........] 0%";
            double pct = Math.Clamp((double)current / total, 0.0, 1.0);
            int filled = (int)Math.Round(pct * barLength);
            int empty = Math.Max(0, barLength - filled);
            return $"[{new string('#', filled)}{new string('.', empty)}] {pct * 100:F0}%";
        }

        private void DrawHud(int scannedCount, int totalCount, int currentAlerts, string statusOverride = "")
        {
            string defaultStatus;
            if (_nextScanTime > DateTime.MinValue)
            {
                var rem = _nextScanTime - Server.TimeInUtc;
                int m = Math.Max(0, (int)rem.TotalMinutes);
                int s = Math.Max(0, rem.Seconds);
                string modeDesc = ScheduleMode switch
                {
                    ScanScheduleMode.Hourly => "Hourly (:00:02 UTC)",
                    ScanScheduleMode.DailyAfterClose => $"Daily ({DailyCloseHourEt}:00 ET Close)",
                    ScanScheduleMode.Every15Minutes => "Quarter-Hour (:15/:30/:45:02)",
                    ScanScheduleMode.CustomInterval => $"Interval ({ScanIntervalSeconds}s)",
                    ScanScheduleMode.ManualOnly => "Manual Only",
                    _ => "Next scan"
                };
                defaultStatus = $"Idle ({modeDesc} at {_nextScanTime:yyyy-MM-dd HH:mm:ss} UTC — in {m}m {s:D2}s)";
            }
            else
            {
                defaultStatus = "Idle (Next scan scheduled)";
            }

            string statusText = !string.IsNullOrEmpty(statusOverride) ? statusOverride : defaultStatus;

            var sb = new StringBuilder();
            sb.AppendLine("=== PPO REVERSAL & CONTINUATION SCANNER (Daily Close Architecture) ===");
            sb.AppendLine($"Watchlist: {WatchlistName} ({totalCount} symbols) | Trigger: {ScheduleMode} | Direction: {AllowedDirection}");
            sb.AppendLine($"PPO ({ScanTimeFrame}): ({PpoFastPeriod}, {PpoSlowPeriod}, {PpoSignalPeriod}) Cross: Unconstrained | RSI({DailyRsiPeriod}) Footprint: {ScaleLookbackToScanBars(ReversalCatalystLookbackBars)}b = {ReversalCatalystLookbackBars}d");

            string spyWindDesc;
            if (!RequireBenchmarkWind)
            {
                spyWindDesc = "BYPASSED (Filter Disabled)";
            }
            else
            {
                string liveSpyStr = !double.IsNaN(_benchmarkWindLongResult.LiveSpy) ? $"${_benchmarkWindLongResult.LiveSpy:F2}" : "N/A";
                string spy50SmaStr = !double.IsNaN(_benchmarkWindLongResult.Sma50) ? $"${_benchmarkWindLongResult.Sma50:F2}" : "N/A";
                string longStatus = _benchmarkWindLongResult.Passes
                    ? $"PASS ({liveSpyStr} >= 50 SMA {spy50SmaStr})"
                    : $"BLOCKED ({liveSpyStr} < 50 SMA {spy50SmaStr})";
                spyWindDesc = $"Long: {longStatus} | Short: ALWAYS PERMITTED";
            }

            string vixStatusDesc;
            if (!RequireVixFilter)
            {
                vixStatusDesc = "BYPASSED (Filter Disabled)";
            }
            else
            {
                string liveVixStr = !double.IsNaN(_vixHaltResult.LiveVix) ? _vixHaltResult.LiveVix.ToString("F2") : "N/A";
                vixStatusDesc = _vixHaltResult.Passes
                    ? $"PASS (Live {liveVixStr} < {MaxVixThreshold:F1})"
                    : $"🛑 BLOCKED (Live {liveVixStr} >= {MaxVixThreshold:F1})";
            }

            string clvHudStr = RequireClvFilter ? $" | CLV/LKV (L>={MinClvLong:F2}/S<={MaxClvShort:F2})" : "";
            string volFloorHudStr = $" | Vol Floor (>={MinRelativeAtrPct:F1}%)";
            sb.AppendLine($"Guardrails (Daily): 200 SMA {(Require200SmaFilter ? "STRICT (Long > 200 SMA, Short < 200 SMA)" : "OFF")} | {ScaleLookbackToScanBars(ReversalCatalystLookbackBars)}b={ReversalCatalystLookbackBars}d Footprint Recency ON{clvHudStr}{volFloorHudStr} | Max Bar Exp (<= {MaxBarExpansionAtr:F2} ATR)");
            sb.AppendLine($"Status: {statusText}");
            sb.AppendLine($"Pass #{_scanPassCount} | Scanned: {scannedCount}/{totalCount} | Active Setups: {_activeArmedSetups.Count} | Total Alerts: {_totalAlertsFired}");
            sb.AppendLine($"Last Scan: {(_lastScanTime == DateTime.MinValue ? "Pending..." : _lastScanTime.ToString("HH:mm:ss") + " UTC")}");

            if (_activeArmedSetups.Count > 0)
            {
                sb.AppendLine("\n--- ACTIVE PPO SETUPS (100% Single Market Entry | Ranked by 14:15 Tie-Breaker) ---");
                var sortedSetups = _activeArmedSetups.Values.ToList();
                sortedSetups.Sort((a, b) => PpoEngine.CompareSetupsTieBreaker(
                    a.SetupType, a.Clv, a.RsDelta20, a.FootprintExtremeRsi14,
                    b.SetupType, b.Clv, b.RsDelta20, b.FootprintExtremeRsi14));

                foreach (var item in sortedSetups)
                {
                    string typeTag = item.SetupType switch
                    {
                        PpoSetupType.ShortUpthrust => "UPTHRUST",
                        PpoSetupType.ShortBearFlagBreakdown => "BEARFLAG",
                        PpoSetupType.LongTerminalShakeout => "SHAKEOUT",
                        PpoSetupType.LongBullFlagExpansion => "BULLFLAG",
                        _ => "EXPANSION"
                    };

                    double diff = item.Direction == "BUY" ? (item.LivePrice - item.EntryPrice) : (item.EntryPrice - item.LivePrice);
                    double atrPnl = item.DailyAtr > 0 ? (diff / item.DailyAtr) : 0.0;
                    string pnlSign = atrPnl >= 0 ? "+" : "";
                    string pnlStr = $"{pnlSign}{atrPnl:F2} ATR";
                    string slSign = item.Direction == "BUY" ? "-" : "+";
                    string tpSign = item.Direction == "BUY" ? "+" : "-";

                    string clvDisp = !double.IsNaN(item.Clv) ? $" | {(item.TimingTag == "[LIVE BAR]" ? "LKV" : "CLV")}: {item.Clv:F2}" : "";
                    string retraceDisp = !double.IsNaN(item.RetracementLimitPrice) ? $" (Retrace Limit: ${item.RetracementLimitPrice:F2})" : "";
                    string divDisp = !string.IsNullOrEmpty(item.RsiDivergenceShortTag) ? $" | Div: {item.RsiDivergenceShortTag}" : "";
                    sb.AppendLine($"[{item.Direction}] [{typeTag}] {item.TimingTag} {item.Symbol,-6} | Live: ${item.LivePrice:F2} ({pnlStr}) | PPO: {item.PpoLine:F2}% | RSI10[1]: {item.Bar1Rsi14:F1}{clvDisp}{rsDisp}{divDisp} | Entry: ${item.EntryPrice:F2}{retraceDisp} | SL: ${item.StopLossPrice:F2} ({slSign}{SlAtrMultiple:F1} ATR) | TP: ${item.TakeProfitPrice:F2} ({tpSign}{PtAtrMultiple:F1} ATR)");
                }
            }
            else
            {
                sb.AppendLine("\n--- No Active Setups (All Watchlist Symbols in Idle/Consolidation) ---");
            }

            if (_recentAlertMessages.Count > 0)
            {
                sb.AppendLine("\n--- RECENT NOTIFICATIONS ---");
                sb.AppendLine(string.Join("\n", _recentAlertMessages));
            }

            Chart.DrawStaticText(HudTableName, sb.ToString(), VerticalAlignment.Top, HorizontalAlignment.Left, Color.CornflowerBlue);
        }
    }
}
