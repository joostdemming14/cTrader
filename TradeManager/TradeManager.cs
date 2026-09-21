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

        [Parameter("VIX Symbol", Group = "Macro Gate", DefaultValue = "VIX")]
        public string VixSymbol { get; set; } = "VIX";

        [Parameter("VIX Long Block Threshold", Group = "Macro Gate", DefaultValue = 25.0, MinValue = 1.0)]
        public double MaxVixThreshold { get; set; } = 25.0;

        [Parameter("Crypto Benchmark Symbol", Group = "Crypto Macro Gate", DefaultValue = "BTCUSD")]
        public string CryptoBenchmarkSymbol { get; set; } = "BTCUSD";

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

        [Parameter("PPO Fast (EMA)", Group = "PPO", DefaultValue = 16, MinValue = 2)]
        public int PpoFastPeriod { get; set; } = 16;

        [Parameter("PPO Slow (EMA)", Group = "PPO", DefaultValue = 32, MinValue = 5)]
        public int PpoSlowPeriod { get; set; } = 32;

        [Parameter("PPO Signal (EMA)", Group = "PPO", DefaultValue = 9, MinValue = 1)]
        public int PpoSignalPeriod { get; set; } = 9;

        [Parameter("ATR Period", Group = "PPO", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; } = 14;

        private readonly Dictionary<string, PositionExitState> _positionExitPending = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PpoCheckResult> _ppoCache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastProcessedCloseDate = DateTime.MinValue;
        private string _resolvedBenchmarkSymbol = "SPY.US";
        private string _resolvedVixSymbol = "VIX";
        private string _resolvedCryptoBenchmarkSymbol = "BTCUSD";
        private HashSet<string> _cryptoSymbols = new(StringComparer.OrdinalIgnoreCase);

        protected override void OnStart()
        {
            _resolvedBenchmarkSymbol = ResolveSymbolName(BenchmarkSymbol, "SPY.US", "SPY", "SPY.ETF");
            _resolvedVixSymbol = ResolveSymbolName(VixSymbol, "VIX", "VIXY.US", ".VIX", "VOLX", "VXX.US");
            _resolvedCryptoBenchmarkSymbol = ResolveSymbolName(CryptoBenchmarkSymbol, "BTCUSD", "BTCEUR", "BTCGBP", "XBTUSD");
            _cryptoSymbols = ParseCryptoSymbols(CryptoSymbolsCsv);
            Timer.Start(TimeSpan.FromSeconds(30));
            Print($"[TradeManager] Started. Daily close check at {DailyCloseHourEt:D2}:{DailyCloseMinuteEt:D2} ET. Crypto macro gate: {_resolvedCryptoBenchmarkSymbol}, {_cryptoSymbols.Count} symbols.");
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
            _ppoCache.Clear();
            var macro = ReadMacroGate();
            var cryptoMacro = ReadCryptoMacroGate();
            CancelOrdersForMacro(macro, cryptoMacro);

            foreach (var order in PendingOrders.ToArray())
                EvaluatePendingOrder(order, macro);

            foreach (var position in Positions.ToArray())
                DetectPositionPpoExit(position);

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

        private void EvaluatePendingOrder(PendingOrder order, MacroGate macro)
        {
            if (!PendingOrders.Any(candidate => candidate.Id == order.Id)) return;

            if (HasPendingTakeProfitBeenReached(order))
            {
                var result = CancelPendingOrder(order);
                Print($"[TradeManager] PT-cancel order {order.Id} {order.SymbolName}: {(result.IsSuccessful ? "cancelled" : result.Error.ToString())}.");
                return;
            }

            if (TryGetPpoCross(order.SymbolName, out bool crossedAgainstLong, out bool crossedAgainstShort, out _))
            {
                bool against = order.TradeType == TradeType.Buy ? crossedAgainstLong : crossedAgainstShort;
                if (against)
                {
                    var result = CancelPendingOrder(order);
                    Print($"[TradeManager] PPO-cancel order {order.Id} {order.SymbolName} {order.TradeType}: {(result.IsSuccessful ? "cancelled" : result.Error.ToString())}.");
                }
            }
        }

        private void DetectPositionPpoExit(Position position)
        {
            if (!TryGetPpoCross(position.SymbolName, out bool crossedAgainstLong, out bool crossedAgainstShort, out double atr))
                return;

            bool against = position.TradeType == TradeType.Buy ? crossedAgainstLong : crossedAgainstShort;
            if (!against) return;

            string key = $"{position.Id}:{position.SymbolName}";
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
                    Print($"[TradeManager] PPO exit skipped for position {position.Id} {position.SymbolName}: ATR unavailable.");
                    continue;
                }

                double spread = symbol.Ask - symbol.Bid;
                bool spreadAcceptable = spread >= 0.0 && spread <= MaxSpreadAtr * pending.Value.Atr;
                bool delayExpired = (Server.TimeInUtc - pending.Value.DetectedAt).TotalMinutes >= MaxExitDelayMinutes;
                if (!spreadAcceptable && !delayExpired) continue;

                var result = ClosePosition(position);
                Print($"[TradeManager] PPO-close position {position.Id} {position.SymbolName} {position.TradeType}: {(result.IsSuccessful ? "closed" : result.Error.ToString())}.");
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

        private bool TryGetPpoCross(string symbolName, out bool crossedAgainstLong, out bool crossedAgainstShort, out double atr)
        {
            if (_ppoCache.TryGetValue(symbolName, out var cached))
            {
                crossedAgainstLong = cached.CrossedAgainstLong;
                crossedAgainstShort = cached.CrossedAgainstShort;
                atr = cached.Atr;
                return cached.CrossedAgainstLong || cached.CrossedAgainstShort;
            }

            crossedAgainstLong = false;
            crossedAgainstShort = false;
            atr = double.NaN;

            var bars = LoadDailyBars(symbolName, 250);
            if (bars == null || bars.Count < PpoSlowPeriod + PpoSignalPeriod + AtrPeriod + 3)
            {
                Print($"[TradeManager] PPO check skipped for {symbolName}: insufficient daily bars.");
                _ppoCache[symbolName] = new PpoCheckResult(false, false, false, double.NaN);
                return false;
            }

            int last = bars.Count - 1;
            int eval = IsDailyBarForming(bars, last) ? last - 1 : last;
            if (eval < 2)
            {
                _ppoCache[symbolName] = new PpoCheckResult(false, false, false, double.NaN);
                return false;
            }

            var closes = new double[bars.Count];
            var highs = new double[bars.Count];
            var lows = new double[bars.Count];
            for (int i = 0; i < bars.Count; i++)
            {
                closes[i] = bars.ClosePrices[i];
                highs[i] = bars.HighPrices[i];
                lows[i] = bars.LowPrices[i];
            }

            var ppo = ReversalEngine.ComputePpo(closes, PpoFastPeriod, PpoSlowPeriod, PpoSignalPeriod);
            var atrValues = ReversalEngine.ComputeAtr(highs, lows, closes, AtrPeriod);
            double previous = ppo.Ppo[eval - 1];
            double previousSignal = ppo.PpoSig[eval - 1];
            double current = ppo.Ppo[eval];
            double currentSignal = ppo.PpoSig[eval];
            atr = atrValues[eval];
            if (double.IsNaN(previous) || double.IsNaN(previousSignal) || double.IsNaN(current) || double.IsNaN(currentSignal) || double.IsNaN(atr) || atr <= 0.0)
            {
                _ppoCache[symbolName] = new PpoCheckResult(false, false, false, atr);
                return false;
            }

            crossedAgainstLong = previous >= previousSignal && current < currentSignal;
            crossedAgainstShort = previous <= previousSignal && current > currentSignal;
            _ppoCache[symbolName] = new PpoCheckResult(true, crossedAgainstLong, crossedAgainstShort, atr);
            return crossedAgainstLong || crossedAgainstShort;
        }

        private MacroGate ReadMacroGate()
        {
            var spyBars = LoadDailyBars(_resolvedBenchmarkSymbol, BenchmarkSmaPeriod + 20);
            var vixBars = LoadDailyBars(_resolvedVixSymbol, 5);
            if (spyBars == null || vixBars == null) return MacroGate.Unavailable;

            int spyIndex = LastCompletedIndex(spyBars);
            int vixIndex = LastCompletedIndex(vixBars);
            if (spyIndex < BenchmarkSmaPeriod - 1 || vixIndex < 0) return MacroGate.Unavailable;

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
            double vixClose = vixBars.ClosePrices[vixIndex];
            if (double.IsNaN(spyAtr) || spyAtr <= 0.0) return MacroGate.Unavailable;
            double buffer = BenchmarkBufferAtr * spyAtr;
            bool vixBlocksLongs = vixClose > MaxVixThreshold;
            bool spyBelowBuffer = spyClose < spySma - buffer;
            bool spyAboveBuffer = spyClose > spySma + buffer;
            return new MacroGate(true, !(spyBelowBuffer || vixBlocksLongs), !spyAboveBuffer, $"SPY {spyClose:F2} vs SMA {spySma:F2} +/- {BenchmarkBufferAtr:F1} ATR ({spyAtr:F2}); VIX {vixClose:F2}");
        }

        private MacroGate ReadCryptoMacroGate()
        {
            var btcBars = LoadDailyBars(_resolvedCryptoBenchmarkSymbol, CryptoBenchmarkSmaPeriod + 20);
            if (btcBars == null) return MacroGate.Unavailable;

            int btcIndex = LastCompletedIndex(btcBars);
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

        private int LastCompletedIndex(Bars bars)
        {
            if (bars.Count == 0) return -1;
            int last = bars.Count - 1;
            return IsDailyBarForming(bars, last) ? last - 1 : last;
        }

        private bool IsDailyBarForming(Bars bars, int index)
        {
            var symbol = Symbols.GetSymbol(bars.SymbolName);
            if (symbol == null || symbol.MarketHours.IsOpened()) return true;
            return bars.OpenTimes[index] > Server.TimeInUtc;
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

        private readonly struct PpoCheckResult
        {
            public PpoCheckResult(bool hasUsableData, bool crossedAgainstLong, bool crossedAgainstShort, double atr)
            {
                HasUsableData = hasUsableData;
                CrossedAgainstLong = crossedAgainstLong;
                CrossedAgainstShort = crossedAgainstShort;
                Atr = atr;
            }

            public bool HasUsableData { get; }
            public bool CrossedAgainstLong { get; }
            public bool CrossedAgainstShort { get; }
            public double Atr { get; }
        }
    }
}