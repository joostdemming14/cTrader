#
# TSI — thinkScript port of the cTrader suite's True Strength Index
# (ReversalEngine.ComputeTsi: William Blau, TSI(25,13,13) + EMA(13) signal line).
# Lower (oscillator) chart study.
#
#   PC       = Close - Close[1]
#   TSI      = 100 * EMA(short, EMA(long, PC)) / EMA(short, EMA(long, |PC|))
#   TSIsignal= EMA(signal, TSI)
#
# Optional: the exact rolling price/TSI divergence rule shared by the
# ReversalScanner and ContinuationScanner (fresh or near-extreme High/Low vs
# the trailing lookback window with weaker/stronger TSI vs the previous
# window extreme and the strong reference), plotted as arrows. This is the
# same math as the scanner ports — NOT a PivotHigh/PivotLow divergence,
# because pivot-based detection waits for confirmed pivots and changes the
# signal timing. thinkScript has no native rolling-divergence function, so
# the fold implementation below is already the idiomatic exact equivalent.
#

input tsiLongPeriod = 25;
input tsiShortPeriod = 13;
input tsiSignalPeriod = 13;
input atrPeriod = 14;
input showDivergences = yes;
input divergenceLookback = 90;
input divergenceMinGap = 5;
input divergenceTsiExtremeLevel = 10.0;
input divergenceMinTsiDrop = 3.0;
input divergenceTriggerWindow = 5;   # 0..5 (fixed candidates in thinkScript)
input divergenceNearExtremeAtr = 1.0;

def pc = close - close[1];
def tsiNum = ExpAverage(ExpAverage(pc, tsiLongPeriod), tsiShortPeriod);
def tsiDen = ExpAverage(ExpAverage(AbsValue(pc), tsiLongPeriod), tsiShortPeriod);
def tsi = if tsiDen > 0.000000000001 then 100 * tsiNum / tsiDen else Double.NaN;
def tsiSignal = ExpAverage(tsi, tsiSignalPeriod);
def atr = MovingAverage(AverageType.WILDERS, TrueRange(high, close, low), atrPeriod);

# ---------------- shared rolling price/TSI divergence (see the scanner ports) ----------------
script TsiDivergence {
    input h = high;
    input l = low;
    input t = close;
    input atrS = close;
    input c = 1;
    input sig = 1;
    input lookback = 90;
    input minGap = 5;
    input tsiLevel = 10.0;
    input minDrop = 3.0;
    input nearAtr = 1.0;

    def nPrev = lookback;
    def nRef = lookback - minGap + 1;

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

def div0 = TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 0, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr);
def div1 = if divergenceTriggerWindow >= 1 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 1, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div0;
def div2 = if divergenceTriggerWindow >= 2 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 2, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div1;
def div3 = if divergenceTriggerWindow >= 3 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 3, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div2;
def div4 = if divergenceTriggerWindow >= 4 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 4, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div3;
def div5 = if divergenceTriggerWindow >= 5 then TsiDivergence(h = high, l = low, t = tsi, atrS = atr, c = 5, sig = 0, lookback = divergenceLookback, minGap = divergenceMinGap, tsiLevel = divergenceTsiExtremeLevel, minDrop = divergenceMinTsiDrop, nearAtr = divergenceNearExtremeAtr) else div4;

def bearDivActive = div0.bear or div1.bear or div2.bear or div3.bear or div4.bear or div5.bear;
def bullDivActive = div0.bull or div1.bull or div2.bull or div3.bull or div4.bull or div5.bull;

plot TSI = tsi;
plot Signal = tsiSignal;
plot ZeroLine = 0;

TSI.SetDefaultColor(GetColor(1));
Signal.SetDefaultColor(GetColor(8));
ZeroLine.SetDefaultColor(Color.GRAY);
ZeroLine.SetPaintingStrategy(PaintingStrategy.LINE);
ZeroLine.HideTitle();

plot BearDiv = if showDivergences and bearDivActive then tsi else Double.NaN;
plot BullDiv = if showDivergences and bullDivActive then tsi else Double.NaN;
BearDiv.SetDefaultColor(Color.RED);
BearDiv.SetPaintingStrategy(PaintingStrategy.ARROW_DOWN);
BearDiv.SetLineWeight(2);
BullDiv.SetDefaultColor(Color.UPTICK);
BullDiv.SetPaintingStrategy(PaintingStrategy.ARROW_UP);
BullDiv.SetLineWeight(2);
