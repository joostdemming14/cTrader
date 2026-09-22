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
    /// Continuation Scanner for the cTrader 2.0 suite (Daily bars, EOD signals, entry next open).
    ///
    /// Final continuation logic:
    ///   Long Continuation: the latest completed bar touches Low &lt;= EMA21, then closes &gt; EMA21 with
    ///     EMA21 &gt; EMA50, CLV &gt;= +0.35, TSI &gt; 0 (momentum regime). SPY gate optional (default off).
    ///   Short Continuation: the latest completed bar touches High &gt;= EMA21, then closes &lt; EMA21 with
    ///     EMA21 &lt; EMA50, CLV &lt;= -0.35, TSI &lt; 0 (momentum regime). SPY gate optional (default off).
    ///
    /// The TSI zero line is the decisive momentum regime check; the TSI signal line is computed
    /// for display only and is not part of the trigger. RSI is intentionally not used.
    /// Continuations additionally require the SMA200 long-term trend filter (Close > SMA200 for
    /// longs, Close < SMA200 for shorts).
    ///
    /// All conditions are evaluated on the close of the last completed daily bar (no repaint).
    /// Alert-only scanner: reports the setup only (no SL/PT computed). No trades placed. AccessRights = None.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class ContinuationScanner : Robot
    {
        /// <summary>Default crypto symbol universe gated by the BTC benchmark filter.</summary>
        private const string DefaultCryptoSymbolsCsv = "BTCUSD,BTCGBP,BTCEUR,BTCAUD,ETHUSD,ETHGBP,ETHEUR,ETHAUD,LTCUSD,TRUMPUSD,AAVUSD,ATMUSD,FLOUSD,JUPUSD,NERUSD,ONDUSD,PEPUSD,SHBUSD,TRXUSD,WIFUSD,ARBUSD,BNKUSD,MANUSD,SANUSD,POLUSD,SONUSD,HBARUSD,SUIUSD,TONUSD,APTUSD,HYPEUSD,INJUSD,RENDERUSD,FETUSD,XAUTUSD,PAXGUSD,DOTUSD,LINKUSD,XLMUSD,XRPUSD,UNIUSD,DOGEUSD,ADAUSD,BCHUSD,BNBUSD,XTZUSD,SOLUSD,AVAXUSD,COMPUSD,ETCUSD,GLMRUSD,KSMUSD";
        // =========================================================================
        // --- 1. Scan Setup ---
        // =========================================================================
        [Parameter("Watchlist Name", Group = "1. Scan Setup", DefaultValue = "Screener")]
        public string WatchlistName { get; set; } = "Screener";

        [Parameter("Scan Direction", Group = "1. Scan Setup", DefaultValue = ReversalScanDirection.Both)]
        public ReversalScanDirection AllowedDirection { get; set; } = ReversalScanDirection.Both;

        [Parameter("Schedule Mode", Group = "1. Scan Setup", DefaultValue = ReversalScheduleMode.DailyAfterClose)]
        public ReversalScheduleMode ScheduleMode { get; set; } = ReversalScheduleMode.DailyAfterClose;

        [Parameter("Daily Close Hour (ET)", Group = "1. Scan Setup", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int DailyCloseHourEt { get; set; } = 16;

        [Parameter("Scan Immediately On Start", Group = "1. Scan Setup", DefaultValue = true)]
        public bool ScanOnStart { get; set; } = true;

        [Parameter("Custom Interval (sec)", Group = "1. Scan Setup", DefaultValue = 3600, MinValue = 60, Step = 60)]
        public int ScanIntervalSeconds { get; set; } = 3600;

        [Parameter("Symbols per Batch Tick", Group = "1. Scan Setup", DefaultValue = 20, MinValue = 5, MaxValue = 50)]
        public int BatchSize { get; set; } = 20;

        [Parameter("Min Bars to Scan", Group = "1. Scan Setup", DefaultValue = 250, MinValue = 100)]
        public int MinBarsToScan { get; set; } = 250;

        [Parameter("US Session Bar Logic (US equities only)", Group = "1. Scan Setup", DefaultValue = true)]
        public bool UseUsSessionBarLogic { get; set; } = true;

        // =========================================================================
        // --- 2. Indicator Parameters ---
        // =========================================================================
        [Parameter("EMA Period", Group = "2. Indicators", DefaultValue = 21, MinValue = 10)]
        public int EmaPeriod { get; set; } = 21;

        [Parameter("Trend EMA Period (alignment)", Group = "2. Indicators", DefaultValue = 50, MinValue = 10)]
        public int TrendEmaPeriod { get; set; } = 50;

        [Parameter("SMA200 Period (Long-Term Trend)", Group = "2. Indicators", DefaultValue = 200, MinValue = 20)]
        public int Sma200Period { get; set; } = 200;

        [Parameter("ATR Period", Group = "2. Indicators", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; } = 14;

        [Parameter("TSI Long EMA", Group = "2. Indicators", DefaultValue = 25, MinValue = 1)]
        public int TsiLongPeriod { get; set; } = 25;

        [Parameter("TSI Short EMA", Group = "2. Indicators", DefaultValue = 13, MinValue = 1)]
        public int TsiShortPeriod { get; set; } = 13;

        [Parameter("TSI Signal (EMA)", Group = "2. Indicators", DefaultValue = 13, MinValue = 1)]
        public int TsiSignalPeriod { get; set; } = 13;

        // =========================================================================
        // --- 3. Continuation Thresholds ---
        // =========================================================================
        [Parameter("CLV Threshold (abs)", Group = "3. Continuation Thresholds", DefaultValue = 0.35, MinValue = 0.0, MaxValue = 1.0, Step = 0.05)]
        public double ClvThreshold { get; set; } = 0.35;

        [Parameter("Divergence Lookback (bars)", Group = "3. Continuation Thresholds", DefaultValue = 20, MinValue = 5)]
        public int DivergenceLookback { get; set; } = 20;

        [Parameter("Divergence Min Gap (bars)", Group = "3. Continuation Thresholds", DefaultValue = 3, MinValue = 1)]
        public int DivergenceMinGap { get; set; } = 3;

        [Parameter("Divergence TSI Extreme Level (abs)", Group = "3. Continuation Thresholds", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 100.0, Step = 1.0)]
        public double DivergenceTsiExtremeLevel { get; set; } = 10.0;

        [Parameter("Divergence Min TSI Gap", Group = "3. Continuation Thresholds", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 100.0, Step = 0.01)]
        public double DivergenceMinTsiDrop { get; set; } = 0.1;

        [Parameter("Divergence Trigger Window (bars, 0 = off)", Group = "3. Continuation Thresholds", DefaultValue = 3, MinValue = 0, MaxValue = 50)]
        public int DivergenceTriggerWindow { get; set; } = 3;

        // =========================================================================
        // --- 4. Benchmark (SPY) Filter ---
        // =========================================================================
        [Parameter("Require Benchmark Filter (SPY vs SMA50)", Group = "4. Benchmark Filter", DefaultValue = false)]
        public bool RequireBenchmarkFilter { get; set; } = false;

        [Parameter("Benchmark Symbol", Group = "4. Benchmark Filter", DefaultValue = "SPY.US")]
        public string BenchmarkSymbol { get; set; } = "SPY.US";

        [Parameter("Benchmark SMA Period", Group = "4. Benchmark Filter", DefaultValue = 50, MinValue = 10)]
        public int BenchmarkSmaPeriod { get; set; } = 50;

        [Parameter("Benchmark Buffer (x ATR)", Group = "4. Benchmark Filter", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 2.0, Step = 0.1)]
        public double BenchmarkBufferAtr { get; set; } = 0.5;

        // =========================================================================
        // --- 4b. VIX Long Block (VIX close > threshold blocks longs; shorts unaffected) ---
        // =========================================================================
        [Parameter("Require VIX Long Block", Group = "4b. VIX Long Block", DefaultValue = true)]
        public bool RequireVixFilter { get; set; } = true;

        [Parameter("VIX Symbol", Group = "4b. VIX Long Block", DefaultValue = "VIX")]
        public string VixSymbol { get; set; } = "VIX";

        [Parameter("Max VIX Threshold (longs blocked above)", Group = "4b. VIX Long Block", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0, Step = 0.5)]
        public double MaxVixThreshold { get; set; } = 25.0;

        // =========================================================================
        // --- 4c. Crypto Benchmark (BTC vs SMA) Filter (crypto symbols only) ---
        // =========================================================================
        [Parameter("Require Crypto Benchmark Filter (BTC vs SMA)", Group = "4c. Crypto Benchmark (BTC vs SMA)", DefaultValue = false)]
        public bool RequireCryptoBenchmarkFilter { get; set; } = false;

        [Parameter("Crypto Benchmark Symbol", Group = "4c. Crypto Benchmark (BTC vs SMA)", DefaultValue = "BTCUSD")]
        public string CryptoBenchmarkSymbol { get; set; } = "BTCUSD";

        [Parameter("Crypto Benchmark SMA Period", Group = "4c. Crypto Benchmark (BTC vs SMA)", DefaultValue = 50, MinValue = 10)]
        public int CryptoBenchmarkSmaPeriod { get; set; } = 50;

        [Parameter("Crypto Symbols (comma separated)", Group = "4c. Crypto Benchmark (BTC vs SMA)", DefaultValue = DefaultCryptoSymbolsCsv)]
        public string CryptoSymbolsCsv { get; set; } = DefaultCryptoSymbolsCsv;

        // =========================================================================
        // --- 5. Alerts ---
        // =========================================================================
        [Parameter("Alert Sound", Group = "5. Alerts", DefaultValue = true)]
        public bool AlertSound { get; set; } = true;

        [Parameter("Alert Popup", Group = "5. Alerts", DefaultValue = true)]
        public bool AlertPopup { get; set; } = true;

        // Active setup info tracked between scan passes.
        public class ArmedContinuationSetup
        {
            public string Symbol { get; set; } = "";
            public ReversalDirection Direction { get; set; }
            public int ExtremeIndex { get; set; }
            public double SwingHigh { get; set; }
            public double SwingLow { get; set; }
            public int EmaTouchIndex { get; set; }
            public int TriggerIndex { get; set; }
            public DateTime SignalBarTime { get; set; }
            public double Close { get; set; }
            public double Clv { get; set; }
            public double Tsi { get; set; }
            public double TsiSig { get; set; }
            public double Ema50 { get; set; }
            public double Sma200 { get; set; }
            public double Atr { get; set; }
            public double DistanceAtr { get; set; }
            public double LivePrice { get; set; }
            public DateTime FirstDetected { get; set; }
            public DateTime LastSeenTime { get; set; }
        }

        private sealed class IndicatorSnapshot
        {
            public int BarsCount { get; init; }
            public DateTime TargetBarTime { get; init; }
            public double[] Closes { get; init; } = Array.Empty<double>();
            public double[] Highs { get; init; } = Array.Empty<double>();
            public double[] Lows { get; init; } = Array.Empty<double>();
            public double[] Ema50 { get; init; } = Array.Empty<double>();
            public double[] SlowEma50 { get; init; } = Array.Empty<double>();
            public double[] Sma200 { get; init; } = Array.Empty<double>();
            public double[] Atr { get; init; } = Array.Empty<double>();
            public (double[] Tsi, double[] TsiSig) Tsi { get; init; }
        }

        // State tracking
        private readonly List<string> _watchlistSymbols = new();
        private readonly Dictionary<string, ArmedContinuationSetup> _activeSetups = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentAlertMessages = new();
        private readonly Dictionary<string, int> _currentPassRejects = new();
        private readonly Dictionary<string, List<string>> _currentPassSkips = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndicatorSnapshot> _indicatorCache = new(StringComparer.OrdinalIgnoreCase);
        private Stopwatch _passStopwatch = new();
        private DateTime _lastScanTime = DateTime.MinValue;
        private DateTime _nextScanTime = DateTime.MinValue;
        private int _scanPassCount;
        private int _totalAlertsFired;
        private int _currentBatchIndex;
        private int _currentPassScanned;
        private int _currentPassSkipped;
        private int _currentPassAlerts;
        // Alert identity: at most one alert per completed signal bar. The bar index of a trigger shifts
        // whenever new bars are appended, so the bar's open time is the only stable identity.
        private readonly Dictionary<string, DateTime> _lastAlertedSignalBar = new(StringComparer.OrdinalIgnoreCase);
        private bool _isPassInProgress;
        private bool _isStopped;
        private DateTime _lastHudUpdateTime = DateTime.MinValue;
        private Button? _scanNowButton;
        private const string HudTableName = "CONTINUATION_SCANNER_HUD";
        private const int MaxSynchronousHistoryLoadsPerSymbol = 3;

        // SPY benchmark gate (recomputed once per scan pass).
        private string _resolvedBenchmarkSymbol = "SPY.US";
        private bool _spyLongOk = true;
        private bool _spyShortOk = true;
        private double _spyClose = double.NaN;
        private double _spySma50 = double.NaN;
        private string _spyDetail = "Pending first check";

        // VIX long-block gate (recomputed once per scan pass). Blocks longs only when the VIX close > threshold.
        private string _resolvedVixSymbol = "VIX";
        private bool _vixLongOk = true;
        private double _vixLive = double.NaN;
        private string _vixDetail = "Pending first check";

        // Crypto benchmark gate (recomputed once per scan pass). Applies only to the configured crypto symbols.
        private string _resolvedCryptoBenchmarkSymbol = "BTCUSD";
        private bool _btcLongOk = true;
        private bool _btcShortOk = true;
        private double _btcClose = double.NaN;
        private double _btcSma = double.NaN;
        private string _btcDetail = "Pending first check";
        private readonly HashSet<string> _cryptoSymbols = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnStart()
        {
            _isStopped = false;
            _isPassInProgress = false;
            _activeSetups.Clear();
            _recentAlertMessages.Clear();
            _scanPassCount = 0;
            _totalAlertsFired = 0;
            _lastAlertedSignalBar.Clear();
            _indicatorCache.Clear();

            _resolvedBenchmarkSymbol = ResolveSymbolName(BenchmarkSymbol, "SPY.US", "SPY", "SPY.ETF");
            _resolvedVixSymbol = ResolveSymbolName(VixSymbol, "VIX", "VIXY.US", ".VIX", "VOLX", "VXX.US");
            _resolvedCryptoBenchmarkSymbol = ResolveSymbolName(CryptoBenchmarkSymbol, "BTCUSD", "BTCEUR", "BTCGBP", "XBTUSD");
            LoadCryptoSymbols();

            LoadWatchlist();
            CreateScanButton();

            Print($"[ContinuationScanner] Started. Watchlist '{WatchlistName}' loaded with {_watchlistSymbols.Count} symbols.");
            Print($"[ContinuationScanner] Schedule: {ScheduleMode} | Direction: {AllowedDirection} | TimeFrame: Daily (evaluates last completed closed bar).");
            Print($"[ContinuationScanner] Indicators: EMA({EmaPeriod})/TrendEMA({TrendEmaPeriod}) | SMA({Sma200Period}) | ATR({AtrPeriod}) | TSI({TsiLongPeriod},{TsiShortPeriod},{TsiSignalPeriod}). (No RSI — TSI zero-line regime + reversal-style divergence suppression on the trigger bar; signal line display-only.)");
            Print($"[ContinuationScanner] Divergence suppression (same rule as the ReversalScanner): lookback {DivergenceLookback}, min gap {DivergenceMinGap}, TSI extreme > {DivergenceTsiExtremeLevel:F1}, min TSI gap {DivergenceMinTsiDrop:F2}, trigger window {DivergenceTriggerWindow} bars (0 = off).");
            Print($"[ContinuationScanner] Thresholds: CLV +-{ClvThreshold:F2}. No SL/PT computed (alert-only).");
            Print($"[ContinuationScanner] Latest closed bar must touch EMA21 and reclaim/break it. Benchmark: {(RequireBenchmarkFilter ? $"ENABLED ('{_resolvedBenchmarkSymbol}', SMA{BenchmarkSmaPeriod}, US equities only)" : "DISABLED")}. Alert-only (entry = next open).");
            Print($"[ContinuationScanner] VIX Long Block: {(RequireVixFilter ? $"ENABLED (Symbol='{_resolvedVixSymbol}', Threshold > {MaxVixThreshold:F1}, US equities only, shorts unaffected)" : "DISABLED")}.");
            Print($"[ContinuationScanner] Crypto benchmark: {(RequireCryptoBenchmarkFilter ? $"ENABLED (Symbol='{_resolvedCryptoBenchmarkSymbol}', SMA{CryptoBenchmarkSmaPeriod}, {_cryptoSymbols.Count} crypto symbols; longs need BTC > SMA, shorts need BTC < SMA)" : "DISABLED")}.");

            DrawHud(0, _watchlistSymbols.Count, 0, "Starting scan pass #1...");

            _nextScanTime = ScanOnStart ? Server.TimeInUtc : ReversalEngine.CalculateNextScanTime(ScheduleMode, ScanIntervalSeconds, Server.TimeInUtc, DailyCloseHourEt);
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
            else if (ScheduleMode != ReversalScheduleMode.ManualOnly)
            {
                if ((now - _lastHudUpdateTime).TotalSeconds >= 1.0)
                {
                    _lastHudUpdateTime = now;
                    var remaining = _nextScanTime - now;
                    int mins = (int)remaining.TotalMinutes;
                    int secs = remaining.Seconds;
                    string countdown = mins > 0 ? $"{mins}m {secs:D2}s" : $"{secs}s";
                    string status = $"Idle (next scan at {_nextScanTime:HH:mm:ss} UTC — in {countdown})";
                    DrawHud(_currentPassScanned, _watchlistSymbols.Count, _currentPassAlerts, status);
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
            Print($"[ContinuationScanner] Stopped. Total alerts fired: {_totalAlertsFired}. Active setups: {_activeSetups.Count}.");
        }

        private void CreateScanButton()
        {
            try
            {
                _scanNowButton = new Button
                {
                    Text = "SCAN NOW",
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
                Print($"[ContinuationScanner] Note: Could not attach on-chart button: {ex.Message}");
            }
        }

        private void OnScanNowButtonClick(ButtonClickEventArgs obj) => TriggerManualScan("On-Chart Button Click");

        public void TriggerManualScan(string source = "Manual")
        {
            if (_isPassInProgress)
            {
                Print($"[ContinuationScanner] Scan pass already in progress. Ignoring request from {source}.");
                return;
            }
            Print($"[ContinuationScanner] Triggering scan pass immediately ({source}).");
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
                        if (!string.IsNullOrWhiteSpace(s) && !_watchlistSymbols.Contains(s))
                            _watchlistSymbols.Add(s);
                }
                else
                {
                    var available = Watchlists.Select(w => $"'{w.Name}' ({w.SymbolNames.Count})").ToArray();
                    string availStr = available.Length > 0 ? string.Join(", ", available) : "None";
                    Print($"[ContinuationScanner] Watchlist '{WatchlistName}' {(wl == null ? "not found" : "is empty")}. Available: {availStr}. Falling back to '{SymbolName}'.");
                    _watchlistSymbols.Add(SymbolName);
                }
            }
            catch (Exception ex)
            {
                Print($"[ContinuationScanner] Error loading watchlist: {ex.Message}. Falling back to '{SymbolName}'.");
                _watchlistSymbols.Add(SymbolName);
            }
        }

        private void StartScanPass()
        {
            if (_isStopped || _watchlistSymbols.Count == 0) return;

            if (_scanNowButton != null)
            {
                _scanNowButton.Text = "SCANNING...";
                _scanNowButton.BackgroundColor = Color.FromHex("#D97706");
            }

            // Refresh the SPY benchmark gate once for this pass (EOD: last completed SPY bar close vs SMA50).
            var gate = CheckBenchmarkGate();
            _spyLongOk = gate.LongOk;
            _spyShortOk = gate.ShortOk;
            _spyClose = gate.SpyClose;
            _spySma50 = gate.SpySma;
            _spyDetail = gate.Detail;
            if (RequireBenchmarkFilter)
                Print($"[ContinuationScanner] Benchmark gate ({_resolvedBenchmarkSymbol}): {gate.Detail} | Longs {(gate.LongOk ? "ALLOWED" : "BLOCKED")} | Shorts {(gate.ShortOk ? "ALLOWED" : "BLOCKED")}.");

            // Refresh the VIX long-block gate once for this pass (last completed VIX close > threshold blocks longs).
            var vix = CheckVixGate();
            _vixLongOk = vix.LongOk;
            _vixLive = vix.LiveVix;
            _vixDetail = vix.Detail;
            if (RequireVixFilter)
                Print($"[ContinuationScanner] VIX long-block gate ({_resolvedVixSymbol}): {vix.Detail} | Longs {(vix.LongOk ? "ALLOWED" : "BLOCKED")} | Shorts ALWAYS ALLOWED.");

            // Refresh the crypto benchmark gate once for this pass (live BTC price vs SMA of completed BTC daily bars).
            var btc = CheckCryptoBenchmarkGate();
            _btcLongOk = btc.LongOk;
            _btcShortOk = btc.ShortOk;
            _btcClose = btc.BtcClose;
            _btcSma = btc.BtcSma;
            _btcDetail = btc.Detail;
            if (RequireCryptoBenchmarkFilter)
                Print($"[ContinuationScanner] Crypto benchmark gate ({_resolvedCryptoBenchmarkSymbol}): {btc.Detail} | Longs {(btc.LongOk ? "ALLOWED" : "BLOCKED")} | Shorts {(btc.ShortOk ? "ALLOWED" : "BLOCKED")}. (Crypto symbols only)");

            _isPassInProgress = true;
            _currentBatchIndex = 0;
            _currentPassScanned = 0;
            _currentPassSkipped = 0;
            _currentPassAlerts = 0;
            _currentPassRejects.Clear();
            _currentPassSkips.Clear();
            _passStopwatch.Restart();

            DrawHud(0, _watchlistSymbols.Count, 0, "Scanning in progress...");
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
                    Print($"[ContinuationScanner] Error scanning '{sym}': {ex.Message}");
                    RecordSkip(sym, $"Unhandled scan error: {ex.Message}");
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
            _nextScanTime = ReversalEngine.CalculateNextScanTime(ScheduleMode, ScanIntervalSeconds, _lastScanTime, DailyCloseHourEt);

            if (_scanNowButton != null)
            {
                _scanNowButton.Text = "SCAN NOW";
                _scanNowButton.BackgroundColor = Color.FromHex("#1E293B");
            }

            int total = _watchlistSymbols.Count;
            string nextDesc = ScheduleMode switch
            {
                ReversalScheduleMode.Hourly => $"at {_nextScanTime:HH:mm:ss} UTC (top of hour + 2s)",
                ReversalScheduleMode.DailyAfterClose => $"at {_nextScanTime:HH:mm:ss} UTC ({DailyCloseHourEt}:00 ET close + 2s)",
                ReversalScheduleMode.Every15Minutes => $"at {_nextScanTime:HH:mm:ss} UTC (15m mark + 2s)",
                ReversalScheduleMode.ManualOnly => "Manual Only (click SCAN NOW)",
                _ => $"in {ScanIntervalSeconds}s (at {_nextScanTime:HH:mm:ss} UTC)"
            };

            Print($"[ContinuationScanner] Pass #{_scanPassCount} complete in {_passStopwatch.ElapsedMilliseconds} ms: {_currentPassScanned}/{total} scanned ({_currentPassSkipped} skipped), {_activeSetups.Count} active setups. Next scan {nextDesc}.");

            if (_currentPassRejects.Count > 0)
            {
                int totalRejected = 0;
                var parts = new List<string>();
                foreach (var kv in _currentPassRejects.OrderByDescending(x => x.Value))
                {
                    totalRejected += kv.Value;
                    parts.Add($"{kv.Value}x {kv.Key}");
                }
                Print($"[ContinuationScanner] {totalRejected} rejections: {string.Join(" | ", parts)}.");
            }

            PrintSkipSummary();

            DrawHud(_currentPassScanned, total, _currentPassAlerts);
        }

        private (bool Scanned, bool AlertFired) ScanSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName))
            {
                RecordSkip(symbolName, "Empty symbol name");
                return (false, false);
            }

            int required = Math.Max(MinBarsToScan, Math.Max(Sma200Period + TsiLongPeriod + TsiShortPeriod, DivergenceLookback + DivergenceMinGap) + 20);
            var bars = EnsureBarsLoaded(TimeFrame.Daily, symbolName, required);
            if (bars == null)
            {
                RecordSkip(symbolName, "No daily bars (symbol unavailable or history request failed)");
                return (false, false);
            }
            if (bars.Count < Sma200Period + 10)
            {
                RecordSkip(symbolName, $"Insufficient usable daily history ({bars.Count} bars; need at least {Sma200Period + 10})");
                return (false, false);
            }

            // Always exclude the forming bar. The optional US-session heuristic refines the
            // session boundary, but it can never re-enable scanning of a live bar.
            int targetIdx = GetLastCompletedDailyBarIndex(bars, symbolName);

            if (targetIdx < 0 || targetIdx < Math.Max(Sma200Period + TsiLongPeriod + TsiShortPeriod, DivergenceLookback + DivergenceMinGap))
            {
                RecordSkip(symbolName, $"No completed daily bar with sufficient warmup (target index {targetIdx})");
                return (false, false);
            }

            int n = bars.Count;
            DateTime targetBarTime = bars.OpenTimes[targetIdx];
            if (!_indicatorCache.TryGetValue(symbolName, out var snapshot) ||
                snapshot.BarsCount != n || snapshot.TargetBarTime != targetBarTime)
            {
                double[] newCloses = new double[n];
                double[] newHighs = new double[n];
                double[] newLows = new double[n];
                for (int i = 0; i < n; i++)
                {
                    newCloses[i] = bars.ClosePrices[i];
                    newHighs[i] = bars.HighPrices[i];
                    newLows[i] = bars.LowPrices[i];
                }

                snapshot = new IndicatorSnapshot
                {
                    BarsCount = n,
                    TargetBarTime = targetBarTime,
                    Closes = newCloses,
                    Highs = newHighs,
                    Lows = newLows,
                    Ema50 = ReversalEngine.ComputeEma(newCloses, EmaPeriod),
                    SlowEma50 = ReversalEngine.ComputeEma(newCloses, TrendEmaPeriod),
                    Sma200 = ReversalEngine.ComputeSma(newCloses, Sma200Period),
                    Atr = ReversalEngine.ComputeAtr(newHighs, newLows, newCloses, AtrPeriod),
                    Tsi = ReversalEngine.ComputeTsi(newCloses, TsiLongPeriod, TsiShortPeriod, TsiSignalPeriod)
                };
                _indicatorCache[symbolName] = snapshot;
            }

            double[] closes = snapshot.Closes;
            double[] highs = snapshot.Highs;
            double[] lows = snapshot.Lows;
            double[] ema50 = snapshot.Ema50;
            double[] slowEma50 = snapshot.SlowEma50;
            double[] sma200 = snapshot.Sma200;
            double[] atr = snapshot.Atr;
            var tsi = snapshot.Tsi;

            // Max Setup Age = 0: only the last completed daily bar is evaluated, so a fresh
            // trigger must fire on that bar.
            bool alertFired = false;
            ArmedContinuationSetup? bestSetup = null;

            // SPY gate applies only to US equities; FX, metals, commodities and crypto are exempt.
            bool spyLongForSymbol = RequireBenchmarkFilter && IsUsEquitySymbol(symbolName) ? _spyLongOk : true;
            bool spyShortForSymbol = RequireBenchmarkFilter && IsUsEquitySymbol(symbolName) ? _spyShortOk : true;

            int evalIdx = targetIdx;
            {
                var res = ContinuationEngine.Evaluate(closes, highs, lows, tsi.Tsi, tsi.TsiSig,
                    ema50, slowEma50, sma200, atr, evalIdx, AllowedDirection,
                    ClvThreshold,
                    DivergenceLookback, DivergenceMinGap, DivergenceTsiExtremeLevel, DivergenceMinTsiDrop, DivergenceTriggerWindow,
                    spyLongForSymbol, spyShortForSymbol);

                if (!res.IsTriggered)
                {
                    RecordReject(res.RejectReason);
                }
                else
                {
                    var armed = BuildArmedSetup(symbolName, res, bars, evalIdx);
                    if (IsSetupStillValid(armed, symbolName, tsi.Tsi, tsi.TsiSig, closes, highs, lows, atr, targetIdx))
                        bestSetup = armed;
                }
            }

            if (bestSetup != null)
            {
                if (_activeSetups.TryGetValue(symbolName, out var existing))
                    bestSetup.FirstDetected = existing.FirstDetected;

                // One alert per completed signal bar, never backwards. A later pass always re-finds the
                // same bar inside the age window, so without this identity check every scan pass would
                // re-notify an unchanged setup (up to 24 popups/day at the hourly schedule). A brand new
                // bullish CLV bar above EMA21 is strictly newer than the last alerted bar and therefore
                // does fire a fresh alert; a stale setup that dies and re-arms on the same bar does not.
                bool alreadyAlerted = _lastAlertedSignalBar.TryGetValue(symbolName, out var lastAlertedBar) &&
                                      bestSetup.SignalBarTime <= lastAlertedBar;

                _activeSetups[symbolName] = bestSetup;

                if (!alreadyAlerted)
                {
                    _lastAlertedSignalBar[symbolName] = bestSetup.SignalBarTime;
                    Notify(symbolName, bestSetup);
                    alertFired = true;
                }
            }
            else if (_activeSetups.Remove(symbolName))
            {
                Print($"[ContinuationScanner] {symbolName} no longer has an active setup. Removed from candidates.");
            }

            return (true, alertFired);
        }

        private bool IsSetupStillValid(ArmedContinuationSetup setup, string symbolName,
            double[] tsi, double[] tsiSig, double[] closes, double[] highs, double[] lows, double[] atr, int targetIdx)
        {
            if (setup.Direction == ReversalDirection.Long &&
                RequireVixFilter && IsUsEquitySymbol(symbolName) && !_vixLongOk)
            {
                RecordReject($"VIX long-block ({_vixDetail}) — longs blocked, shorts unaffected");
                return false;
            }

            if (RequireCryptoBenchmarkFilter && IsCryptoSymbol(symbolName))
            {
                if (setup.Direction == ReversalDirection.Long && !_btcLongOk)
                {
                    RecordReject($"BTC crypto gate failed ({_btcDetail}) — crypto longs blocked");
                    return false;
                }
                if (setup.Direction == ReversalDirection.Short && !_btcShortOk)
                {
                    RecordReject($"BTC crypto gate failed ({_btcDetail}) — crypto shorts blocked");
                    return false;
                }
            }

            double liveTsi = tsi[targetIdx];
            double liveTsiSig = tsiSig[targetIdx];
            if (double.IsNaN(liveTsi) || double.IsNaN(liveTsiSig))
            {
                RecordReject("Live TSI NaN on last closed bar — cannot confirm momentum regime");
                return false;
            }
            if (setup.Direction == ReversalDirection.Long && !(liveTsi > 0.0))
            {
                RecordReject($"Live TSI {liveTsi:F2} not > 0 — momentum regime no longer bullish");
                return false;
            }
            if (setup.Direction == ReversalDirection.Short && !(liveTsi < 0.0))
            {
                RecordReject($"Live TSI {liveTsi:F2} not < 0 — momentum regime no longer bearish");
                return false;
            }
            if (DivergenceTriggerWindow > 0)
            {
                if (setup.Direction == ReversalDirection.Long &&
                    ReversalEngine.HasActiveBearishTsiDivergence(highs, tsi, targetIdx, DivergenceLookback, DivergenceMinGap, DivergenceTsiExtremeLevel, DivergenceMinTsiDrop, DivergenceTriggerWindow))
                {
                    RecordReject($"Active bearish price/TSI divergence (fresh high with lower TSI) — momentum fading under the highs");
                    return false;
                }
                if (setup.Direction == ReversalDirection.Short &&
                    ReversalEngine.HasActiveBullishTsiDivergence(lows, tsi, targetIdx, DivergenceLookback, DivergenceMinGap, DivergenceTsiExtremeLevel, DivergenceMinTsiDrop, DivergenceTriggerWindow))
                {
                    RecordReject($"Active bullish price/TSI divergence (fresh low with higher TSI) — momentum recovering under the lows");
                    return false;
                }
            }

            return true;
        }

        private ArmedContinuationSetup BuildArmedSetup(string symbolName, ContinuationSetupResult res, Bars bars, int triggerIdx)
        {
            double livePrice = GetLivePrice(symbolName);
            if (double.IsNaN(livePrice) || livePrice <= 0)
                livePrice = bars.ClosePrices[bars.Count - 1];

            return new ArmedContinuationSetup
            {
                Symbol = symbolName,
                Direction = res.Direction,
                ExtremeIndex = res.ExtremeIndex,
                SwingHigh = res.SwingHigh,
                SwingLow = res.SwingLow,
                EmaTouchIndex = res.EmaTouchIndex,
                TriggerIndex = triggerIdx,
                SignalBarTime = bars.OpenTimes[triggerIdx],
                Close = res.Close,
                Clv = res.Clv,
                Tsi = res.Tsi,
                TsiSig = res.TsiSig,
                Ema50 = res.Ema50,
                Sma200 = res.Sma200,
                Atr = res.Atr,
                DistanceAtr = res.DistanceAtr,
                LivePrice = livePrice,
                FirstDetected = Server.TimeInUtc,
                LastSeenTime = Server.TimeInUtc
            };
        }

        private void Notify(string symbolName, ArmedContinuationSetup s)
        {
            DateTime now = Server.TimeInUtc;
            _totalAlertsFired++;

            string dir = s.Direction == ReversalDirection.Short ? "SHORT" : "LONG";
            string dateTag = s.SignalBarTime != DateTime.MinValue ? s.SignalBarTime.ToString("yyyyMMdd") : now.ToString("yyyyMMdd");

            var sb = new StringBuilder();
            sb.AppendLine($"{dir} CONTINUATION SETUP — {symbolName} [{dateTag}]");
            sb.AppendLine($"  Scanned {now:yyyy-MM-dd HH:mm} UTC | Signal bar opened {s.SignalBarTime:yyyy-MM-dd} = latest COMPLETED daily bar at scan time (the live/forming bar is never evaluated)");
            sb.AppendLine($"  EMA21 touch and trigger bar #{s.TriggerIndex}");
            sb.AppendLine($"  Close: {s.Close:F4} | CLV: {s.Clv:F2} | TSI: {s.Tsi:F2} (regime >0/<0; sig {s.TsiSig:F2} display-only)");
            sb.AppendLine($"  EMA21: {s.Ema50:F4} | SMA200: {s.Sma200:F4} | ATR: {s.Atr:F4} | Live: {s.LivePrice:F4}");
            sb.AppendLine($"  ENTRY: next open (scanner reports the setup only; no SL/PT computed)");
            string detail = sb.ToString();

            string shortMsg = $"[{now:HH:mm}] {symbolName} {dir} CONT | CLV {s.Clv:F2} TSI {s.Tsi:F2} (sig {s.TsiSig:F2}) Dist {s.DistanceAtr:F2} ATR";
            _recentAlertMessages.Insert(0, shortMsg);
            if (_recentAlertMessages.Count > 6) _recentAlertMessages.RemoveAt(_recentAlertMessages.Count - 1);

            if (AlertSound) Notifications.PlaySound(SoundType.Announcement);
            if (AlertPopup) Notifications.ShowPopup("Continuation Setup", detail, PopupNotificationState.Information);

            Print($"[ContinuationScanner Alert]\n{detail}");
        }

        // =========================================================================
        // --- Benchmark (SPY) gate ---
        // =========================================================================

        /// <summary>
        /// Computes the SPY benchmark gate once per pass on the last completed SPY daily bar (EOD,
        /// no repaint): longs require SPY close &gt; SPY_SMA50, shorts require SPY close &lt; SPY_SMA50.
        /// When the filter is disabled or SPY data is unavailable, the gate is bypassed (both sides allowed).
        /// </summary>
        private (bool LongOk, bool ShortOk, double SpyClose, double SpySma, string Detail) CheckBenchmarkGate()
        {
            if (!RequireBenchmarkFilter)
                return (true, true, double.NaN, double.NaN, "Benchmark filter disabled (bypassed)");

            try
            {
                int minBars = BenchmarkSmaPeriod + 20;
                var spyBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedBenchmarkSymbol, minBars);
                if (spyBars == null || spyBars.Count < minBars)
                    return (true, true, double.NaN, double.NaN, $"SPY data unavailable ({_resolvedBenchmarkSymbol} bars < {minBars}) — filter bypassed");

                int n = spyBars.Count;
                // SPY is a US-session instrument: select its bar with the US cash session model even
                // when the broker lists it without the '.US' suffix (see the overload below).
                int evalIdx = GetLastCompletedDailyBarIndex(spyBars, UseUsSessionBarLogic);
                if (evalIdx < 0) return (true, true, double.NaN, double.NaN, "SPY completed bar unavailable — filter bypassed");

                double spyClose = spyBars.ClosePrices[evalIdx];
                int startIdx = evalIdx - BenchmarkSmaPeriod + 1;
                if (startIdx < 0) startIdx = 0;
                double sum = 0.0;
                int count = 0;
                for (int i = startIdx; i <= evalIdx; i++) { sum += spyBars.ClosePrices[i]; count++; }
                double spySma = count > 0 ? sum / count : double.NaN;

                var spyHighs = new double[spyBars.Count];
                var spyLows = new double[spyBars.Count];
                var spyCloses = new double[spyBars.Count];
                for (int i = 0; i < spyBars.Count; i++)
                {
                    spyHighs[i] = spyBars.HighPrices[i];
                    spyLows[i] = spyBars.LowPrices[i];
                    spyCloses[i] = spyBars.ClosePrices[i];
                }
                double[] spyAtrValues = ReversalEngine.ComputeAtr(spyHighs, spyLows, spyCloses, AtrPeriod);
                double spyAtr = spyAtrValues[evalIdx];

                if (double.IsNaN(spySma) || spySma <= 0.0 || double.IsNaN(spyAtr) || spyAtr <= 0.0)
                    return (true, true, spyClose, double.NaN, "SPY SMA unavailable — filter bypassed");

                double buffer = BenchmarkBufferAtr * spyAtr;
                bool longHeadwind = spyClose < spySma - buffer;
                bool shortHeadwind = spyClose > spySma + buffer;
                bool longOk = !longHeadwind;
                bool shortOk = !shortHeadwind;
                string detail = $"SPY {spyClose:F2} vs SMA{BenchmarkSmaPeriod} +/- {BenchmarkBufferAtr:F1} ATR ({spyAtr:F2}) -> {(longHeadwind ? "SPY below buffer (longs blocked)" : shortHeadwind ? "SPY above buffer (shorts blocked)" : "SPY inside buffer (both allowed)")}";
                return (longOk, shortOk, spyClose, spySma, detail);
            }
            catch (Exception ex)
            {
                return (true, true, double.NaN, double.NaN, $"SPY check error: {ex.Message} — filter bypassed");
            }
        }

        // =========================================================================
        // --- VIX long-block gate ---
        // =========================================================================

        /// <summary>
        /// Computes the VIX long-block gate once per pass using the last completed daily VIX close
        /// (close-based, no live data). When the filter is disabled or VIX data is unavailable, the
        /// gate is bypassed (longs allowed). Shorts are never affected. Longs are blocked when the
        /// VIX close strictly exceeds <see cref="MaxVixThreshold"/>.
        /// </summary>
        private (bool LongOk, double LiveVix, string Detail) CheckVixGate()
        {
            if (!RequireVixFilter)
                return (true, double.NaN, "VIX long-block disabled (bypassed)");

            try
            {
                // Close-based: last completed daily VIX close.
                var vixBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedVixSymbol, 5);
                if (vixBars != null && vixBars.Count > 0)
                {
                    // VIX follows the US cash session: same 16:00 ET rule, independent of the suffix.
                    int evalIdx = GetLastCompletedDailyBarIndex(vixBars, UseUsSessionBarLogic);
                    if (evalIdx < 0) return (true, double.NaN, $"VIX completed bar unavailable for '{_resolvedVixSymbol}' — long-block bypassed");
                    double closeVix = vixBars.ClosePrices[evalIdx];
                    if (!double.IsNaN(closeVix) && closeVix > 0)
                        return VixLongDecision(closeVix);
                }

                return (true, double.NaN, $"VIX data unavailable for '{_resolvedVixSymbol}' — long-block bypassed");
            }
            catch (Exception ex)
            {
                return (true, double.NaN, $"VIX check error ({_resolvedVixSymbol}): {ex.Message} — long-block bypassed");
            }
        }

        private (bool LongOk, double LiveVix, string Detail) VixLongDecision(double vix)
        {
            bool longOk = !(vix > MaxVixThreshold);
            string detail = longOk
                ? $"VIX {vix:F2} <= {MaxVixThreshold:F1} (longs allowed)"
                : $"VIX {vix:F2} > {MaxVixThreshold:F1} (LONGS BLOCKED — high volatility regime; shorts unaffected)";
            return (longOk, vix, detail);
        }

        /// <summary>VIX gate applies only to US equities; FX, metals, commodities and crypto are exempt.</summary>
        private static bool IsUsEquitySymbol(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return false;
            return symbolName.EndsWith(".US", StringComparison.OrdinalIgnoreCase);
        }

        // =========================================================================
        // --- Crypto benchmark (BTC vs SMA) gate ---
        // =========================================================================

        /// <summary>
        /// Computes the crypto benchmark gate once per pass using the live BTC quote against an SMA
        /// calculated only from completed BTC daily bars. This is an intentional live regime filter,
        /// not an EOD trigger condition. Only symbols in the configured crypto list are affected.
        /// When the filter is disabled or BTC data is unavailable, the gate is bypassed.
        /// </summary>
        private (bool LongOk, bool ShortOk, double BtcClose, double BtcSma, string Detail) CheckCryptoBenchmarkGate()
        {
            if (!RequireCryptoBenchmarkFilter)
                return (true, true, double.NaN, double.NaN, "Crypto benchmark filter disabled (bypassed)");

            try
            {
                int minBars = CryptoBenchmarkSmaPeriod + 20;
                var btcBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedCryptoBenchmarkSymbol, minBars);
                if (btcBars == null || btcBars.Count < minBars)
                    return (true, true, double.NaN, double.NaN, $"BTC data unavailable ({_resolvedCryptoBenchmarkSymbol} bars < {minBars}) — filter bypassed");

                int n = btcBars.Count;

                // Crypto trades 24/7: the last completed bar is the newest bar whose own 24h window has
                // elapsed, so a stale feed or a late rollover never costs a bar (no repaint either).
                int evalIdx = GetLastCompletedDailyBarIndex(btcBars, false);
                if (evalIdx < 0)
                    return (true, true, double.NaN, double.NaN, "BTC completed bar unavailable — filter bypassed");

                // SMA is computed on completed bars only (no repaint).
                int startIdx = evalIdx - CryptoBenchmarkSmaPeriod + 1;
                if (startIdx < 0) startIdx = 0;
                double sum = 0.0;
                int count = 0;
                for (int i = startIdx; i <= evalIdx; i++) { sum += btcBars.ClosePrices[i]; count++; }
                double btcSma = count > 0 ? sum / count : double.NaN;

                var btcHighs = new double[btcBars.Count];
                var btcLows = new double[btcBars.Count];
                var btcCloses = new double[btcBars.Count];
                for (int i = 0; i < btcBars.Count; i++)
                {
                    btcHighs[i] = btcBars.HighPrices[i];
                    btcLows[i] = btcBars.LowPrices[i];
                    btcCloses[i] = btcBars.ClosePrices[i];
                }
                double[] btcAtrValues = ReversalEngine.ComputeAtr(btcHighs, btcLows, btcCloses, AtrPeriod);
                double btcAtr = btcAtrValues[evalIdx];

                if (double.IsNaN(btcSma) || btcSma <= 0.0 || double.IsNaN(btcAtr) || btcAtr <= 0.0)
                    return (true, true, double.NaN, double.NaN, "BTC SMA unavailable — filter bypassed");

                // The live BTC quote is intentional: it keeps the crypto regime gate current while
                // the SMA reference remains based on completed bars. If no quote is available, use
                // the last completed close so the gate degrades deterministically.
                double btcClose = GetLivePrice(_resolvedCryptoBenchmarkSymbol);
                if (double.IsNaN(btcClose) || btcClose <= 0)
                    btcClose = btcBars.ClosePrices[evalIdx];

                double buffer = BenchmarkBufferAtr * btcAtr;
                bool longHeadwind = btcClose < btcSma - buffer;
                bool shortHeadwind = btcClose > btcSma + buffer;
                bool longOk = !longHeadwind;
                bool shortOk = !shortHeadwind;
                string detail = $"BTC live {btcClose:F2} vs SMA{CryptoBenchmarkSmaPeriod} +/- {BenchmarkBufferAtr:F1} ATR ({btcAtr:F2}) -> {(longHeadwind ? "BTC below buffer (crypto longs blocked)" : shortHeadwind ? "BTC above buffer (crypto shorts blocked)" : "BTC inside buffer (both allowed)")}";
                return (longOk, shortOk, btcClose, btcSma, detail);
            }
            catch (Exception ex)
            {
                return (true, true, double.NaN, double.NaN, $"BTC check error: {ex.Message} — filter bypassed");
            }
        }

        /// <summary>Parses the configured crypto symbol list into the lookup set (spaces ignored).</summary>
        private void LoadCryptoSymbols()
        {
            _cryptoSymbols.Clear();
            if (string.IsNullOrWhiteSpace(CryptoSymbolsCsv)) return;
            foreach (var part in CryptoSymbolsCsv.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string raw = part.Trim();
                if (raw.Length == 0) continue;
                _cryptoSymbols.Add(raw);
                string compact = raw.Replace(" ", "").Replace("\t", "");
                if (compact.Length > 0) _cryptoSymbols.Add(compact);
            }
        }

        /// <summary>True when the symbol is in the configured crypto list (spaces ignored, e.g. "BTC EUR" matches "BTCEUR").</summary>
        private bool IsCryptoSymbol(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return false;
            if (_cryptoSymbols.Contains(symbolName)) return true;
            string compact = symbolName.Replace(" ", "");
            return compact.Length > 0 && _cryptoSymbols.Contains(compact);
        }

        private string ResolveSymbolName(string preferred, params string[] fallbacks)
        {
            if (IsSymbolValid(preferred)) return preferred;
            foreach (var c in fallbacks)
            {
                if (!string.Equals(c, preferred, StringComparison.OrdinalIgnoreCase) && IsSymbolValid(c))
                {
                    Print($"[ContinuationScanner] Preferred symbol '{preferred}' not found on broker. Auto-resolved to '{c}'.");
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
                var b = MarketData.GetBars(TimeFrame.Daily, name);
                if (b != null && b.Count > 0) return true;
            }
            catch { }
            return false;
        }

        // =========================================================================
        // --- Shared data helpers ---
        // =========================================================================

        private Bars? EnsureBarsLoaded(TimeFrame timeFrame, string symbolName, int minBars)
        {
            try
            {
                var bars = MarketData.GetBars(timeFrame, symbolName);
                if (bars == null) return null;

                if (bars.Count < minBars)
                {
                    int guard = 0;
                    while (bars.Count < minBars && guard++ < MaxSynchronousHistoryLoadsPerSymbol)
                    {
                        int loaded = bars.LoadMoreHistory();
                        if (loaded <= 0) break;
                    }
                    // Do not queue unobserved async loads on every scan pass. A later pass retries
                    // the bounded synchronous load without blocking the cBot for an unbounded time.
                    int minUsable = Math.Max(minBars / 2, 25);
                    return bars.Count >= minUsable ? bars : null;
                }
                return bars;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Index of the last completed daily bar of the scanned symbol (no repaint): the newest bar that
        /// has traded and can no longer receive data. US equities ('.US' with the session logic enabled)
        /// are completed at their 16:00 ET session close, every other instrument once its own 24h window
        /// has elapsed. Outside market hours that is the newest closed bar of the series (never the one
        /// before it), and a still-forming bar is never evaluated.
        /// </summary>
        private int GetLastCompletedDailyBarIndex(Bars bars, string symbolName)
        {
            return GetLastCompletedDailyBarIndex(bars, IsUsEquitySymbol(symbolName) && UseUsSessionBarLogic);
        }

        /// <summary>
        /// Index of the last completed daily bar with an explicit session model. The benchmark gates use
        /// US-session instruments (SPY, VIX) and therefore select their bar with the same 16:00 ET rule
        /// even when the broker lists them without the '.US' suffix; the crypto benchmark stays on the
        /// 24h window rule because it trades around the clock. The actual selection - including skipping
        /// untouched bars the broker pre-created for the next session and bars stamped in the future - is
        /// shared with TradeManager via <see cref="ReversalEngine.GetLastCompletedDailyBarIndex"/>.
        /// </summary>
        private int GetLastCompletedDailyBarIndex(Bars bars, bool usCashEquity)
        {
            if (bars == null || bars.Count == 0) return -1;

            int count = bars.Count;
            var openTimes = new DateTime[count];
            var hasTraded = new bool[count];
            for (int i = 0; i < count; i++)
            {
                openTimes[i] = bars.OpenTimes[i];
                hasTraded[i] = !ReversalEngine.IsUntouchedDailyBar(
                    bars.OpenPrices[i], bars.HighPrices[i], bars.LowPrices[i], bars.ClosePrices[i], bars.TickVolumes[i]);
            }

            return ReversalEngine.GetLastCompletedDailyBarIndex(
                openTimes, hasTraded, usCashEquity, Server.TimeInUtc);
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
                var dBars = MarketData.GetBars(TimeFrame.Daily, symbolName);
                if (dBars != null && dBars.Count > 0) return dBars.ClosePrices.LastValue;
            }
            catch { }
            return double.NaN;
        }

        private void RecordReject(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;
            string key = reason.Length > 48 ? reason.Substring(0, 48) : reason;
            _currentPassRejects.TryGetValue(key, out int c);
            _currentPassRejects[key] = c + 1;
        }

        private void RecordSkip(string symbolName, string reason)
        {
            string key = string.IsNullOrWhiteSpace(reason) ? "Unknown skip reason" : reason;
            if (!_currentPassSkips.TryGetValue(key, out var symbols))
            {
                symbols = new List<string>();
                _currentPassSkips[key] = symbols;
            }
            symbols.Add(string.IsNullOrWhiteSpace(symbolName) ? "<empty>" : symbolName);
        }

        private void PrintSkipSummary()
        {
            foreach (var entry in _currentPassSkips.OrderByDescending(x => x.Value.Count))
                Print($"[ContinuationScanner] Skipped {entry.Value.Count}: {entry.Key} | Symbols: {string.Join(", ", entry.Value)}");
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
            if (_nextScanTime > DateTime.MinValue && _nextScanTime < DateTime.MaxValue)
            {
                var rem = _nextScanTime - Server.TimeInUtc;
                int m = Math.Max(0, (int)rem.TotalMinutes);
                int s = Math.Max(0, rem.Seconds);
                string modeDesc = ScheduleMode switch
                {
                    ReversalScheduleMode.Hourly => "Hourly (:00:02 UTC)",
                    ReversalScheduleMode.DailyAfterClose => $"Daily ({DailyCloseHourEt}:00 ET close)",
                    ReversalScheduleMode.Every15Minutes => "Quarter-Hour (:15/:30/:45:02)",
                    ReversalScheduleMode.CustomInterval => $"Interval ({ScanIntervalSeconds}s)",
                    ReversalScheduleMode.ManualOnly => "Manual Only",
                    _ => "Next scan"
                };
                defaultStatus = $"Idle ({modeDesc} at {_nextScanTime:yyyy-MM-dd HH:mm:ss} UTC — in {m}m {s:D2}s)";
            }
            else
            {
                defaultStatus = ScheduleMode == ReversalScheduleMode.ManualOnly ? "Manual Only (click SCAN NOW)" : "Idle (next scan scheduled)";
            }

            string statusText = !string.IsNullOrEmpty(statusOverride) ? statusOverride : defaultStatus;

            string spyStatus = RequireBenchmarkFilter
                ? $"SPY {_spyClose:F2} vs SMA{BenchmarkSmaPeriod} {_spySma50:F2} | Longs {(_spyLongOk ? "OK" : "BLOCKED")} | Shorts {(_spyShortOk ? "OK" : "BLOCKED")}"
                : "Benchmark filter OFF";

            string vixLiveStr = !double.IsNaN(_vixLive) ? _vixLive.ToString("F2") : "N/A";
            string vixStatus = RequireVixFilter
                ? $"VIX close {vixLiveStr} vs > {MaxVixThreshold:F1} | Longs {(_vixLongOk ? "OK" : "BLOCKED")} | Shorts OK"
                : "VIX long-block OFF";

            string btcStatus = RequireCryptoBenchmarkFilter
                ? $"BTC {_btcClose:F2} vs SMA{CryptoBenchmarkSmaPeriod} {_btcSma:F2} | Longs {(_btcLongOk ? "OK" : "BLOCKED")} | Shorts {(_btcShortOk ? "OK" : "BLOCKED")}"
                : "Crypto benchmark OFF";

            var sb = new StringBuilder();
            sb.AppendLine("=== CONTINUATION SCANNER (Daily, EOD signals, entry next open) ===");
            sb.AppendLine($"Watchlist: {WatchlistName} ({totalCount} symbols) | Trigger: {ScheduleMode} | Direction: {AllowedDirection}");
            sb.AppendLine($"EMA({EmaPeriod})/EMA({TrendEmaPeriod}) | SMA({Sma200Period}) | ATR({AtrPeriod}) | TSI({TsiLongPeriod},{TsiShortPeriod},{TsiSignalPeriod}) | No lookback | No RSI");
            sb.AppendLine($"Thresholds: CLV +-{ClvThreshold:F2} | No SL/PT");
            sb.AppendLine($"Benchmark: {spyStatus} | {_spyDetail}");
            sb.AppendLine($"VIX: {vixStatus} | {_vixDetail}");
            sb.AppendLine($"Crypto: {btcStatus} | {_btcDetail}");
            sb.AppendLine($"Status: {statusText}");
            sb.AppendLine($"Pass #{_scanPassCount} | Scanned: {scannedCount}/{totalCount} | Active Setups: {_activeSetups.Count} | Total Alerts: {_totalAlertsFired}");
            sb.AppendLine($"Last Scan: {(_lastScanTime == DateTime.MinValue ? "Pending..." : _lastScanTime.ToString("HH:mm:ss") + " UTC")}");

            if (_activeSetups.Count > 0)
            {
                sb.AppendLine("\n--- ACTIVE CONTINUATION SETUPS ---");
                var sorted = _activeSetups.Values.OrderByDescending(x => x.SignalBarTime).ToList();
                foreach (var item in sorted)
                {
                    string dir = item.Direction == ReversalDirection.Short ? "SHORT" : "LONG";
                    double diff = item.Direction == ReversalDirection.Short
                        ? (item.Close - item.LivePrice)
                        : (item.LivePrice - item.Close);
                    double atrPnl = item.Atr > 0 ? (diff / item.Atr) : 0.0;
                    string pnlSign = atrPnl >= 0 ? "+" : "";
                    string dateTag = item.SignalBarTime != DateTime.MinValue ? item.SignalBarTime.ToString("yyyy-MM-dd") : "?";
                    sb.AppendLine($"[{dir}] {item.Symbol,-8} | {dateTag} | Live: {item.LivePrice:F4} ({pnlSign}{atrPnl:F2} ATR) | CLV: {item.Clv:F2} | TSI: {item.Tsi:F2} (sig {item.TsiSig:F2}) | EMA21: {item.Ema50:F4} | SMA200: {item.Sma200:F4} | Dist: {item.DistanceAtr:F2} ATR | Confirmed: {item.LastSeenTime:MM-dd HH:mm} UTC");
                }
            }
            else
            {
                sb.AppendLine("\n--- No Active Setups ---");
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
