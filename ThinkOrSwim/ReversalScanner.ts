#
# ReversalScanner — thinkScript port of the cTrader ReversalScanner
# (ReversalScanner/ReversalEngine.cs). For the thinkorswim Stock Hacker /
# Market Scanner. Use Daily aggregation (the cTrader scanner evaluates
# completed DAILY bars only).
#
# 1:1 signal logic with the cTrader defaults:
#   SHORT reversal (two steps, both on rolling windows):
#     1. DIVERGENCE: some bar d in the last 5 bars (the signal bar included)
#        printed a High at or above the highest High of its trailing 90-bar
#        window (fresh extreme) or within 1.0 x ATR of it (near-extreme), with
#        TSI[d] <= TSI at the previous window extreme and TSI[d] at least 3.0
#        below the strong reference TSI (the highest High at least 5 bars back
#        with TSI > 10), and no higher High printed since d.
#     2. TRIGGER (signal bar):
#        - CLV <= 0 only when the CLV veto is ON (default OFF)
#        - TSI <= SMA(TSI, 5) (momentum flat or falling)
#        - extension filter (default ON, 90th percentile over 250 bars): the
#          close's EMA21 gap in ATR must rank in the most extreme 10% of the
#          window for this symbol
#        - confirmation (default ON): the last closed bar closes below the
#          signal-bar low
#   LONG reversal: exact mirror (fresh/near-extreme Low with stronger TSI,
#   reference TSI < -10, extension percentile on the other side, confirmation
#   close above the signal-bar high).
#
# Platform notes (same as the continuation port):
#   - "assumeMarketClosed": with Daily aggregation bar 0 is today's FORMING bar
#     during market hours. Yes -> bar 0 counts as the closed confirmation bar
#     (run the scan after 16:00 ET). No  -> everything shifts one bar back so
#     only fully closed bars are evaluated (no repaint).
#   - The VIX long block (default OFF in the cTrader ReversalScanner) uses
#     close("VIX"). The SPY benchmark gate (default OFF) uses close("SPY") vs
#     its SMA50 with a 0.5 x ATR buffer. The crypto benchmark has no Stock
#     Hacker equivalent and is intentionally omitted.
#   - RS rank tags, scheduling/HUD/alert plumbing do not apply to a scanner.
#   - Trigger window capped at 5 (the cTrader default): thinkScript needs
#     fixed candidate offsets.
#
# Setup: Stock Hacker -> add study filter -> this script, "LongSignal is true"
# (and a second scan/filter for "ShortSignal is true").
#

input assumeMarketClosed = yes;      # bar 0 is a CLOSED bar (run after 16:00 ET)
input requireConfirmation = yes;     # next-bar confirmation (cTrader default: ON)
input emaPeriod = 21;
input atrPeriod = 14;
input tsiLongPeriod = 25;
input tsiShortPeriod = 13;
input tsiSignalPeriod = 13;         # display-only in cTrader; kept for parity
input requireClvVeto = no;           # cTrader default: OFF
input divergenceLookback = 90;
input divergenceMinGap = 5;
input divergenceTsiExtremeLevel = 10.0;
input divergenceMinTsiDrop = 3.0;
input divergenceTriggerWindow = 5;   # 0..5 (fixed candidates in thinkScript)
input divergenceNearExtremeAtr = 1.0;
input tsiMomentumPeriod = 5;
input minExtensionPercentile = 90.0; # 0 = off (cTrader default: 90)
input extensionWindow = 250;
input requireVixFilter = no;         # cTrader ReversalScanner default: OFF
input maxVixThreshold = 25.0;
input requireBenchmarkFilter = no;   # cTrader default: OFF
input benchmarkSymbol = "SPY";
input benchmarkSmaPeriod = 50;
input benchmarkBufferAtr = 0.5;
input requireRvolFilter = yes;       # volume filter on the confirmation bar (default ON; ToS/Schwab volume is real, unlike CFD tick volume)
input rvolPeriod = 50;               # rvol = Volume / SMA(Volume, rvolPeriod)
input minRvol = 1.5;

# ---------------- indicator math (Wilder ATR, Blau TSI) ----------------
def clv = if (high - low) > 0 then (2 * close - high - low) / (high - low) else 0;
def ema21 = ExpAverage(close, emaPeriod);
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
# sits at offset sig). "bear" = fresh/near-extreme High with weaker TSI vs the
# previous window extreme and vs the strong reference (highest High of the
# window with TSI > level); "bull" is the mirror for lows. Stale candidates
# (a more extreme print after d) never qualify.
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

    def pHi = fold k = 1 to nPrev with p = 0 do if GetValue(h, c + 1 + k) > GetValue(h, c + 1 + p) then k else p;
    def pLo = fold k = 1 to nPrev with p = 0 do if GetValue(l, c + 1 + k) < GetValue(l, c + 1 + p) then k else p;
    def pHiVal = GetValue(h, c + 1 + pHi);
    def pLoVal = GetValue(l, c + 1 + pLo);
    def pHiTsi = GetValue(t, c + 1 + pHi);
    def pLoTsi = GetValue(t, c + 1 + pLo);

    def rHi = fold k = 0 to nRef with r = 0 do if GetValue(t, c + minGap + k) > tsiLevel and (r == 0 or GetValue(h, c + minGap + k) > GetValue(h, c + minGap + r - 1)) then k + 1 else r;
    def rLo = fold k = 0 to nRef with r = 0 do if GetValue(t, c + minGap + k) < -tsiLevel and (r == 0 or GetValue(l, c + minGap + k) < GetValue(l, c + minGap + r - 1)) then k + 1 else r;
    def rHiTsi = if rHi > 0 then GetValue(t, c + minGap + rHi - 1) else Double.NaN;
    def rLoTsi = if rLo > 0 then GetValue(t, c + minGap + rLo - 1) else Double.NaN;

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

# ---------------- gates ----------------
def vixClose = close("VIX");
def vixLongOk = !requireVixFilter or IsNaN(vixClose) or vixClose <= maxVixThreshold;

def spyClose = close(benchmarkSymbol);
def spySma = Average(spyClose, benchmarkSmaPeriod);
def spyAtr = MovingAverage(AverageType.WILDERS, TrueRange(high(benchmarkSymbol), spyClose, low(benchmarkSymbol)), atrPeriod);
def spyBuffer = benchmarkBufferAtr * spyAtr;
def spyLongOk = !requireBenchmarkFilter or spyClose >= spySma - spyBuffer;
def spyShortOk = !requireBenchmarkFilter or spyClose <= spySma + spyBuffer;

# ---------------- extension percentile (ReversalEngine.ExtensionPercentile) ----------------
# Percentile rank (0..100) of the signal bar's EMA21 gap in ATR within the last
# extensionWindow bars. Longs rank (EMA21 - Close)/ATR, shorts (Close - EMA21)/ATR,
# so 100 = the most extended bar of the window in the setup's direction. The
# cTrader engine needs 30 valid bars and counts ties as "not below" (conservative).
def gapLong = (ema21[sig] - close[sig]) / atr[sig];
def gapShort = (close[sig] - ema21[sig]) / atr[sig];
def belowLong = fold k = 1 to extensionWindow with b = 0 do if !IsNaN(close[sig + k]) and !IsNaN(ema21[sig + k]) and !IsNaN(atr[sig + k]) and atr[sig + k] > 0 and (ema21[sig + k] - close[sig + k]) / atr[sig + k] < gapLong then b + 1 else b;
def belowShort = fold k = 1 to extensionWindow with b = 0 do if !IsNaN(close[sig + k]) and !IsNaN(ema21[sig + k]) and !IsNaN(atr[sig + k]) and atr[sig + k] > 0 and (close[sig + k] - ema21[sig + k]) / atr[sig + k] < gapShort then b + 1 else b;
def extPctLong = 100.0 * belowLong / (extensionWindow - 1);
def extPctShort = 100.0 * belowShort / (extensionWindow - 1);


# ---------------- optional relative-volume filter (confirmation bar) ----------------
def rvol = volume[lastClosed] / Average(volume, rvolPeriod)[lastClosed];
def rvolOk = !requireRvolFilter or (!IsNaN(rvol) and rvol >= minRvol);

# ---------------- reversal evaluation at the signal bar ----------------
def warm = !IsNaN(tsi[sig]) and !IsNaN(ema21[sig]) and !IsNaN(atr[sig]) and atr[sig] > 0;

def longCore =
    warm
    and bullDivActive
    and (!requireClvVeto or clv[sig] >= 0)
    and (tsiMomentumPeriod <= 1 or tsi[sig] >= tsiAvg[sig])
    and (minExtensionPercentile <= 0 or (extPctLong >= minExtensionPercentile))
    and vixLongOk
    and spyLongOk;

def shortCore =
    warm
    and bearDivActive
    and (!requireClvVeto or clv[sig] <= 0)
    and (tsiMomentumPeriod <= 1 or tsi[sig] <= tsiAvg[sig])
    and (minExtensionPercentile <= 0 or (extPctShort >= minExtensionPercentile))
    and spyShortOk;

def longConfirm = !requireConfirmation or close[lastClosed] > high[sig];
def shortConfirm = !requireConfirmation or close[lastClosed] < low[sig];

plot LongSignal = longCore and longConfirm and rvolOk;
plot ShortSignal = shortCore and shortConfirm and rvolOk;
