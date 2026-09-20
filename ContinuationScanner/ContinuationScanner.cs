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
    /// Final no-RSI continuation logic:
    ///   Long Continuation: some bar in the trailing lookback bars touches Low &lt;= EMA50, then the trigger bar
    ///     closes &gt; EMA50 with CLV &gt;= +0.35, PPO &gt; PPOsig, and SPY &gt; SPY_SMA50.
    ///   Short Continuation: some bar in the trailing lookback bars touches High &gt;= EMA50, then the trigger bar
    ///     closes &lt; EMA50 with CLV &lt;= -0.35, PPO &lt; PPOsig, and SPY &lt; SPY_SMA50.
    ///
    /// PPO on the trigger bar is the decisive momentum check. RSI is intentionally not used.
    /// Continuations additionally require the SMA200 long-term trend filter (Close > SMA200 for
    /// longs, Close < SMA200 for shorts). Reversals do not use SMA200.
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

        [Parameter("Max Setup Age (Trading Days, 0 = Today Only)", Group = "1. Scan Setup", DefaultValue = 2, MinValue = 0, MaxValue = 5)]
        public int MaxSetupAgeTradingDays { get; set; } = 2;

        [Parameter("US Session Bar Logic (US equities only)", Group = "1. Scan Setup", DefaultValue = true)]
        public bool UseUsSessionBarLogic { get; set; } = true;

        // =========================================================================
        // --- 2. Indicator Parameters ---
        // =========================================================================
        [Parameter("EMA Period", Group = "2. Indicators", DefaultValue = 50, MinValue = 10)]
        public int EmaPeriod { get; set; } = 50;

        [Parameter("SMA200 Period (Long-Term Trend)", Group = "2. Indicators", DefaultValue = 200, MinValue = 20)]
        public int Sma200Period { get; set; } = 200;

        [Parameter("ATR Period", Group = "2. Indicators", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; } = 14;

        [Parameter("PPO Fast (EMA)", Group = "2. Indicators", DefaultValue = 16, MinValue = 2)]
        public int PpoFastPeriod { get; set; } = 16;

        [Parameter("PPO Slow (EMA)", Group = "2. Indicators", DefaultValue = 32, MinValue = 5)]
        public int PpoSlowPeriod { get; set; } = 32;

        [Parameter("PPO Signal (EMA)", Group = "2. Indicators", DefaultValue = 9, MinValue = 1)]
        public int PpoSignalPeriod { get; set; } = 9;

        [Parameter("Lookback (bars)", Group = "2. Indicators", DefaultValue = 5, MinValue = 5, MaxValue = 120)]
        public int Lookback { get; set; } = 5;

        // =========================================================================
        // --- 3. Continuation Thresholds ---
        // =========================================================================
        [Parameter("CLV Threshold (abs)", Group = "3. Continuation Thresholds", DefaultValue = 0.35, MinValue = 0.0, MaxValue = 1.0, Step = 0.05)]
        public double ClvThreshold { get; set; } = 0.35;

        [Parameter("Require Max Distance To Level", Group = "3. Continuation Thresholds", DefaultValue = true)]
        public bool RequireMaxDistance { get; set; } = true;

        [Parameter("Max Distance To Level (x ATR)", Group = "3. Continuation Thresholds", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 5.0, Step = 0.1)]
        public double MaxDistanceAtr { get; set; } = 1.5;

        // =========================================================================
        // --- 4. Benchmark (SPY) Filter ---
        // =========================================================================
        [Parameter("Require Benchmark Filter (SPY vs SMA50)", Group = "4. Benchmark Filter", DefaultValue = true)]
        public bool RequireBenchmarkFilter { get; set; } = true;

        [Parameter("Benchmark Symbol", Group = "4. Benchmark Filter", DefaultValue = "SPY.US")]
        public string BenchmarkSymbol { get; set; } = "SPY.US";

        [Parameter("Benchmark SMA Period", Group = "4. Benchmark Filter", DefaultValue = 50, MinValue = 10)]
        public int BenchmarkSmaPeriod { get; set; } = 50;

        // =========================================================================
        // --- 4b. VIX Long Block (live VIX > threshold blocks longs; shorts unaffected) ---
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
        [Parameter("Require Crypto Benchmark Filter (BTC vs SMA)", Group = "4c. Crypto Benchmark (BTC vs SMA)", DefaultValue = true)]
        public bool RequireCryptoBenchmarkFilter { get; set; } = true;

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
            public double Ppo { get; set; }
            public double PpoSig { get; set; }
            public double Ema50 { get; set; }
            public double Sma200 { get; set; }
            public double Atr { get; set; }
            public double DistanceAtr { get; set; }
            public double LivePrice { get; set; }
            public DateTime FirstDetected { get; set; }
            public DateTime LastSeenTime { get; set; }
        }

        // State tracking
        private readonly List<string> _watchlistSymbols = new();
        private readonly Dictionary<string, ArmedContinuationSetup> _activeSetups = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentAlertMessages = new();
        private readonly Dictionary<string, int> _currentPassRejects = new();
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

        // SPY benchmark gate (recomputed once per scan pass).
        private string _resolvedBenchmarkSymbol = "SPY.US";
        private bool _spyLongOk = true;
        private bool _spyShortOk = true;
        private double _spyClose = double.NaN;
        private double _spySma50 = double.NaN;
        private string _spyDetail = "Pending first check";

        // VIX long-block gate (recomputed once per scan pass). Blocks longs only when live VIX > threshold.
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

            _resolvedBenchmarkSymbol = ResolveSymbolName(BenchmarkSymbol, "SPY.US", "SPY", "SPY.ETF");
            _resolvedVixSymbol = ResolveSymbolName(VixSymbol, "VIX", "VIXY.US", ".VIX", "VOLX", "VXX.US");
            _resolvedCryptoBenchmarkSymbol = ResolveSymbolName(CryptoBenchmarkSymbol, "BTCUSD", "BTCEUR", "BTCGBP", "XBTUSD");
            LoadCryptoSymbols();

            LoadWatchlist();
            CreateScanButton();

            Print($"[ContinuationScanner] Started. Watchlist '{WatchlistName}' loaded with {_watchlistSymbols.Count} symbols.");
            Print($"[ContinuationScanner] Schedule: {ScheduleMode} | Direction: {AllowedDirection} | TimeFrame: Daily (evaluates last completed closed bar).");
            Print($"[ContinuationScanner] Indicators: EMA({EmaPeriod}) | SMA({Sma200Period}) | ATR({AtrPeriod}) | PPO({PpoFastPeriod},{PpoSlowPeriod},{PpoSignalPeriod}) | Lookback {Lookback} bars. (No RSI — PPO on trigger bar only.)");
            Print($"[ContinuationScanner] Thresholds: CLV +-{ClvThreshold:F2} | Max dist to {Lookback}-bar level {(RequireMaxDistance ? $"{MaxDistanceAtr:F1}*ATR" : "OFF")}. No SL/PT computed (alert-only).");
            Print($"[ContinuationScanner] Lookback = {Lookback}-bar EMA50 touch + swing H/L. Benchmark: {(RequireBenchmarkFilter ? $"ENABLED ('{_resolvedBenchmarkSymbol}', SMA{BenchmarkSmaPeriod}, US equities only)" : "DISABLED")}. Alert-only (entry = next open).");
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

            // Refresh the VIX long-block gate once for this pass (live VIX > threshold blocks longs).
            var vix = CheckVixGate();
            _vixLongOk = vix.LongOk;
            _vixLive = vix.LiveVix;
            _vixDetail = vix.Detail;
            if (RequireVixFilter)
                Print($"[ContinuationScanner] VIX long-block gate ({_resolvedVixSymbol}): {vix.Detail} | Longs {(vix.LongOk ? "ALLOWED" : "BLOCKED")} | Shorts ALWAYS ALLOWED.");

            // Refresh the crypto benchmark gate once for this pass (BTC close vs SMA on the last completed BTC daily bar).
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

            Print($"[ContinuationScanner] Pass #{_scanPassCount} complete: {_currentPassScanned}/{total} scanned ({_currentPassSkipped} skipped), {_activeSetups.Count} active setups. Next scan {nextDesc}.");

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

            DrawHud(_currentPassScanned, total, _currentPassAlerts);
        }

        private (bool Scanned, bool AlertFired) ScanSymbol(string symbolName)
        {
            if (string.IsNullOrEmpty(symbolName)) return (false, false);

            int required = Math.Max(MinBarsToScan, Sma200Period + PpoSlowPeriod + PpoSignalPeriod + Lookback + 20);
            var bars = EnsureBarsLoaded(TimeFrame.Daily, symbolName, required);
            if (bars == null || bars.Count < Sma200Period + 10)
                return (false, false);

            int n = bars.Count;
            double[] closes = new double[n];
            double[] highs = new double[n];
            double[] lows = new double[n];
            for (int i = 0; i < n; i++)
            {
                closes[i] = bars.ClosePrices[i];
                highs[i] = bars.HighPrices[i];
                lows[i] = bars.LowPrices[i];
            }

            // Indicators (pure engine, shared with Reversal scanner). No RSI.
            var ppo = ReversalEngine.ComputePpo(closes, PpoFastPeriod, PpoSlowPeriod, PpoSignalPeriod);
            double[] ema50 = ReversalEngine.ComputeEma(closes, EmaPeriod);
            double[] sma200 = ReversalEngine.ComputeSma(closes, Sma200Period);
            double[] atr = ReversalEngine.ComputeAtr(highs, lows, closes, AtrPeriod);

            // Last completed daily bar (no repaint), resolved per asset class. The US-session heuristic
            // is only valid for US equities; for 24/7 crypto, FX, metals and commodities the broker
            // session calendar decides. While the market is open, bar n - 1 is still forming and n - 2
            // is the last completed bar; once the market has closed, n - 1 is the completed session bar.
            int targetIdx;
            if (IsUsEquitySymbol(symbolName))
            {
                targetIdx = UseUsSessionBarLogic
                    ? ReversalEngine.GetLastCompletedDailyBarIndex(bars.OpenTimes[n - 1], n, Server.TimeInUtc)
                    : n - 1;
            }
            else
            {
                var scannedSymbol = Symbols.GetSymbol(symbolName);
                bool isMarketOpen = scannedSymbol != null && scannedSymbol.MarketHours.IsOpened();
                targetIdx = isMarketOpen ? (n - 2) : (n - 1);

                // Protect against an unformed future bar or a flat phantom rollover bar.
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

            if (targetIdx < 0 || targetIdx < Sma200Period + PpoSlowPeriod + PpoSignalPeriod + Lookback)
            {
                RecordReject("Insufficient history");
                return (true, false);
            }

            // Scan back a few bars so freshly-triggered setups are not missed.
            int earliest = Math.Max(Sma200Period + PpoSlowPeriod + PpoSignalPeriod + Lookback, targetIdx - MaxSetupAgeTradingDays);
            bool alertFired = false;
            ArmedContinuationSetup? bestSetup = null;

            // SPY gate applies only to US equities; FX, metals, commodities and crypto are exempt.
            bool spyLongForSymbol = RequireBenchmarkFilter && IsUsEquitySymbol(symbolName) ? _spyLongOk : true;
            bool spyShortForSymbol = RequireBenchmarkFilter && IsUsEquitySymbol(symbolName) ? _spyShortOk : true;

            for (int evalIdx = targetIdx; evalIdx >= earliest; evalIdx--)
            {
                var res = ContinuationEngine.Evaluate(closes, highs, lows, ppo.Ppo, ppo.PpoSig,
                    ema50, sma200, atr, evalIdx, Lookback, AllowedDirection,
                    ClvThreshold, RequireMaxDistance, MaxDistanceAtr, spyLongForSymbol, spyShortForSymbol);

                if (!res.IsTriggered)
                {
                    if (evalIdx == targetIdx) RecordReject(res.RejectReason);
                    continue;
                }

                var armed = BuildArmedSetup(symbolName, res, bars, evalIdx);
                if (bestSetup == null || evalIdx > bestSetup.TriggerIndex)
                    bestSetup = armed;
            }

            // Pass-level validation on the last completed bar (targetIdx). These gates confirm that a
            // possibly older trigger is still alive right now: VIX regime, live PPO intact, and the
            // close still within tradeable distance of the structural extreme.
            if (bestSetup != null)
            {
                // VIX long-block: a triggered Long on a US equity is rejected when live VIX > threshold;
                // shorts are never blocked by VIX.
                if (bestSetup.Direction == ReversalDirection.Long &&
                    RequireVixFilter && IsUsEquitySymbol(symbolName) && !_vixLongOk)
                {
                    RecordReject($"VIX long-block ({_vixDetail}) — longs blocked, shorts unaffected");
                    bestSetup = null;
                }

                // Crypto benchmark gate: crypto longs require BTC > BTC_SMA, crypto shorts BTC < BTC_SMA.
                // US equities keep using the SPY gate; non-crypto, non-US assets are unaffected.
                if (RequireCryptoBenchmarkFilter && IsCryptoSymbol(symbolName))
                {
                    if (bestSetup.Direction == ReversalDirection.Long && !_btcLongOk)
                    {
                        RecordReject($"BTC crypto gate failed ({_btcDetail}) — crypto longs blocked");
                        bestSetup = null;
                    }
                    else if (bestSetup.Direction == ReversalDirection.Short && !_btcShortOk)
                    {
                        RecordReject($"BTC crypto gate failed ({_btcDetail}) — crypto shorts blocked");
                        bestSetup = null;
                    }
                }
            }

            if (bestSetup != null)
            {
                // Live-PPO re-verification: the trigger PPO must still be intact on the last completed
                // bar (targetIdx, no-repaint). A long whose PPO has since crossed below its signal, or a
                // short whose PPO has crossed above, is stale and must not be reported.
                double livePpo = ppo.Ppo[targetIdx];
                double livePpoSig = ppo.PpoSig[targetIdx];
                if (double.IsNaN(livePpo) || double.IsNaN(livePpoSig))
                {
                    RecordReject("Live PPO NaN on last closed bar — cannot confirm momentum intact");
                    bestSetup = null;
                }
                else if (bestSetup.Direction == ReversalDirection.Long && !(livePpo > livePpoSig))
                {
                    RecordReject($"Live PPO {livePpo:F2} not > PPOsig {livePpoSig:F2} — momentum no longer intact for long");
                    bestSetup = null;
                }
                else if (bestSetup.Direction == ReversalDirection.Short && !(livePpo < livePpoSig))
                {
                    RecordReject($"Live PPO {livePpo:F2} not < PPOsig {livePpoSig:F2} — momentum no longer intact for short");
                    bestSetup = null;
                }
            }

            if (bestSetup != null && RequireMaxDistance)
            {
                // Live-distance re-verification: the live price (bid/ask mid, falling back to the last
                // completed bar close when no live quote is available) must still be within
                // MaxDistanceAtr * ATR of the structural extreme. Entry happens at the next open with a
                // live price, so an older trigger whose price has since travelled past the entry window
                // is no longer tradeable even while its signal bar stays completed and unrepainted.
                double liveClose = GetLivePrice(symbolName);
                if (double.IsNaN(liveClose) || liveClose <= 0)
                    liveClose = closes[targetIdx];
                double liveAtr = atr[targetIdx];
                if (double.IsNaN(liveClose) || double.IsNaN(liveAtr) || liveAtr <= 0.0)
                {
                    RecordReject("Live price/ATR unavailable — cannot confirm entry distance");
                    bestSetup = null;
                }
                else
                {
                    double extreme = bestSetup.Direction == ReversalDirection.Long
                        ? bestSetup.SwingLow
                        : bestSetup.SwingHigh;
                    double liveDistanceAtr = bestSetup.Direction == ReversalDirection.Short
                        ? (extreme - liveClose) / liveAtr
                        : (liveClose - extreme) / liveAtr;
                    if (liveDistanceAtr >= MaxDistanceAtr)
                    {
                        RecordReject($"Live price {liveClose:F4} is {liveDistanceAtr:F2} ATR from {Lookback}-bar extreme {extreme:F4} (>= {MaxDistanceAtr:F2} ATR, entry no longer tradeable)");
                        bestSetup = null;
                    }
                }
            }

            if (bestSetup != null)
            {
                if (_activeSetups.TryGetValue(symbolName, out var existing))
                    bestSetup.FirstDetected = existing.FirstDetected;

                // One alert per completed signal bar, never backwards. A later pass always re-finds the
                // same bar inside the age window, so without this identity check every scan pass would
                // re-notify an unchanged setup (up to 24 popups/day at the hourly schedule). A brand new
                // bullish CLV bar above EMA50 is strictly newer than the last alerted bar and therefore
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
                Ppo = res.Ppo,
                PpoSig = res.PpoSig,
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
            sb.AppendLine($"  EMA50 touch bar #{s.EmaTouchIndex} | Swing high: {s.SwingHigh:F4} | Swing low: {s.SwingLow:F4} | Trigger bar #{s.TriggerIndex}");
            sb.AppendLine($"  Close: {s.Close:F4} | CLV: {s.Clv:F2} | PPO: {s.Ppo:F2} vs sig {s.PpoSig:F2}");
            sb.AppendLine($"  EMA50: {s.Ema50:F4} | SMA200: {s.Sma200:F4} | ATR: {s.Atr:F4} | Live: {s.LivePrice:F4} | Dist to {Lookback}-bar level: {s.DistanceAtr:F2} ATR");
            sb.AppendLine($"  SPY gate: {_spyDetail}");
            sb.AppendLine($"  VIX long-block: {_vixDetail}");
            sb.AppendLine($"  BTC crypto gate: {_btcDetail}");
            sb.AppendLine($"  ENTRY: next open (scanner reports the setup only; no SL/PT computed)");
            string detail = sb.ToString();

            string shortMsg = $"[{now:HH:mm}] {symbolName} {dir} CONT | CLV {s.Clv:F2} PPO {s.Ppo:F2}/{s.PpoSig:F2} Dist {s.DistanceAtr:F2} ATR";
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
                int evalIdx = UseUsSessionBarLogic
                    ? ReversalEngine.GetLastCompletedDailyBarIndex(spyBars.OpenTimes[n - 1], n, Server.TimeInUtc)
                    : n - 1;
                if (evalIdx < 0) evalIdx = n - 1;

                double spyClose = spyBars.ClosePrices[evalIdx];
                int startIdx = evalIdx - BenchmarkSmaPeriod + 1;
                if (startIdx < 0) startIdx = 0;
                double sum = 0.0;
                int count = 0;
                for (int i = startIdx; i <= evalIdx; i++) { sum += spyBars.ClosePrices[i]; count++; }
                double spySma = count > 0 ? sum / count : double.NaN;

                if (double.IsNaN(spySma) || spySma <= 0.0)
                    return (true, true, spyClose, double.NaN, "SPY SMA unavailable — filter bypassed");

                bool longOk = spyClose > spySma;
                bool shortOk = spyClose < spySma;
                string detail = $"SPY {spyClose:F2} vs SMA{BenchmarkSmaPeriod} {spySma:F2} -> {(longOk ? "SPY > SMA50 (longs allowed, shorts blocked)" : shortOk ? "SPY < SMA50 (shorts allowed, longs blocked)" : "SPY at SMA50 (both blocked)")}";
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
        /// Computes the VIX long-block gate once per pass using the live VIX quote (bid/ask mid),
        /// falling back to the last completed daily VIX close. When the filter is disabled or VIX
        /// data is unavailable, the gate is bypassed (longs allowed). Shorts are never affected.
        /// Longs are blocked when liveVix strictly exceeds <see cref="MaxVixThreshold"/>.
        /// </summary>
        private (bool LongOk, double LiveVix, string Detail) CheckVixGate()
        {
            if (!RequireVixFilter)
                return (true, double.NaN, "VIX long-block disabled (bypassed)");

            try
            {
                // 1. Try live mid price from the resolved VIX symbol.
                double liveVix = GetLivePrice(_resolvedVixSymbol);
                if (!double.IsNaN(liveVix) && liveVix > 0)
                    return VixLongDecision(liveVix);

                // 2. Fallback: last completed daily VIX close.
                var vixBars = EnsureBarsLoaded(TimeFrame.Daily, _resolvedVixSymbol, 5);
                if (vixBars != null && vixBars.Count > 0)
                {
                    double closeVix = vixBars.ClosePrices.LastValue;
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
        /// Computes the crypto benchmark gate once per pass on the last completed BTC daily bar
        /// (24/7 market, no repaint): crypto longs require BTC close &gt; BTC_SMA, crypto shorts require
        /// BTC close &lt; BTC_SMA. Only symbols in the configured crypto list are affected. When the
        /// filter is disabled or BTC data is unavailable, the gate is bypassed (both sides allowed).
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

                // Crypto trades 24/7: while the market is open (the usual case) bar n-1 is still
                // forming and n-2 is the last completed daily bar (no repaint).
                var btcSymbol = Symbols.GetSymbol(_resolvedCryptoBenchmarkSymbol);
                bool isMarketOpen = btcSymbol != null && btcSymbol.MarketHours.IsOpened();
                int evalIdx = isMarketOpen ? (n - 2) : (n - 1);

                // Protect against an unformed future bar or a flat phantom rollover bar.
                if (evalIdx == n - 1 && n >= 2)
                {
                    if (btcBars.OpenTimes[n - 1] > Server.TimeInUtc ||
                        (btcBars.TickVolumes[n - 1] == 0 &&
                         btcBars.OpenPrices[n - 1] == btcBars.ClosePrices[n - 1] &&
                         btcBars.HighPrices[n - 1] == btcBars.LowPrices[n - 1]))
                    {
                        evalIdx = n - 2;
                    }
                }
                if (evalIdx < 0) evalIdx = n - 1;

                double btcClose = btcBars.ClosePrices[evalIdx];
                int startIdx = evalIdx - CryptoBenchmarkSmaPeriod + 1;
                if (startIdx < 0) startIdx = 0;
                double sum = 0.0;
                int count = 0;
                for (int i = startIdx; i <= evalIdx; i++) { sum += btcBars.ClosePrices[i]; count++; }
                double btcSma = count > 0 ? sum / count : double.NaN;

                if (double.IsNaN(btcSma) || btcSma <= 0.0)
                    return (true, true, btcClose, double.NaN, "BTC SMA unavailable — filter bypassed");

                bool longOk = btcClose > btcSma;
                bool shortOk = btcClose < btcSma;
                string detail = $"BTC {btcClose:F2} vs SMA{CryptoBenchmarkSmaPeriod} {btcSma:F2} -> {(longOk ? "BTC > SMA (crypto longs allowed, shorts blocked)" : shortOk ? "BTC < SMA (crypto shorts allowed, longs blocked)" : "BTC at SMA (both blocked)")}";
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
                    return bars.Count >= minUsable ? bars : null;
                }
                return bars;
            }
            catch
            {
                return null;
            }
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
                ? $"VIX {vixLiveStr} vs > {MaxVixThreshold:F1} | Longs {(_vixLongOk ? "OK" : "BLOCKED")} | Shorts OK"
                : "VIX long-block OFF";

            string btcStatus = RequireCryptoBenchmarkFilter
                ? $"BTC {_btcClose:F2} vs SMA{CryptoBenchmarkSmaPeriod} {_btcSma:F2} | Longs {(_btcLongOk ? "OK" : "BLOCKED")} | Shorts {(_btcShortOk ? "OK" : "BLOCKED")}"
                : "Crypto benchmark OFF";

            var sb = new StringBuilder();
            sb.AppendLine("=== CONTINUATION SCANNER (Daily, EOD signals, entry next open) ===");
            sb.AppendLine($"Watchlist: {WatchlistName} ({totalCount} symbols) | Trigger: {ScheduleMode} | Direction: {AllowedDirection}");
            sb.AppendLine($"EMA({EmaPeriod}) | SMA({Sma200Period}) | ATR({AtrPeriod}) | PPO({PpoFastPeriod},{PpoSlowPeriod},{PpoSignalPeriod}) | Lookback {Lookback}b | No RSI");
            sb.AppendLine($"Thresholds: CLV +-{ClvThreshold:F2} | Max dist to {Lookback}-bar level {(RequireMaxDistance ? $"{MaxDistanceAtr:F1}*ATR" : "OFF")} | No SL/PT");
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
                    sb.AppendLine($"[{dir}] {item.Symbol,-8} | {dateTag} | Live: {item.LivePrice:F4} ({pnlSign}{atrPnl:F2} ATR) | CLV: {item.Clv:F2} | PPO: {item.Ppo:F2}/{item.PpoSig:F2} | EMA50: {item.Ema50:F4} | SMA200: {item.Sma200:F4} | Dist: {item.DistanceAtr:F2} ATR");
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
