using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo
{
    /// <summary>
    /// Account-wide manager for pending orders and positions. It evaluates once per UTC day
    /// after the configured daily close hour, never modifies orders or position protection.
    /// TSI zero-line exits are cross-based and tracked from the order/position creation time:
    /// a position entered below the zero line is only closed after TSI first crossed above it
    /// and then crossed back against the position, so a cBot restart never misses an earlier
    /// cross, and a pending exit is disarmed when the regime recovers first.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, TimeZone = TimeZones.UTC)]
    public class TradeManager : Robot
    {
        private const string DefaultCryptoSymbolsCsv = "BTCUSD,BTCGBP,BTCEUR,BTCAUD,ETHUSD,ETHGBP,ETHEUR,ETHAUD,LTCUSD,TRUMPUSD,AAVUSD,ATMUSD,FLOUSD,JUPUSD,NERUSD,ONDUSD,PEPUSD,SHBUSD,TRXUSD,WIFUSD,ARBUSD,BNKUSD,MANUSD,SANUSD,POLUSD,SONUSD,HBARUSD,SUIUSD,TONUSD,APTUSD,HYPEUSD,INJUSD,RENDERUSD,FETUSD,XAUTUSD,PAXGUSD,DOTUSD,LINKUSD,XLMUSD,XRPUSD,UNIUSD,DOGEUSD,ADAUSD,BCHUSD,BNBUSD,XTZUSD,SOLUSD,AVAXUSD,COMPUSD,ETCUSD,GLMRUSD,KSMUSD";

        [Parameter("Benchmark Symbol", Group = "Macro Gate", DefaultValue = "SPY.US")]
        public string BenchmarkSymbol { get; set; } = "SPY.US";

        [Parameter("Benchmark SMA Period", Group = "Macro Gate", DefaultValue = 50, MinValue = 10)]
        public int BenchmarkSmaPeriod { get; set; } = 50;

        [Parameter("Benchmark Buffer (x ATR)", Group = "Macro Gate", DefaultValue = 0.5, MinValue = 0.0, MaxValue = 2.0, Step = 0.1)]
        public double BenchmarkBufferAtr { get; set; } = 0.5;

        [Parameter("Require Benchmark Filter (SPY vs SMA)", Group = "Macro Gate", DefaultValue = false)]
        public bool RequireBenchmarkFilter { get; set; } = false;

        [Parameter("Crypto Benchmark Symbol", Group = "Crypto Macro Gate", DefaultValue = "BTCUSD")]
        public string CryptoBenchmarkSymbol { get; set; } = "BTCUSD";

        [Parameter("Require Crypto Benchmark Filter (BTC vs SMA)", Group = "Crypto Macro Gate", DefaultValue = false)]
        public bool RequireCryptoBenchmarkFilter { get; set; } = false;

        [Parameter("Crypto Benchmark SMA Period", Group = "Crypto Macro Gate", DefaultValue = 50, MinValue = 10)]
        public int CryptoBenchmarkSmaPeriod { get; set; } = 50;

        [Parameter("Crypto Symbols (comma separated)", Group = "Crypto Macro Gate", DefaultValue = DefaultCryptoSymbolsCsv)]
        public string CryptoSymbolsCsv { get; set; } = DefaultCryptoSymbolsCsv;

        [Parameter("Daily Close Hour (ET)", Group = "Schedule", DefaultValue = 16, MinValue = 0, MaxValue = 23)]
        public int DailyCloseHourEt { get; set; } = 16;

        [Parameter("Daily Close Minute (ET)", Group = "Schedule", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int DailyCloseMinuteEt { get; set; } = 0;

        [Parameter("Max Exit Delay (minutes)", Group = "Position Exit", DefaultValue = 30, MinValue = 1, MaxValue = 240)]
        public int MaxExitDelayMinutes { get; set; } = 30;

        [Parameter("Spread Max (x ATR)", Group = "Position Exit", DefaultValue = 0.05, MinValue = 0.001, MaxValue = 0.5, Step = 0.001)]
        public double MaxSpreadAtr { get; set; } = 0.05;

        [Parameter("TSI Long (EMA)", Group = "TSI", DefaultValue = 25, MinValue = 2)]
        public int TsiLongPeriod { get; set; } = 25;

        [Parameter("TSI Short (EMA)", Group = "TSI", DefaultValue = 13, MinValue = 2)]
        public int TsiShortPeriod { get; set; } = 13;

        [Parameter("TSI Signal (EMA)", Group = "TSI", DefaultValue = 13, MinValue = 1)]
        public int TsiSignalPeriod { get; set; } = 13;

        [Parameter("ATR Period", Group = "TSI", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; } = 14;

        private readonly Dictionary<string, PositionExitState> _positionExitPending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TsiSeries?> _tsiCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastProcessedCloseDate = DateTime.MinValue;
        private string _resolvedBenchmarkSymbol = "SPY.US";
        private string _resolvedCryptoBenchmarkSymbol = "BTCUSD";
        private HashSet<string> _cryptoSymbols = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnStart()
        {
            _resolvedBenchmarkSymbol = ResolveSymbolName(BenchmarkSymbol, "SPY.US", "SPY", "SPY.ETF");
            _resolvedCryptoBenchmarkSymbol = ResolveSymbolName(CryptoBenchmarkSymbol, "BTCUSD", "BTCEUR", "BTCGBP", "XBTUSD");
            _cryptoSymbols = ParseCryptoSymbols(CryptoSymbolsCsv);
            Timer.Start(TimeSpan.FromSeconds(30));
            Print($"[TradeManager] Started. Daily close check at {DailyCloseHourEt:D2}:{DailyCloseMinuteEt:D2} ET. SPY macro gate {(RequireBenchmarkFilter ? "ON" : "OFF")} | BTC macro gate {(RequireCryptoBenchmarkFilter ? $"ON ({_resolvedCryptoBenchmarkSymbol}, {_cryptoSymbols.Count} symbols)" : "OFF")} | TSI exits: zero-line cross against the direction, tracked from order/position creation.");
        }

        protected override void OnTimer()
        {
            DateTime now = Server.TimeInUtc;
            DateTime nowEt = ReversalEngine.ConvertUtcToEt(now);
            DateTime closeTimeEt = nowEt.Date.AddHours(DailyCloseHourEt).AddMinutes(DailyCloseMinuteEt);
            if (nowEt >= closeTimeEt && _lastProcessedCloseDate != nowEt.Date)
            {
                ProcessDailyClose(nowEt.Date);
                _lastProcessedCloseDate = nowEt.Date;
            }

            MonitorPendingPositionExits();
        }

        protected override void OnStop()
        {
            Timer.Stop();
        }

        private void ProcessDailyClose(DateTime closeDate)
        {
            _tsiCache.Clear();
            // Macro gates default OFF: the per-symbol TSI zero-line cross is the direction-aware
            // cancel/exit trigger; index gates are opt-in, matching the scanner defaults.
            var macro = RequireBenchmarkFilter ? ReadMacroGate() : MacroGate.Bypassed;
            var cryptoMacro = RequireCryptoBenchmarkFilter ? ReadCryptoMacroGate() : MacroGate.Bypassed;
            CancelOrdersForMacro(macro, cryptoMacro);

            foreach (var order in PendingOrders.ToArray())
                EvaluatePendingOrder(order);

            foreach (var position in Positions.ToArray())
                DetectPositionTsiExit(position);

            Print($"[TradeManager] Daily close processed: {closeDate:yyyy-MM-dd}. Orders={PendingOrders.Count}, Positions={Positions.Count}.");
        }

        private void CancelOrdersForMacro(MacroGate macro, MacroGate cryptoMacro)
        {
            foreach (var order in PendingOrders.ToArray())
            {
                MacroGate applicableGate = IsCryptoSymbol(order.SymbolName)
                    ? cryptoMacro
                    : IsUsEquitySymbol(order.SymbolName) ? macro : MacroGate.Bypassed;
                if (!applicableGate.Available) continue;

                bool cancel = order.TradeType == TradeType.Buy
                    ? !applicableGate.BuyAllowed
                    : !applicableGate.SellAllowed;
                if (!cancel) continue;

                var result = CancelPendingOrder(order);
                Print($"[TradeManager] Macro-cancel order {order.Id} {order.SymbolName} {order.TradeType}: {(result.IsSuccessful ? "cancelled" : result.Error.ToString())}.");
            }
        }

        private void EvaluatePendingOrder(PendingOrder order)
        {
            if (!PendingOrders.Any(candidate => candidate.Id == order.Id)) return;

            if (HasPendingTakeProfitBeenReached(order))
            {
                var result = CancelPendingOrder(order);
                Print($"[TradeManager] PT-cancel order {order.Id} {order.SymbolName}: {(result.IsSuccessful ? "cancelled" : result.Error.ToString())}.");
                return;
            }

            var crossState = EvaluateTsiCross(order.SymbolName, order.SubmittedTime, out _);
            bool against = order.TradeType == TradeType.Buy
                ? crossState == TsiCrossState.AgainstLong
                : crossState == TsiCrossState.AgainstShort;
            if (against)
            {
                var result = CancelPendingOrder(order);
                Print($"[TradeManager] TSI-cancel order {order.Id} {order.SymbolName} {order.TradeType}: {(result.IsSuccessful ? "cancelled" : result.Error.ToString())}.");
            }
        }

        private void DetectPositionTsiExit(Position position)
        {
            string key = $"{position.Id}:{position.SymbolName}";
            var crossState = EvaluateTsiCross(position.SymbolName, position.EntryTime, out double atr);

            if (crossState == TsiCrossState.NoData)
            {
                // Series unavailable this pass: leave any pending exit armed and retry next pass.
                return;
            }

            bool against = position.TradeType == TradeType.Buy
                ? crossState == TsiCrossState.AgainstLong
                : crossState == TsiCrossState.AgainstShort;
            if (!against)
            {
                // No adverse cross since entry, or the regime recovered after an earlier one:
                // disarm an exit that has not executed yet.
                if (_positionExitPending.Remove(key))
                    Print($"[TradeManager] TSI exit disarmed for position {position.Id} {position.SymbolName}: regime no longer against the position.");
                return;
            }

            if (!_positionExitPending.ContainsKey(key))
                _positionExitPending[key] = new PositionExitState(Server.TimeInUtc, atr);
        }

        private void MonitorPendingPositionExits()
        {
            if (_positionExitPending.Count == 0) return;

            var positionsByKey = Positions.ToArray()
                .ToDictionary(position => $"{position.Id}:{position.SymbolName}", StringComparer.OrdinalIgnoreCase);

            foreach (var pending in _positionExitPending.ToArray())
            {
                if (!positionsByKey.TryGetValue(pending.Key, out var position))
                {
                    _positionExitPending.Remove(pending.Key);
                    continue;
                }

                var symbol = Symbols.GetSymbol(position.SymbolName);
                if (symbol == null || !symbol.MarketHours.IsOpened())
                    continue;

                if (pending.Value.Atr <= 0.0 || double.IsNaN(pending.Value.Atr))
                {
                    Print($"[TradeManager] TSI exit skipped for position {position.Id} {position.SymbolName}: ATR unavailable.");
                    continue;
                }

                double spread = symbol.Ask - symbol.Bid;
                bool spreadAcceptable = spread >= 0.0 && spread <= MaxSpreadAtr * pending.Value.Atr;
                bool delayExpired = (Server.TimeInUtc - pending.Value.DetectedAt).TotalMinutes >= MaxExitDelayMinutes;
                if (!spreadAcceptable && !delayExpired) continue;

                var result = ClosePosition(position);
                Print($"[TradeManager] TSI-close position {position.Id} {position.SymbolName} {position.TradeType}: {(result.IsSuccessful ? "closed" : result.Error.ToString())}.");
                if (result.IsSuccessful)
                    _positionExitPending.Remove(pending.Key);
            }
        }

        private bool HasPendingTakeProfitBeenReached(PendingOrder order)
        {
            if (!order.TakeProfit.HasValue) return false;
            var symbol = Symbols.GetSymbol(order.SymbolName);
            if (symbol == null) return false;
            return order.TradeType == TradeType.Buy
                ? symbol.Bid >= order.TakeProfit.Value
                : symbol.Ask <= order.TakeProfit.Value;
        }

        /// <summary>
        /// Outcome of the TSI zero-line evaluation for one order/position since it was created.
        /// </summary>
        private enum TsiCrossState
        {
            /// <summary>Daily series unavailable this pass; the check is retried next pass.</summary>
            NoData,

            /// <summary>No zero-line cross since the order/position was created.</summary>
            NoCross,

            /// <summary>The most recent cross was bearish (TSI fell through zero) — against longs.</summary>
            AgainstLong,

            /// <summary>The most recent cross was bullish (TSI rose through zero) — against shorts.</summary>
            AgainstShort
        }

        /// <summary>
        /// Finds the most recent TSI zero-line cross on completed daily bars since <paramref name="sinceUtc"/>
        /// (order creation / position entry). Only the newest cross decides: a bearish cross followed by a
        /// recovery (bullish cross) leaves the position alone, and a reversal entry below the zero line can
        /// only be closed after TSI first crossed above it and then crossed back down. Because the scan runs
        /// from the creation time forward, a cBot restart never misses a cross from earlier days.
        /// </summary>
        private TsiCrossState EvaluateTsiCross(string symbolName, DateTime sinceUtc, out double atr)
        {
            atr = double.NaN;
            var series = GetTsiSeries(symbolName);
            if (series == null)
                return TsiCrossState.NoData;

            int eval = series.EvalIndex;
            if (eval < 1)
                return TsiCrossState.NoData;

            atr = series.Atr[eval];
            if (double.IsNaN(atr) || atr <= 0.0)
            {
                atr = double.NaN;
                return TsiCrossState.NoData;
            }

            // Only completed bars whose close printed after the order/position existed count:
            // when even the newest completed bar closed before the creation time there is no
            // qualifying bar yet (e.g. an intraday entry on a 24/7 instrument, a weekend order
            // or a pass that runs before the configured close hour), so a cross that printed
            // before the order/position existed can never arm an exit or a cancel.
            if (ReversalEngine.GetDailyBarCloseTimeUtc(series.OpenTimes[eval]) <= sinceUtc)
                return TsiCrossState.NoCross;

            int first = eval;
            while (first > 1 && ReversalEngine.GetDailyBarCloseTimeUtc(series.OpenTimes[first - 1]) > sinceUtc)
                first--;

            for (int i = eval; i >= first; i--)
            {
                double prev = series.Tsi[i - 1];
                double cur = series.Tsi[i];
                if (double.IsNaN(prev) || double.IsNaN(cur))
                    continue;
                if (prev > 0.0 && cur <= 0.0) return TsiCrossState.AgainstLong;
                if (prev < 0.0 && cur >= 0.0) return TsiCrossState.AgainstShort;
                // No sign change at this bar; keep scanning backwards for the newest cross.
            }
            return TsiCrossState.NoCross;
        }

        private TsiSeries? GetTsiSeries(string symbolName)
        {
            if (_tsiCache.TryGetValue(symbolName, out var cached))
                return cached;
            cached = LoadTsiSeries(symbolName);
            _tsiCache[symbolName] = cached;
            return cached;
        }

        private TsiSeries? LoadTsiSeries(string symbolName)
        {
            var bars = LoadDailyBars(symbolName, 250);
            if (bars == null || bars.Count < TsiLongPeriod + TsiShortPeriod + TsiSignalPeriod + AtrPeriod + 3)
            {
                Print($"[TradeManager] TSI series unavailable for {symbolName}: insufficient daily bars.");
                return null;
            }

            int eval = LastCompletedIndex(bars);
            if (eval < 2)
                return null;

            var closes = new double[bars.Count];
            var highs = new double[bars.Count];
            var lows = new double[bars.Count];
            var openTimes = new DateTime[bars.Count];
            for (int i = 0; i < bars.Count; i++)
            {
                closes[i] = bars.ClosePrices[i];
                highs[i] = bars.HighPrices[i];
                lows[i] = bars.LowPrices[i];
                openTimes[i] = bars.OpenTimes[i];
            }

            var tsi = ReversalEngine.ComputeTsi(closes, TsiLongPeriod, TsiShortPeriod, TsiSignalPeriod);
            var atrValues = ReversalEngine.ComputeAtr(highs, lows, closes, AtrPeriod);
            if (double.IsNaN(tsi.Tsi[eval]))
                return null;

            return new TsiSeries
            {
                Tsi = tsi.Tsi,
                Atr = atrValues,
                OpenTimes = openTimes,
                EvalIndex = eval
            };
        }

        private MacroGate ReadMacroGate()
        {
            var spyBars = LoadDailyBars(_resolvedBenchmarkSymbol, BenchmarkSmaPeriod + 20);
            if (spyBars == null) return MacroGate.Unavailable;

            // The SPY band is a US-session macro gate: select its bar with the US cash session model
            // even when the broker lists the symbol without the '.US' suffix.
            int spyIndex = LastCompletedIndex(spyBars, true);
            if (spyIndex < BenchmarkSmaPeriod - 1) return MacroGate.Unavailable;

            double spyClose = spyBars.ClosePrices[spyIndex];
            double spySma = 0.0;
            for (int i = spyIndex - BenchmarkSmaPeriod + 1; i <= spyIndex; i++) spySma += spyBars.ClosePrices[i];
            spySma /= BenchmarkSmaPeriod;
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
            double spyAtr = spyAtrValues[spyIndex];
            if (double.IsNaN(spyAtr) || spyAtr <= 0.0) return MacroGate.Unavailable;
            double buffer = BenchmarkBufferAtr * spyAtr;
            bool spyBelowBuffer = spyClose < spySma - buffer;
            bool spyAboveBuffer = spyClose > spySma + buffer;
            return new MacroGate(true, !spyBelowBuffer, !spyAboveBuffer, $"SPY {spyClose:F2} vs SMA {spySma:F2} +/- {BenchmarkBufferAtr:F1} ATR ({spyAtr:F2})");
        }

        private MacroGate ReadCryptoMacroGate()
        {
            var btcBars = LoadDailyBars(_resolvedCryptoBenchmarkSymbol, CryptoBenchmarkSmaPeriod + 20);
            if (btcBars == null) return MacroGate.Unavailable;

            // BTC trades 24/7: the 24h window rule applies, never the US session model.
            int btcIndex = LastCompletedIndex(btcBars, false);
            if (btcIndex < CryptoBenchmarkSmaPeriod - 1) return MacroGate.Unavailable;

            double btcSma = 0.0;
            for (int i = btcIndex - CryptoBenchmarkSmaPeriod + 1; i <= btcIndex; i++) btcSma += btcBars.ClosePrices[i];
            btcSma /= CryptoBenchmarkSmaPeriod;

            var btcHighs = new double[btcBars.Count];
            var btcLows = new double[btcBars.Count];
            var btcCloses = new double[btcBars.Count];
            for (int i = 0; i < btcBars.Count; i++)
            {
                btcHighs[i] = btcBars.HighPrices[i];
                btcLows[i] = btcBars.LowPrices[i];
                btcCloses[i] = btcBars.ClosePrices[i];
            }

            double btcAtr = ReversalEngine.ComputeAtr(btcHighs, btcLows, btcCloses, AtrPeriod)[btcIndex];
            if (double.IsNaN(btcSma) || btcSma <= 0.0 || double.IsNaN(btcAtr) || btcAtr <= 0.0)
                return MacroGate.Unavailable;

            var btcSymbol = Symbols.GetSymbol(_resolvedCryptoBenchmarkSymbol);
            double btcClose = btcSymbol?.Bid ?? btcBars.ClosePrices[btcIndex];
            if (double.IsNaN(btcClose) || btcClose <= 0.0) btcClose = btcBars.ClosePrices[btcIndex];

            double buffer = BenchmarkBufferAtr * btcAtr;
            bool belowBuffer = btcClose < btcSma - buffer;
            bool aboveBuffer = btcClose > btcSma + buffer;
            return new MacroGate(true, !belowBuffer, !aboveBuffer,
                $"BTC {btcClose:F2} vs SMA {btcSma:F2} +/- {BenchmarkBufferAtr:F1} ATR ({btcAtr:F2})");
        }

        private HashSet<string> ParseCryptoSymbols(string csv)
        {
            var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(csv)) return symbols;

            foreach (string part in csv.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string raw = part.Trim();
                if (raw.Length == 0) continue;
                symbols.Add(raw);
                symbols.Add(raw.Replace(" ", "").Replace("\t", ""));
            }
            return symbols;
        }

        private bool IsCryptoSymbol(string symbolName)
        {
            if (string.IsNullOrWhiteSpace(symbolName)) return false;
            return _cryptoSymbols.Contains(symbolName) || _cryptoSymbols.Contains(symbolName.Replace(" ", ""));
        }

        private static bool IsUsEquitySymbol(string symbolName)
        {
            return !string.IsNullOrWhiteSpace(symbolName) && symbolName.EndsWith(".US", StringComparison.OrdinalIgnoreCase);
        }

        private Bars? LoadDailyBars(string symbolName, int minimumBars)
        {
            try
            {
                var bars = MarketData.GetBars(TimeFrame.Daily, symbolName);
                if (bars == null) return null;
                int attempts = 0;
                while (bars.Count < minimumBars && attempts++ < 3)
                {
                    if (bars.LoadMoreHistory() <= 0) break;
                }
                return bars;
            }
            catch { return null; }
        }

        /// <summary>
        /// Index of the last completed daily bar of the given series, using the session model of the
        /// symbol name ('.US' = US cash session, everything else = 24h window).
        /// </summary>
        private int LastCompletedIndex(Bars bars)
        {
            return LastCompletedIndex(bars, IsUsEquitySymbol(bars.SymbolName));
        }

        /// <summary>
        /// Index of the last completed daily bar (no repaint) with an explicit session model. The
        /// selection is shared with the scanners (<see cref="ReversalEngine.GetLastCompletedDailyBarIndex"/>)
        /// so both always evaluate the same bar:
        ///   * US cash equities (<paramref name="usCashEquity"/>): completed at their 16:00 ET session
        ///     close, derived from the bar's own open time, independent of the broker's daily bar
        ///     stamping convention;
        ///   * every other instrument (FX, metals, commodities, indices, crypto): completed once the
        ///     bar's own 24h window has elapsed.
        /// Untouched bars (no ticks, no range) that the broker pre-created for the next session are
        /// skipped, and a bar that is still forming is never evaluated - so the TSI zero-line check can
        /// no longer run one day behind.
        /// </summary>
        private int LastCompletedIndex(Bars bars, bool usCashEquity)
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

        private string ResolveSymbolName(string preferred, params string[] fallbacks)
        {
            foreach (string candidate in new[] { preferred }.Concat(fallbacks))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try { if (Symbols.GetSymbol(candidate) != null) return candidate; } catch { }
            }
            return preferred;
        }

        private readonly struct MacroGate
        {
            public static MacroGate Unavailable => new(false, false, false, "Macro data unavailable");
            public static MacroGate Bypassed => new(true, true, true, "Macro gate not applicable");
            public MacroGate(bool available, bool buyAllowed, bool sellAllowed, string detail)
            {
                Available = available;
                BuyAllowed = buyAllowed;
                SellAllowed = sellAllowed;
                Detail = detail;
            }
            public bool Available { get; }
            public bool BuyAllowed { get; }
            public bool SellAllowed { get; }
            public string Detail { get; }
        }

        private readonly struct PositionExitState
        {
            public PositionExitState(DateTime detectedAt, double atr)
            {
                DetectedAt = detectedAt;
                Atr = atr;
            }

            public DateTime DetectedAt { get; }
            public double Atr { get; }
        }

        /// <summary>Cached per-symbol daily TSI/ATR series for one daily-close pass.</summary>
        private sealed class TsiSeries
        {
            public double[] Tsi = Array.Empty<double>();
            public double[] Atr = Array.Empty<double>();
            public DateTime[] OpenTimes = Array.Empty<DateTime>();
            public int EvalIndex = -1;
        }
    }
}