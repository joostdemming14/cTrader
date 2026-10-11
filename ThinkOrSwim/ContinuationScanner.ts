#
# ContinuationScanner — thinkScript port of the cTrader ContinuationScanner
# (ContinuationScanner/ContinuationEngine.cs + ReversalEngine.cs divergence rule).
# For the thinkorswim Stock Hacker / Market Scanner. Use Daily aggregation
# (the cTrader scanner evaluates completed DAILY bars only).
#
# 1:1 signal logic with the cTrader defaults:
#   LONG continuation:
#     - signal bar touched EMA21 (Low <= EMA21)
#     - EMA21 > EMA50 (trend alignment)
#     - Close > SMA200 (long-term trend filter)
#     - CLV >= 0 only when the CLV veto is ON (default OFF)
#     - TSI > 0 (momentum regime) and TSI >= SMA(TSI, 5) (momentum flat or rising)
#     - no active bearish rolling price/TSI divergence (lookback 90, min gap 5,
#       TSI extreme 10, min TSI drop 3, near-extreme margin 1 ATR, window 5)
#     - confirmation ON by default: the confirmation bar closes above BOTH the
#       signal-bar high and EMA21
#   SHORT continuation: exact mirror.
#
# Platform notes (differences vs the cTrader bot, by necessity):
#   - "assumeMarketClosed": with Daily aggregation bar 0 is today's FORMING bar
#     during market hours. Yes -> bar 0 counts as the closed confirmation bar
#     (run the scan after 16:00 ET). No  -> everything shifts one bar back so
#     only fully closed bars are evaluated (no repaint).
#   - The VIX long block (default ON in cTrader, US equities) uses close("VIX").
#   - The SPY benchmark gate (default OFF) uses close("SPY") vs its SMA50 with
#     a 0.5 x ATR buffer.
#   - The crypto benchmark (BTC vs SMA, default OFF) has no Stock Hacker
#     equivalent and is intentionally omitted.
#   - RS rank tags, scheduling/HUD/alert plumbing are bot features, not scan
#     conditions, and do not apply to a scanner.
#   - thinkScript ExpAverage/TSI are defined from bar 0; the cTrader engine
#     NaN-guards its warmup. The SMA200 term provides the warmup gate.
#   - Trigger window is capped at 5 (the cTrader default): thinkScript needs
#     fixed candidate offsets.
#
# Setup: Stock Hacker -> add study filter -> this script, "LongSignal is true"
# (and a second scan/filter for "ShortSignal is true").
#

input assumeMarketClosed = yes;      # bar 0 is a CLOSED bar (run after 16:00 ET)
input requireConfirmation = yes;     # next-bar confirmation (cTrader default: ON)
input emaPeriod = 21;
input trendEmaPeriod = 50;
input sma200Period = 200;
input atrPeriod = 14;
input tsiLongPeriod = 25;
input tsiShortPeriod = 13;
input tsiSignalPeriod = 13;          # display-only in cTrader; kept for parity
input tsiMomentumPeriod = 5;
input requireClvVeto = no;           # cTrader default: OFF
input divergenceLookback = 90;
input divergenceMinGap = 5;
input divergenceTsiExtremeLevel = 10.0;
input divergenceMinTsiDrop = 3.0;
input divergenceTriggerWindow = 5;   # 0..5 (fixed candidates in thinkScript)
input divergenceNearExtremeAtr = 1.0;
input requireVixFilter = yes;        # cTrader default: ON (blocks longs when VIX > threshold)
input maxVixThreshold = 25.0;
input requireBenchmarkFilter = no;  # cTrader default: OFF
input benchmarkSymbol = "SPY";
input benchmarkSmaPeriod = 50;
input benchmarkBufferAtr = 0.5;

# ---------------- indicator math (Wilder ATR, Blau TSI) ----------------
def clv = if (high - low) > 0 then (2 * close - high - low) / (high - low) else 0;
def ema21 = ExpAverage(close, emaPeriod);
def ema50 = ExpAverage(close, trendEmaPeriod);
def sma200 = Average(close, sma200Period);
def atr = MovingAverage(AverageType.WILDERS, TrueRange(high, close, low), atrPeriod);

def pc = close - close[1];
def tsiNum = ExpAverage(ExpAverage(pc, tsiLongPeriod), tsiShortPeriod);
def tsiDen = ExpAverage(ExpAverage(AbsValue(pc), tsiLongPeriod), tsiShortPeriod);
def tsi = if tsiDen > 0.000000000001 then 100 * tsiNum / tsiDen else Double.NaN;
def tsiSigLine = ExpAverage(tsi, tsiSignalPeriod);   # display-only, never a trigger
def tsiAvg = Average(tsi, tsiMomentumPeriod);

# ---------------- shared rolling price/TSI divergence ----------------
# Exact port of ReversalEngine.FindRollingBearish/BullishDivergence for ONE
# candidate bar at offset c (counted back from the current bar; the signal bar
# sits at offset sig). A candidate qualifies when its High is at or above the
# highest High of the trailing window [c+minGap .. c+lookback] (fresh extreme)
# or within nearAtr x ATR of it, with TSI[c] <= TSI at the previous window
# extreme and TSI[c] <= reference TSI - minDrop, where the strong reference is
# the highest High of the window with TSI > level, and no higher High printed
# between the candidate and the signal bar. "bull" is the mirror for lows.
script TsiDivergence {
    input h = high;
    input l = low;
    input t = close;
    input atrS = close;
    input c = 1;            # candidate bar offset (bars back)
    input sig = 1;          # signal bar offset
    input lookback = 90;
    input minGap = 5;
    input tsiLevel = 10.0;
    input minDrop = 3.0;
    input nearAtr = 1.0;

    def nPrev = lookback;                    # window [c+1 .. c+lookback]
    def nRef = lookback - minGap + 1;         # window [c+minGap .. c+lookback]

    # index (0-based inside the window) of the previous window extreme
    def pHi = fold k = 1 to nPrev with p = 0 do if GetValue(h, c + 1 + k) > GetValue(h, c + 1 + p) then k else p;
    def pLo = fold k = 1 to nPrev with p = 0 do if GetValue(l, c + 1 + k) < GetValue(l, c + 1 + p) then k else p;
    def pHiVal = GetValue(h, c + 1 + pHi);
    def pLoVal = GetValue(l, c + 1 + pLo);
    def pHiTsi = GetValue(t, c + 1 + pHi);
    def pLoTsi = GetValue(t, c + 1 + pLo);

    # strong references (0-based window index + 1, so 0 = none qualifies):
    # highest High with TSI > level / lowest Low with TSI < -level
    def rHi = fold k = 0 to nRef with r = 0 do if GetValue(t, c + minGap + k) > tsiLevel and (r == 0 or GetValue(h, c + minGap + k) > GetValue(h, c + minGap + r - 1)) then k + 1 else r;
    def rLo = fold k = 0 to nRef with r = 0 do if GetValue(t, c + minGap + k) < -tsiLevel and (r == 0 or GetValue(l, c + minGap + k) < GetValue(l, c + minGap + r - 1)) then k + 1 else r;
    def rHiTsi = if rHi > 0 then GetValue(t, c + minGap + rHi - 1) else Double.NaN;
    def rLoTsi = if rLo > 0 then GetValue(t, c + minGap + rLo - 1) else Double.NaN;

    # stale: a higher High / lower Low printed between the candidate and the signal bar
    def nStale = c - sig;
    def staleHi = if nStale <= 0 then 0 else fold s = 0 to nStale with st = 0 do if GetValue(h, sig + s) > GetValue(h, c) then 1 else st;
    def staleLo = if nStale <= 0 then 0 else fold s = 0 to nStale with st = 0 do if GetValue(l, sig + s) < GetValue(l, c) then 1 else st;

    def atrC = GetValue(atrS, c);
    def tC = GetValue(t, c);
    def hC = GetValue(h, c);
    def lC = GetValue(l, c);

    def validHi = !IsNaN(tC) and !IsNaN(pHiTsi) and rHi > 0 and !IsNaN(rHiTsi) and !IsNaN(atrC) and atrC > 0;
    def validLo = !IsNaN(tC) and !IsNaN(pLoTsi) and rLo > 0 and !IsNaN(rLoTsi) and !IsNaN(atrC) and atrC > 0;

    def freshHi = hC >= pHiVal;
    def nearHi = hC >= pHiVal - nearAtr * atrC;
    def freshLo = lC <= pLoVal;
    def nearLo = lC <= pLoVal + nearAtr * atrC;

    plot bear = validHi and staleHi == 0 and (freshHi or nearHi) and tC <= pHiTsi and tC <= rHiTsi - minDrop;
    plot bull = validLo and staleLo == 0 and (freshLo or nearLo) and tC >= pLoTsi and tC >= rLoTsi + minDrop;
}

# divergence candidates: the signal bar itself plus the previous
# (divergenceTriggerWindow) bars — cTrader: d in [signal - window, signal]
def lastClosed = if assumeMarketClosed then 0 else 1;
def sig = if requireConfirmation then lastClosed + 1 else lastClosed;

def div0 = TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 0, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr);
def div1 = if divergenceTriggerWindow >= 1 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 1, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div0;
def div2 = if divergenceTriggerWindow >= 2 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 2, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div1;
def div3 = if divergenceTriggerWindow >= 3 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 3, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div2;
def div4 = if divergenceTriggerWindow >= 4 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 4, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div3;
def div5 = if divergenceTriggerWindow >= 5 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = sig + 5, sig = sig, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div4;

def bearDivActive = div0.bear or div1.bear or div2.bear or div3.bear or div4.bear or div5.bear;
def bullDivActive = div0.bull or div1.bull or div2.bull or div3.bull or div4.bull or div5.bull;

# ---------------- gates (computed once per pass in cTrader) ----------------
def vixClose = close("VIX");
def vixLongOk = !requireVixFilter or IsNaN(vixClose) or vixClose <= maxVixThreshold;

def spyClose = close(benchmarkSymbol);
def spySma = Average(spyClose, benchmarkSmaPeriod);
def spyAtr = MovingAverage(AverageType.WILDERS, TrueRange(high(benchmarkSymbol), spyClose, low(benchmarkSymbol)), atrPeriod);
def spyBuffer = benchmarkBufferAtr * spyAtr;
def spyLongOk = !requireBenchmarkFilter or spyClose >= spySma - spyBuffer;
def spyShortOk = !requireBenchmarkFilter or spyClose <= spySma + spyBuffer;

# ---------------- continuation evaluation at the signal bar ----------------
def warm = !IsNaN(tsi[sig]) and !IsNaN(ema21[sig]) and !IsNaN(ema50[sig]) and !IsNaN(sma200[sig]) and !IsNaN(atr[sig]) and atr[sig] > 0;

def longCore =
    warm
    and low[sig] <= ema21[sig]
    and ema21[sig] > ema50[sig]
    and close[sig] > sma200[sig]
    and (!requireClvVeto or clv[sig] >= 0)
    and tsi[sig] > 0
    and (tsiMomentumPeriod <= 1 or tsi[sig] >= tsiAvg[sig])
    and !bearDivActive
    and vixLongOk
    and spyLongOk
    and (!requireConfirmation or close[sig] > ema21[sig]);

def shortCore =
    warm
    and high[sig] >= ema21[sig]
    and ema21[sig] < ema50[sig]
    and close[sig] < sma200[sig]
    and (!requireClvVeto or clv[sig] <= 0)
    and tsi[sig] < 0
    and (tsiMomentumPeriod <= 1 or tsi[sig] <= tsiAvg[sig])
    and !bullDivActive
    and spyShortOk
    and (!requireConfirmation or close[sig] < ema21[sig]);

def longConfirm = !requireConfirmation or (close[lastClosed] > ema21[lastClosed] and close[lastClosed] > high[sig]);
def shortConfirm = !requireConfirmation or (close[lastClosed] < ema21[lastClosed] and close[lastClosed] < low[sig]);

plot LongSignal = longCore and longConfirm;
plot ShortSignal = shortCore and shortConfirm;
