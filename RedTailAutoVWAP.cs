#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using System.IO;
using System.Speech.Synthesis;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class RedTailAutoVWAP : Indicator
    {
        #region Private Classes
        
        private class VwapData
        {
            public double CumVolume;
            public double CumTypicalVolume;
            public double CumVolumeSquared; // for bands (variance)
            public double Value;
            public double UpperBand;
            public double LowerBand;
            public bool   IsActive;
            public int    AnchorBar;
            
            public void Reset(int barIndex)
            {
                CumVolume = 0;
                CumTypicalVolume = 0;
                CumVolumeSquared = 0;
                Value = 0;
                UpperBand = 0;
                LowerBand = 0;
                IsActive = true;
                AnchorBar = barIndex;
            }
            
            public void Update(double source, double vol)
            {
                if (!IsActive || vol <= 0) return;
                
                CumVolume += vol;
                CumTypicalVolume += source * vol;
                CumVolumeSquared += source * source * vol;
                
                if (CumVolume > 0)
                {
                    Value = CumTypicalVolume / CumVolume;
                    double variance = (CumVolumeSquared / CumVolume) - (Value * Value);
                    double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;
                    UpperBand = Value + stdDev;
                    LowerBand = Value - stdDev;
                }
            }
        }
        
        private class RangeData
        {
            public double High;
            public double Low;
            public int    StartBar;
            public int    EndBar;
            public bool   IsForming;
            
            public void Reset(double h, double l, int bar)
            {
                High = h;
                Low = l;
                StartBar = bar;
                EndBar = bar;
                IsForming = true;
            }
            
            public void Update(double h, double l, int bar)
            {
                if (!IsForming) return;
                High = Math.Max(High, h);
                Low = Math.Min(Low, l);
                EndBar = bar;
            }
        }
        
        private class HistoricalVwap
        {
            public List<KeyValuePair<int, double>> Points = new List<KeyValuePair<int, double>>();
        }
        
        private class HistoricalRange
        {
            public double High;
            public double Low;
            public int StartBar;
            public int EndBar;
        }
        
        #endregion
        
        #region Private Variables
        
        // VWAP data objects
        private VwapData nyVwap;
        private VwapData prevNyVwap;
        private VwapData dayVwap;
        private VwapData hodVwap;
        private VwapData lodVwap;
        private VwapData monthVwap;
        private VwapData yearVwap;
        private VwapData hoyVwap;
        
        // Historical VWAP storage
        private List<HistoricalVwap> nyVwapHistory;
        private List<HistoricalVwap> dayVwapHistory;
        private List<HistoricalVwap> dayBandUpperHistory;
        private List<HistoricalVwap> dayBandLowerHistory;
        private List<HistoricalVwap> monthVwapHistory;
        private List<HistoricalVwap> yearVwapHistory;
        
        // Previous NY VWAP 
        private VwapData prevDayNyVwapCalc;
        private int prevNyAnchorBar = -1;
        private int currentNyAnchorBar = -1;
        
        // Previous Session VWAP
        private VwapData prevSessionVwap;
        private VwapData prevSessionVwapCalc;
        private int prevSessionAnchorBar = -1;
        private int currentSessionAnchorBar = -1;
        
        // Range data
        private RangeData nyOpeningRange;
        private RangeData dayInitialBalance;
        private List<HistoricalRange> nyRangeHistory;
        private List<HistoricalRange> dayRangeHistory;
        
        // Session tracking
        private bool wasNySession;
        private bool wasNyRange;
        private bool wasDayRange;
        private double highOfDay;
        private double lowOfDay;
        private double highOfYear;
        private int lastDayChangeBar = -1;
        private int lastYearChangeBar = -1;
        private int hodAnchorBar = -1;
        private int lodAnchorBar = -1;
        
        // Timezone
        private TimeZoneInfo estZone;
        private TimeZoneInfo chartZone;
        
        // Session time helpers
        private SessionIterator sessionIterator;
        private bool hasVolume;
        
        // Voice alert system
        private Dictionary<string, string> voiceAlertPaths = new Dictionary<string, string>();
        private string instrumentName = "";
        private Dictionary<string, DateTime> lastAlertTime;
        private Dictionary<string, bool> lastTouchState;
        private Dictionary<string, bool> lastApproachState;
        
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"RedTail Auto VWAP - Automatic VWAPs & Key Levels for futures trading. Features NY Session VWAP, Previous Day NY VWAP, Daily VWAP, High/Low of Day VWAPs, Monthly/Yearly VWAPs, Opening Range, and Initial Balance Range.";
                Name = "RedTail Auto VWAP";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
                DisplayInDataBox = true;
                DrawOnPricePanel = true;
                PaintPriceMarkers = false;
                IsSuspendedWhileInactive = true;
                
                // NY Session VWAP
                ShowNyVwap = true;
                NyVwapColor = Brushes.Blue;
                NyVwapStyle = DashStyleHelper.Solid;
                NyVwapHistoryCount = 0;
                
                // Previous Day NY VWAP
                ShowPrevNyVwap = true;
                PrevNyVwapColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 77, 208, 225));
                PrevNyVwapStyle = DashStyleHelper.Solid;
                
                // Day VWAP
                ShowDayVwap = false;
                DayVwapColor = Brushes.Black;
                DayVwapStyle = DashStyleHelper.Solid;
                DayVwapHistoryCount = 0;
                UseDynamicSessionColor = false;
                SessionVwapAboveColor = Brushes.Green;
                SessionVwapBelowColor = Brushes.Red;
                ShowDayBands = false;
                DayBandMult = 1;
                DayBandColor = Brushes.Black;
                DayBandStyle = DashStyleHelper.Dot;
                
                // Previous Session VWAP
                ShowPrevSessionVwap = false;
                PrevSessionVwapColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(100, 128, 128, 128));
                PrevSessionVwapStyle = DashStyleHelper.Dash;
                PrevSessionBandColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(80, 128, 128, 128));
                PrevSessionBandStyle = DashStyleHelper.Dot;
                
                // HOD VWAP
                ShowHodVwap = true;
                HodVwapColor = Brushes.Purple;
                HodVwapStyle = DashStyleHelper.Solid;
                ShowHodBands = false;
                HodBandMult = 1;
                HodBandColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 128, 0, 128));
                HodBandStyle = DashStyleHelper.Solid;
                
                // LOD VWAP
                ShowLodVwap = false;
                LodVwapColor = Brushes.Purple;
                LodVwapStyle = DashStyleHelper.Solid;
                ShowLodBands = false;
                LodBandMult = 1;
                LodBandColor = Brushes.Purple;
                LodBandStyle = DashStyleHelper.Solid;
                
                // Month VWAP
                ShowMonthVwap = false;
                MonthVwapColor = Brushes.Black;
                MonthVwapStyle = DashStyleHelper.Solid;
                MonthVwapHistoryCount = 0;
                
                // Year VWAP
                ShowYearVwap = false;
                YearVwapColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 200, 230, 201));
                YearVwapStyle = DashStyleHelper.Solid;
                YearVwapHistoryCount = 0;
                
                // HOY VWAP
                ShowHoyVwap = false;
                HoyVwapColor = Brushes.Green;
                HoyVwapStyle = DashStyleHelper.Solid;
                
                // NY Opening Range
                ShowNyOpeningRange = true;
                NyOpeningRangeStart = DateTime.Parse("09:30");
                NyOpeningRangeEnd = DateTime.Parse("09:45");
                NyRangeColor = Brushes.Black;
                NyRangeHighStyle = DashStyleHelper.Solid;
                NyRangeLowStyle = DashStyleHelper.Solid;
                NyRangeLineThickness = 1;
                NyRangeLineOpacity = 100;
                NyRangeFillOpacity = 100;
                NyRangeHistoryCount = 0;
                ShowNyRangeText = true;
                NyRangeFontSize = 10;
                
                // Day Initial Balance
                ShowDayInitialBalance = false;
                DayIBStart = DateTime.Parse("09:30");
                DayIBEnd = DateTime.Parse("10:30");
                DayIBColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(128, 0, 188, 244));
                DayIBHighStyle = DashStyleHelper.Dash;
                DayIBLowStyle = DashStyleHelper.Dash;
                DayIBLineThickness = 1;
                DayIBLineOpacity = 100;
                DayIBFillOpacity = 85;
                DayIBHistoryCount = 0;
                ShowIBText = true;
                DayIBFontSize = 10;
                
                // Level Merging
                MergeOverlappingLevels = true;
                
                // VWAP Labels
                ShowVwapLabels = false;
                VwapLabelFontSize = 10;
                
                // Voice Alerts
                EnableVoiceAlerts = true;
                VoiceAlertRate = 2;
                AlertOnTouch = true;
                AlertOnApproach = true;
                ApproachTicks = 8;
                AlertCooldownSeconds = 30;
                AlertFallbackSound = "Alert1.wav";
            }
            else if (State == State.Configure)
            {
            }
            else if (State == State.Terminated)
            {
                // Nothing to dispose - WAV files are cached on disk
            }
            else if (State == State.DataLoaded)
            {
                sessionIterator = new SessionIterator(Bars);
                
                // Cache Eastern timezone — handles both Windows ("Eastern Standard Time") 
                // and Linux/Mac ("America/New_York") timezone IDs
                try { estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); }
                catch { estZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
                
                // Get chart's exchange timezone for proper bar time conversion
                chartZone = Bars.TradingHours.TimeZoneInfo ?? TimeZoneInfo.Local;
                
                nyVwap = new VwapData();
                prevNyVwap = new VwapData();
                prevDayNyVwapCalc = new VwapData();
                prevSessionVwap = new VwapData();
                prevSessionVwapCalc = new VwapData();
                dayVwap = new VwapData();
                hodVwap = new VwapData();
                lodVwap = new VwapData();
                monthVwap = new VwapData();
                yearVwap = new VwapData();
                hoyVwap = new VwapData();
                
                nyVwapHistory = new List<HistoricalVwap>();
                dayVwapHistory = new List<HistoricalVwap>();
                dayBandUpperHistory = new List<HistoricalVwap>();
                dayBandLowerHistory = new List<HistoricalVwap>();
                monthVwapHistory = new List<HistoricalVwap>();
                yearVwapHistory = new List<HistoricalVwap>();
                
                nyOpeningRange = new RangeData();
                dayInitialBalance = new RangeData();
                nyRangeHistory = new List<HistoricalRange>();
                dayRangeHistory = new List<HistoricalRange>();
                
                // Initialize voice alert system
                lastAlertTime = new Dictionary<string, DateTime>();
                lastTouchState = new Dictionary<string, bool>();
                lastApproachState = new Dictionary<string, bool>();
                
                if (EnableVoiceAlerts)
                {
                    try
                    {
                        instrumentName = Instrument != null ? Instrument.MasterInstrument.Name : "Unknown";
                        GenerateVoiceAlerts();
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail VWAP: Voice alert generation error: " + ex.Message);
                    }
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;
            
            double source = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0; // ohlc4
            double vol = Volume[0];
            
            hasVolume = vol > 0;
            
            // Initialize VWAPs on first processable bar
            if (CurrentBar == 1)
            {
                if (ShowNyVwap) nyVwap.Reset(CurrentBar);
                if (ShowDayVwap) dayVwap.Reset(CurrentBar);
                if (ShowHodVwap) hodVwap.Reset(CurrentBar);
                if (ShowLodVwap) lodVwap.Reset(CurrentBar);
                if (ShowMonthVwap) monthVwap.Reset(CurrentBar);
                if (ShowYearVwap) yearVwap.Reset(CurrentBar);
                if (ShowHoyVwap) hoyVwap.Reset(CurrentBar);
            }
            
            // Convert bar times to Eastern for session detection
            // 2-param ConvertTime treats Unspecified Kind as local time, converts to destination zone
            DateTime barTimeEst = TimeZoneInfo.ConvertTime(Time[0], estZone);
            DateTime prevBarTimeEst = TimeZoneInfo.ConvertTime(Time[1], estZone);
            
            TimeSpan barTime = barTimeEst.TimeOfDay;
            TimeSpan prevBarTime = prevBarTimeEst.TimeOfDay;
            
            // ─── Session Detection ───
            
            // For time-based charts, estimate bar open time from close time
            // NT8 Time[0] is bar CLOSE time. Bar open ≈ Time[0] - period
            // For non-time-based charts (tick/range), use Time[0] as-is
            TimeSpan barOpenTime = barTime;
            TimeSpan prevBarOpenTime = prevBarTime;
            if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
            {
                TimeSpan period = TimeSpan.FromMinutes(BarsPeriod.Value);
                barOpenTime = barTime - period;
                prevBarOpenTime = prevBarTime - period;
            }
            else if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
            {
                TimeSpan period = TimeSpan.FromSeconds(BarsPeriod.Value);
                barOpenTime = barTime - period;
                prevBarOpenTime = prevBarTime - period;
            }
            
            bool isNySession = barOpenTime >= new TimeSpan(9, 30, 0) && barOpenTime < new TimeSpan(16, 0, 0);
            
            bool isNyRangeTime = barOpenTime >= NyOpeningRangeStart.TimeOfDay && barOpenTime < NyOpeningRangeEnd.TimeOfDay;
            bool isDayIBTime = barOpenTime >= DayIBStart.TimeOfDay && barOpenTime < DayIBEnd.TimeOfDay;
            
            // Detect new NY session start (first bar at or after 9:30)
            bool newNySession = isNySession && barOpenTime >= new TimeSpan(9, 30, 0) && 
                                (prevBarOpenTime < new TimeSpan(9, 30, 0) || barTimeEst.Date != prevBarTimeEst.Date);
            
            // Detect new NY range session start
            bool newNyRange = isNyRangeTime && 
                              (prevBarOpenTime < NyOpeningRangeStart.TimeOfDay || barTimeEst.Date != prevBarTimeEst.Date);
            
            bool newDayIB = isDayIBTime && 
                            (prevBarOpenTime < DayIBStart.TimeOfDay || barTimeEst.Date != prevBarTimeEst.Date);
            
            // Detect new futures session (6:00 PM ET start)
            bool newSession = barOpenTime >= new TimeSpan(18, 0, 0) && 
                              (prevBarOpenTime < new TimeSpan(18, 0, 0) || barTimeEst.Date != prevBarTimeEst.Date);
            
            
            // Detect day/month/year changes
            bool newDay = barTimeEst.Date != prevBarTimeEst.Date;
            bool newMonth = barTimeEst.Month != prevBarTimeEst.Month || barTimeEst.Year != prevBarTimeEst.Year;
            bool newYear = barTimeEst.Year != prevBarTimeEst.Year;
            
            // ─── High/Low of Day Tracking ───
            if (newSession)
            {
                highOfDay = High[0];
                lowOfDay = Low[0];
                lastDayChangeBar = CurrentBar;
            }
            else
            {
                highOfDay = Math.Max(High[0], highOfDay);
                lowOfDay = Math.Min(Low[0], lowOfDay);
            }
            
            // ─── High of Year Tracking ───
            if (newYear)
            {
                highOfYear = High[0];
                lastYearChangeBar = CurrentBar;
            }
            else
            {
                highOfYear = Math.Max(High[0], highOfYear);
            }
            
            // ═══════════════════════════════════════════════
            // VWAP Calculations
            // ═══════════════════════════════════════════════
            
            // ─── NY Session VWAP ───
            if (ShowNyVwap)
            {
                if (newNySession)
                {
                    // Save previous NY VWAP to history
                    if (nyVwap.IsActive && NyVwapHistoryCount > 0)
                        SaveVwapToHistory(nyVwapHistory, nyVwap, NyVwapHistoryCount);
                    
                    // Track anchor bars for Previous Day NY VWAP
                    prevNyAnchorBar = currentNyAnchorBar;
                    currentNyAnchorBar = CurrentBar;
                    
                    // Reset previous day VWAP calc from the old current
                    if (ShowPrevNyVwap && prevNyAnchorBar >= 0 && prevDayNyVwapCalc.IsActive)
                    {
                        // prevNyVwap takes over the previous day's calculation
                        prevNyVwap.CumVolume = prevDayNyVwapCalc.CumVolume;
                        prevNyVwap.CumTypicalVolume = prevDayNyVwapCalc.CumTypicalVolume;
                        prevNyVwap.CumVolumeSquared = prevDayNyVwapCalc.CumVolumeSquared;
                        prevNyVwap.Value = prevDayNyVwapCalc.Value;
                        prevNyVwap.AnchorBar = prevDayNyVwapCalc.AnchorBar;
                        prevNyVwap.IsActive = true;
                    }
                    
                    // Start fresh tracking for current day (will become "previous" tomorrow)
                    prevDayNyVwapCalc.Reset(CurrentBar);
                    
                    nyVwap.Reset(CurrentBar);
                }
                
                if (isNySession && nyVwap.IsActive)
                {
                    nyVwap.Update(source, vol);
                    
                    // Also update the running calc that will become "previous day" next session
                    prevDayNyVwapCalc.Update(source, vol);
                }
                
                // Continue updating previous day VWAP with current session data
                if (isNySession && prevNyVwap.IsActive && ShowPrevNyVwap)
                {
                    prevNyVwap.Update(source, vol);
                }
            }
            
            // ─── Session VWAP ───
            if (ShowDayVwap)
            {
                if (newSession)
                {
                    if (dayVwap.IsActive && DayVwapHistoryCount > 0)
                        SaveVwapToHistory(dayVwapHistory, dayVwap, DayVwapHistoryCount);
                    
                    // Track anchor bars for Previous Session VWAP
                    prevSessionAnchorBar = currentSessionAnchorBar;
                    currentSessionAnchorBar = CurrentBar;
                    
                    // Copy current session calc to prevSessionVwap
                    if (ShowPrevSessionVwap && prevSessionAnchorBar >= 0 && prevSessionVwapCalc.IsActive)
                    {
                        prevSessionVwap.CumVolume = prevSessionVwapCalc.CumVolume;
                        prevSessionVwap.CumTypicalVolume = prevSessionVwapCalc.CumTypicalVolume;
                        prevSessionVwap.CumVolumeSquared = prevSessionVwapCalc.CumVolumeSquared;
                        prevSessionVwap.Value = prevSessionVwapCalc.Value;
                        prevSessionVwap.AnchorBar = prevSessionVwapCalc.AnchorBar;
                        prevSessionVwap.IsActive = true;
                    }
                    
                    // Start fresh tracking for current session
                    prevSessionVwapCalc.Reset(CurrentBar);
                    
                    dayVwap.Reset(CurrentBar);
                }
                
                dayVwap.Update(source, vol);
                
                // Also update the running calc that will become "previous session" next session
                prevSessionVwapCalc.Update(source, vol);
                
                // Continue updating previous session VWAP with current session data
                if (prevSessionVwap.IsActive && ShowPrevSessionVwap)
                {
                    prevSessionVwap.Update(source, vol);
                }
            }
            
            // ─── HOD VWAP ───
            if (ShowHodVwap)
            {
                bool isNewHod = High[0] >= highOfDay;
                if (isNewHod)
                {
                    hodVwap.Reset(CurrentBar);
                    hodAnchorBar = CurrentBar;
                }
                
                if (hodVwap.IsActive)
                    hodVwap.Update(source, vol);
            }
            
            // ─── LOD VWAP ───
            if (ShowLodVwap)
            {
                bool isNewLod = Low[0] <= lowOfDay;
                if (isNewLod)
                {
                    lodVwap.Reset(CurrentBar);
                    lodAnchorBar = CurrentBar;
                }
                
                if (lodVwap.IsActive)
                    lodVwap.Update(source, vol);
            }
            
            // ─── Month VWAP ───
            if (ShowMonthVwap)
            {
                if (newMonth)
                {
                    if (monthVwap.IsActive && MonthVwapHistoryCount > 0)
                        SaveVwapToHistory(monthVwapHistory, monthVwap, MonthVwapHistoryCount);
                    
                    monthVwap.Reset(CurrentBar);
                }
                
                monthVwap.Update(source, vol);
            }
            
            // ─── Year VWAP ───
            if (ShowYearVwap)
            {
                if (newYear)
                {
                    if (yearVwap.IsActive && YearVwapHistoryCount > 0)
                        SaveVwapToHistory(yearVwapHistory, yearVwap, YearVwapHistoryCount);
                    
                    yearVwap.Reset(CurrentBar);
                }
                
                yearVwap.Update(source, vol);
            }
            
            // ─── HOY VWAP ───
            if (ShowHoyVwap)
            {
                bool isNewHoy = High[0] >= highOfYear;
                if (isNewHoy)
                {
                    hoyVwap.Reset(CurrentBar);
                }
                
                if (hoyVwap.IsActive)
                    hoyVwap.Update(source, vol);
            }
            
            // ═══════════════════════════════════════════════
            // Opening Range / Initial Balance
            // ═══════════════════════════════════════════════
            
            // Ranges work on all chart types
            {
                // ─── NY Opening Range ───
                if (ShowNyOpeningRange)
                {
                    if (newNyRange)
                    {
                        // Save previous range to history
                        if (nyOpeningRange.IsForming || nyOpeningRange.High > 0)
                        {
                            if (NyRangeHistoryCount > 0)
                                SaveRangeToHistory(nyRangeHistory, nyOpeningRange, NyRangeHistoryCount);
                        }
                        
                        nyOpeningRange.Reset(High[0], Low[0], CurrentBar);
                    }
                    else if (isNyRangeTime && nyOpeningRange.IsForming)
                    {
                        nyOpeningRange.Update(High[0], Low[0], CurrentBar);
                    }
                    else if (!isNyRangeTime && nyOpeningRange.IsForming)
                    {
                        nyOpeningRange.IsForming = false;
                    }
                    
                    // Keep extending range lines to current bar
                    if (nyOpeningRange.High > 0)
                        nyOpeningRange.EndBar = CurrentBar;
                }
                
                // ─── Day Initial Balance ───
                if (ShowDayInitialBalance)
                {
                    if (newDayIB)
                    {
                        if (dayInitialBalance.IsForming || dayInitialBalance.High > 0)
                        {
                            if (DayIBHistoryCount > 0)
                                SaveRangeToHistory(dayRangeHistory, dayInitialBalance, DayIBHistoryCount);
                        }
                        
                        dayInitialBalance.Reset(High[0], Low[0], CurrentBar);
                    }
                    else if (isDayIBTime && dayInitialBalance.IsForming)
                    {
                        dayInitialBalance.Update(High[0], Low[0], CurrentBar);
                    }
                    else if (!isDayIBTime && dayInitialBalance.IsForming)
                    {
                        dayInitialBalance.IsForming = false;
                    }
                    
                    if (dayInitialBalance.High > 0)
                        dayInitialBalance.EndBar = CurrentBar;
                }
            }
            
            // ═══════════════════════════════════════════════
            // Voice Alert System (real-time only)
            // ═══════════════════════════════════════════════
            if (EnableVoiceAlerts && State == State.Realtime && IsFirstTickOfBar)
            {
                double closePrice = Close[0];
                double highPrice = High[0];
                double lowPrice = Low[0];
                double approachDist = ApproachTicks * TickSize;
                
                if (ShowNyVwap && nyVwap.IsActive && nyVwap.Value > 0)
                    CheckVwapAlert("NYSession", "NY Session VWAP", nyVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowPrevNyVwap && prevNyVwap.IsActive && prevNyVwap.Value > 0)
                    CheckVwapAlert("PrevNY", "Prev NY VWAP", prevNyVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowDayVwap && dayVwap.IsActive && dayVwap.Value > 0)
                    CheckVwapAlert("Session", "Session VWAP", dayVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowPrevSessionVwap && prevSessionVwap.IsActive && prevSessionVwap.Value > 0)
                    CheckVwapAlert("PrevSession", "Prev Session VWAP", prevSessionVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowHodVwap && hodVwap.IsActive && hodVwap.Value > 0)
                    CheckVwapAlert("HOD", "HOD VWAP", hodVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowLodVwap && lodVwap.IsActive && lodVwap.Value > 0)
                    CheckVwapAlert("LOD", "LOD VWAP", lodVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowMonthVwap && monthVwap.IsActive && monthVwap.Value > 0)
                    CheckVwapAlert("Monthly", "Monthly VWAP", monthVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowYearVwap && yearVwap.IsActive && yearVwap.Value > 0)
                    CheckVwapAlert("Yearly", "Yearly VWAP", yearVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                if (ShowHoyVwap && hoyVwap.IsActive && hoyVwap.Value > 0)
                    CheckVwapAlert("HOY", "HOY VWAP", hoyVwap.Value, closePrice, highPrice, lowPrice, approachDist);
                
                // ─── Band Alerts (outermost enabled band for each VWAP) ───
                
                // Session VWAP bands
                if (ShowDayVwap && ShowDayBands && dayVwap.IsActive && dayVwap.Value > 0 && dayVwap.UpperBand > dayVwap.Value)
                {
                    double stdDev = dayVwap.UpperBand - dayVwap.Value;
                    double outerUpper = dayVwap.Value + stdDev * DayBandMult;
                    double outerLower = dayVwap.Value - stdDev * DayBandMult;
                    CheckVwapAlert("SessionUpperBand", "Session vee-wop Upper Band", outerUpper, closePrice, highPrice, lowPrice, approachDist);
                    CheckVwapAlert("SessionLowerBand", "Session vee-wop Lower Band", outerLower, closePrice, highPrice, lowPrice, approachDist);
                }
                
                // HOD VWAP bands
                if (ShowHodVwap && ShowHodBands && hodVwap.IsActive && hodVwap.Value > 0 && hodVwap.UpperBand > hodVwap.Value)
                {
                    double stdDev = hodVwap.UpperBand - hodVwap.Value;
                    double outerUpper = hodVwap.Value + stdDev * HodBandMult;
                    double outerLower = hodVwap.Value - stdDev * HodBandMult;
                    CheckVwapAlert("HODUpperBand", "HOD vee-wop Upper Band", outerUpper, closePrice, highPrice, lowPrice, approachDist);
                    CheckVwapAlert("HODLowerBand", "HOD vee-wop Lower Band", outerLower, closePrice, highPrice, lowPrice, approachDist);
                }
                
                // LOD VWAP bands
                if (ShowLodVwap && ShowLodBands && lodVwap.IsActive && lodVwap.Value > 0 && lodVwap.UpperBand > lodVwap.Value)
                {
                    double stdDev = lodVwap.UpperBand - lodVwap.Value;
                    double outerUpper = lodVwap.Value + stdDev * LodBandMult;
                    double outerLower = lodVwap.Value - stdDev * LodBandMult;
                    CheckVwapAlert("LODUpperBand", "LOD vee-wop Upper Band", outerUpper, closePrice, highPrice, lowPrice, approachDist);
                    CheckVwapAlert("LODLowerBand", "LOD vee-wop Lower Band", outerLower, closePrice, highPrice, lowPrice, approachDist);
                }
            }
        }
        
        #region Helper Methods
        
        private void SaveVwapToHistory(List<HistoricalVwap> history, VwapData vwap, int maxCount)
        {
            // We don't store point-by-point for history in OnBarUpdate; 
            // instead we'll render from stored anchor info in OnRender
            // This is a placeholder - actual historical rendering handled differently
            while (history.Count >= maxCount)
                history.RemoveAt(0);
        }
        
        private void SaveRangeToHistory(List<HistoricalRange> history, RangeData range, int maxCount)
        {
            history.Add(new HistoricalRange 
            { 
                High = range.High, 
                Low = range.Low, 
                StartBar = range.StartBar, 
                EndBar = range.EndBar 
            });
            
            while (history.Count > maxCount)
                history.RemoveAt(0);
        }
        
        private SharpDX.Direct2D1.StrokeStyle GetStrokeStyle(RenderTarget renderTarget, DashStyleHelper style)
        {
            SharpDX.Direct2D1.StrokeStyleProperties props = new SharpDX.Direct2D1.StrokeStyleProperties();
            
            switch (style)
            {
                case DashStyleHelper.Dash:
                    props.DashStyle = SharpDX.Direct2D1.DashStyle.Custom;
                    props.DashOffset = 0;
                    return new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, props, new float[] { 6f, 3f });
                case DashStyleHelper.Dot:
                    props.DashStyle = SharpDX.Direct2D1.DashStyle.Custom;
                    props.DashOffset = 0;
                    props.DashCap = SharpDX.Direct2D1.CapStyle.Round;
                    return new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, props, new float[] { 2f, 3f });
                default:
                    props.DashStyle = SharpDX.Direct2D1.DashStyle.Solid;
                    return new SharpDX.Direct2D1.StrokeStyle(renderTarget.Factory, props);
            }
        }
        
        private SharpDX.Color4 BrushToColor4(System.Windows.Media.Brush brush, float opacity = 1f)
        {
            if (brush is System.Windows.Media.SolidColorBrush scb)
            {
                var c = scb.Color;
                return new SharpDX.Color4(c.R / 255f, c.G / 255f, c.B / 255f, (c.A / 255f) * opacity);
            }
            return new SharpDX.Color4(1, 1, 1, opacity);
        }
        
        #endregion
        
        #region Voice Alert Methods
        
        private void CheckVwapAlert(string vwapKey, string vwapName, double vwapValue, double close, double high, double low, double approachDist)
        {
            bool isTouching = low <= vwapValue && high >= vwapValue;
            double distToVwap = Math.Min(Math.Abs(high - vwapValue), Math.Abs(low - vwapValue));
            bool isApproaching = !isTouching && distToVwap <= approachDist;
            
            string touchKey = vwapKey + "_Touch";
            string approachKey = vwapKey + "_Approach";
            
            if (!lastTouchState.ContainsKey(touchKey)) lastTouchState[touchKey] = false;
            if (!lastApproachState.ContainsKey(approachKey)) lastApproachState[approachKey] = false;
            
            // Touch alert
            if (AlertOnTouch && isTouching && !lastTouchState[touchKey])
            {
                if (CanAlert(touchKey))
                {
                    string alertId = "RTVWAP_Touch_" + vwapKey + "_" + CurrentBar;
                    string message = instrumentName + " has touched the " + vwapName;
                    Alert(alertId, Priority.Medium, message,
                        GetVoiceAlertPath(vwapKey + "_Touch", AlertFallbackSound), 10, Brushes.Black, Brushes.Yellow);
                    RecordAlert(touchKey);
                }
            }
            
            // Approach alert
            if (AlertOnApproach && isApproaching && !lastApproachState[approachKey])
            {
                if (CanAlert(approachKey))
                {
                    string alertId = "RTVWAP_Approach_" + vwapKey + "_" + CurrentBar;
                    string message = instrumentName + " is approaching the " + vwapName;
                    Alert(alertId, Priority.Low, message,
                        GetVoiceAlertPath(vwapKey + "_Approach", AlertFallbackSound), 10, Brushes.Black, Brushes.Cyan);
                    RecordAlert(approachKey);
                }
            }
            
            lastTouchState[touchKey] = isTouching;
            lastApproachState[approachKey] = isApproaching;
        }
        
        private bool CanAlert(string key)
        {
            if (!lastAlertTime.ContainsKey(key)) return true;
            return (DateTime.Now - lastAlertTime[key]).TotalSeconds >= AlertCooldownSeconds;
        }
        
        private void RecordAlert(string key)
        {
            lastAlertTime[key] = DateTime.Now;
        }
        
        private void GenerateVoiceAlerts()
        {
            string soundDir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds");
            if (!Directory.Exists(soundDir))
                Directory.CreateDirectory(soundDir);
            
            // Define all alert phrases for each VWAP type
            var alerts = new Dictionary<string, string>
            {
                { "NYSession_Touch",      instrumentName + " has touched the NY Session vee-wop" },
                { "NYSession_Approach",    instrumentName + " is approaching the NY Session vee-wop" },
                { "PrevNY_Touch",          instrumentName + " has touched the Previous NY vee-wop" },
                { "PrevNY_Approach",        instrumentName + " is approaching the Previous NY vee-wop" },
                { "Session_Touch",          instrumentName + " has touched the Session vee-wop" },
                { "Session_Approach",       instrumentName + " is approaching the Session vee-wop" },
                { "PrevSession_Touch",      instrumentName + " has touched the Previous Session vee-wop" },
                { "PrevSession_Approach",   instrumentName + " is approaching the Previous Session vee-wop" },
                { "HOD_Touch",              instrumentName + " has touched the high of day vee-wop" },
                { "HOD_Approach",           instrumentName + " is approaching the high of day vee-wop" },
                { "LOD_Touch",              instrumentName + " has touched the low of day vee-wop" },
                { "LOD_Approach",           instrumentName + " is approaching the low of day vee-wop" },
                { "Monthly_Touch",          instrumentName + " has touched the Monthly vee-wop" },
                { "Monthly_Approach",       instrumentName + " is approaching the Monthly vee-wop" },
                { "Yearly_Touch",           instrumentName + " has touched the Yearly vee-wop" },
                { "Yearly_Approach",        instrumentName + " is approaching the Yearly vee-wop" },
                { "HOY_Touch",              instrumentName + " has touched the high of year vee-wop" },
                { "HOY_Approach",           instrumentName + " is approaching the high of year vee-wop" },
                // Band alerts
                { "SessionUpperBand_Touch",     instrumentName + " has touched the Session vee-wop Upper Band" },
                { "SessionUpperBand_Approach",  instrumentName + " is approaching the Session vee-wop Upper Band" },
                { "SessionLowerBand_Touch",     instrumentName + " has touched the Session vee-wop Lower Band" },
                { "SessionLowerBand_Approach",  instrumentName + " is approaching the Session vee-wop Lower Band" },
                { "HODUpperBand_Touch",         instrumentName + " has touched the high of day vee-wop Upper Band" },
                { "HODUpperBand_Approach",      instrumentName + " is approaching the high of day vee-wop Upper Band" },
                { "HODLowerBand_Touch",         instrumentName + " has touched the high of day vee-wop Lower Band" },
                { "HODLowerBand_Approach",      instrumentName + " is approaching the high of day vee-wop Lower Band" },
                { "LODUpperBand_Touch",         instrumentName + " has touched the low of day vee-wop Upper Band" },
                { "LODUpperBand_Approach",      instrumentName + " is approaching the low of day vee-wop Upper Band" },
                { "LODLowerBand_Touch",         instrumentName + " has touched the low of day vee-wop Lower Band" },
                { "LODLowerBand_Approach",      instrumentName + " is approaching the low od day vee-wop Lower Band" },
            };
            
            int totalAlerts = alerts.Count;
            
            // Use a marker file to track voice settings
            string markerPath = Path.Combine(soundDir, "RTVWAP_" + instrumentName + "_voicesettings.txt");
            string currentSettings = "rate=" + VoiceAlertRate + "|neural=true|phonetic=v2";
            bool settingsChanged = true;
            
            if (File.Exists(markerPath))
            {
                try
                {
                    string savedSettings = File.ReadAllText(markerPath).Trim();
                    if (savedSettings == currentSettings)
                        settingsChanged = false;
                }
                catch { }
            }
            
            // Check if all files exist
            bool allExist = true;
            foreach (var kvp in alerts)
            {
                string fileName = "RTVWAP_" + instrumentName + "_" + kvp.Key + ".wav";
                if (!File.Exists(Path.Combine(soundDir, fileName)))
                {
                    allExist = false;
                    break;
                }
            }
            
            if (settingsChanged || !allExist)
            {
                // Delete old files so they regenerate cleanly
                foreach (var kvp in alerts)
                {
                    string fileName = "RTVWAP_" + instrumentName + "_" + kvp.Key + ".wav";
                    string filePath = Path.Combine(soundDir, fileName);
                    try { if (File.Exists(filePath)) File.Delete(filePath); } catch { }
                }
                
                Print("RedTail VWAP: Generating voice alerts for " + instrumentName + "...");
                
                // Try neural voices first via edge-tts
                bool neuralSuccess = TryGenerateNeuralVoiceAlerts(soundDir, alerts);
                
                if (!neuralSuccess)
                {
                    Print("RedTail VWAP: Neural voices not available, using SAPI5 with SSML.");
                    GenerateSAPIVoiceAlerts(soundDir, alerts);
                }
                
                // Save marker
                try { File.WriteAllText(markerPath, currentSettings); } catch { }
            }
            else
            {
                Print("RedTail VWAP: Voice alert files already cached for " + instrumentName + ".");
            }
            
            // Register all paths
            foreach (var kvp in alerts)
            {
                string fileName = "RTVWAP_" + instrumentName + "_" + kvp.Key + ".wav";
                string filePath = Path.Combine(soundDir, fileName);
                if (File.Exists(filePath))
                    voiceAlertPaths[kvp.Key] = filePath;
            }
            
            Print("RedTail VWAP: Voice alerts ready for " + instrumentName + " (" + voiceAlertPaths.Count + "/" + totalAlerts + " files)");
        }
        
        private bool TryGenerateNeuralVoiceAlerts(string soundDir, Dictionary<string, string> alerts)
        {
            try
            {
                Print("RedTail VWAP: Checking for edge-tts...");
                
                var checkPsi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "pip",
                    Arguments = "show edge-tts",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                bool edgeTtsInstalled = false;
                try
                {
                    using (var checkProc = System.Diagnostics.Process.Start(checkPsi))
                    {
                        string checkOut = checkProc.StandardOutput.ReadToEnd();
                        checkProc.WaitForExit(15000);
                        edgeTtsInstalled = checkOut.Contains("edge-tts");
                    }
                }
                catch { }
                
                if (!edgeTtsInstalled)
                {
                    Print("RedTail VWAP: Installing edge-tts via pip...");
                    var installPsi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "pip",
                        Arguments = "install edge-tts",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    
                    using (var installProc = System.Diagnostics.Process.Start(installPsi))
                    {
                        string installOut = installProc.StandardOutput.ReadToEnd();
                        string installErr = installProc.StandardError.ReadToEnd();
                        installProc.WaitForExit(60000);
                        Print("RedTail VWAP: pip install result: " + installOut);
                        if (!string.IsNullOrEmpty(installErr) && installErr.Contains("error"))
                        {
                            Print("RedTail VWAP: pip install error: " + installErr);
                            return false;
                        }
                    }
                }
                else
                {
                    Print("RedTail VWAP: edge-tts already installed.");
                }
                
                string voice = "en-US-JennyNeural";
                string rateStr = GetEdgeTtsRate();
                int successCount = 0;
                
                foreach (var kvp in alerts)
                {
                    string fileName = "RTVWAP_" + instrumentName + "_" + kvp.Key + ".wav";
                    string mp3Path = Path.Combine(soundDir, "RTVWAP_" + instrumentName + "_" + kvp.Key + ".mp3");
                    string wavPath = Path.Combine(soundDir, fileName);
                    string phrase = kvp.Value;
                    
                    try
                    {
                        var ttsPsi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = "edge-tts",
                            Arguments = "--voice " + voice + " --rate=" + rateStr + " --text \"" + phrase.Replace("\"", "'") + "\" --write-media \"" + mp3Path + "\"",
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        
                        using (var ttsProc = System.Diagnostics.Process.Start(ttsPsi))
                        {
                            string ttsOut = ttsProc.StandardOutput.ReadToEnd();
                            string ttsErr = ttsProc.StandardError.ReadToEnd();
                            ttsProc.WaitForExit(30000);
                            
                            if (File.Exists(mp3Path) && new FileInfo(mp3Path).Length > 500)
                            {
                                ConvertMp3ToWav(mp3Path, wavPath);
                                
                                if (File.Exists(wavPath) && new FileInfo(wavPath).Length > 1000)
                                {
                                    Print("RedTail VWAP Neural: OK: " + kvp.Key);
                                    successCount++;
                                }
                                else
                                {
                                    try { File.Copy(mp3Path, wavPath, true); } catch { }
                                    if (File.Exists(wavPath))
                                    {
                                        successCount++;
                                        Print("RedTail VWAP Neural: OK: " + kvp.Key + " (mp3 fallback)");
                                    }
                                }
                                
                                try { File.Delete(mp3Path); } catch { }
                            }
                            else
                            {
                                Print("RedTail VWAP Neural: FAIL: " + kvp.Key + " - edge-tts did not produce output");
                                if (!string.IsNullOrEmpty(ttsErr))
                                    Print("RedTail VWAP Neural Err: " + ttsErr.Substring(0, Math.Min(ttsErr.Length, 200)));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail VWAP Neural: FAIL: " + kvp.Key + ": " + ex.Message);
                    }
                }
                
                if (successCount > 0)
                {
                    Print("RedTail VWAP: Neural voice generation complete (" + successCount + "/" + alerts.Count + " files using " + voice + ").");
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                Print("RedTail VWAP neural voice error: " + ex.Message);
                return false;
            }
        }
        
        private void ConvertMp3ToWav(string mp3Path, string wavPath)
        {
            try
            {
                string psScript = @"
Add-Type -AssemblyName PresentationCore
$mediaPlayer = New-Object System.Windows.Media.MediaPlayer
$mediaPlayer.Open([Uri]::new('" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"'))
Start-Sleep -Milliseconds 500
$mediaPlayer.Close()

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue
if ($ffmpeg) {
    & ffmpeg -y -i '" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"' -acodec pcm_s16le -ar 22050 -ac 1 '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"' 2>&1 | Out-Null
    if (Test-Path '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"') {
        Write-Host 'CONVERTED_FFMPEG'
        exit 0
    }
}

Copy-Item '" + mp3Path.Replace("\\", "\\\\").Replace("'", "''") + @"' '" + wavPath.Replace("\\", "\\\\").Replace("'", "''") + @"'
Write-Host 'COPIED_MP3'
";
                string scriptPath = Path.Combine(Path.GetTempPath(), "rtvwap_convert.ps1");
                File.WriteAllText(scriptPath, psScript);
                
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(15000);
                    if (output.Contains("CONVERTED_FFMPEG"))
                        Print("RedTail VWAP: Converted to WAV via ffmpeg");
                    else
                        Print("RedTail VWAP: Using mp3 directly (ffmpeg not found)");
                }
                
                try { File.Delete(scriptPath); } catch { }
            }
            catch (Exception ex)
            {
                Print("RedTail VWAP mp3->wav conversion error: " + ex.Message);
                try { File.Copy(mp3Path, wavPath, true); } catch { }
            }
        }
        
        private string GetEdgeTtsRate()
        {
            int pct = VoiceAlertRate * 10;
            if (pct >= 0)
                return "+" + pct + "%";
            else
                return pct + "%";
        }
        
        private void GenerateSAPIVoiceAlerts(string soundDir, Dictionary<string, string> alerts)
        {
            using (var synth = new SpeechSynthesizer())
            {
                Print("RedTail VWAP SAPI voices available:");
                foreach (var voice in synth.GetInstalledVoices())
                {
                    if (voice.Enabled)
                        Print("  - " + voice.VoiceInfo.Name + " (" + voice.VoiceInfo.Gender + ", " + voice.VoiceInfo.Culture + ")");
                }
                
                foreach (var voice in synth.GetInstalledVoices())
                {
                    if (voice.VoiceInfo.Gender == VoiceGender.Female && voice.Enabled)
                    {
                        synth.SelectVoice(voice.VoiceInfo.Name);
                        Print("RedTail VWAP: Selected SAPI voice: " + voice.VoiceInfo.Name);
                        break;
                    }
                }
                
                synth.Rate = Math.Max(-10, Math.Min(10, VoiceAlertRate));
                
                foreach (var kvp in alerts)
                {
                    string fileName = "RTVWAP_" + instrumentName + "_" + kvp.Key + ".wav";
                    string filePath = Path.Combine(soundDir, fileName);
                    
                    if (File.Exists(filePath))
                        continue;
                    
                    try
                    {
                        synth.SetOutputToWaveFile(filePath);
                        synth.SpeakSsml(BuildSSML(kvp.Value));
                        synth.SetOutputToNull();
                        Print("RedTail VWAP SAPI: Generated " + fileName);
                    }
                    catch (Exception ex)
                    {
                        Print("RedTail VWAP SAPI failed '" + kvp.Key + "': " + ex.Message);
                        continue;
                    }
                }
            }
        }
        
        private string BuildSSML(string phrase)
        {
            string[] words = phrase.Split(' ');
            string instrument = words[0];
            string action = string.Join(" ", words, 1, words.Length - 1);
            
            return "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>"
                + "<prosody rate='" + GetSSMLRate() + "' pitch='+5%'>"
                + "<emphasis level='moderate'>" + instrument + "</emphasis>"
                + "<break time='350ms'/>"
                + action
                + "</prosody></speak>";
        }
        
        private string GetSSMLRate()
        {
            if (VoiceAlertRate <= -5) return "x-slow";
            if (VoiceAlertRate <= -2) return "slow";
            if (VoiceAlertRate <= 2)  return "medium";
            if (VoiceAlertRate <= 5)  return "fast";
            return "x-fast";
        }
        
        private string GetVoiceAlertPath(string alertKey, string fallbackSoundFile)
        {
            if (EnableVoiceAlerts && voiceAlertPaths.ContainsKey(alertKey))
                return voiceAlertPaths[alertKey];
            
            return ResolveSoundPath(fallbackSoundFile);
        }
        
        private string ResolveSoundPath(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (Path.IsPathRooted(raw)) return raw;
            
            var install = Path.Combine(NinjaTrader.Core.Globals.InstallDir, "sounds", raw);
            if (File.Exists(install)) return install;
            
            var user = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "sounds", raw);
            if (File.Exists(user)) return user;
            
            return raw;
        }
        
        #endregion

        #region OnRender
        
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);
            
            if (Bars == null || chartControl == null || ChartBars == null) return;
            
            RenderTarget renderTarget = RenderTarget;
            
            int firstBarVisible = ChartBars.FromIndex;
            int lastBarVisible = ChartBars.ToIndex;
            
            // ═══════════════════════════════════════════════
            // Render VWAPs
            // ═══════════════════════════════════════════════
            
            // NY Session VWAP
            if (ShowNyVwap && nyVwap.IsActive)
                RenderVwapLine(renderTarget, chartControl, chartScale, nyVwap, NyVwapColor, NyVwapStyle, 2, firstBarVisible, lastBarVisible, true);
            
            // Previous Day NY VWAP
            if (ShowPrevNyVwap && prevNyVwap.IsActive)
                RenderVwapLine(renderTarget, chartControl, chartScale, prevNyVwap, PrevNyVwapColor, PrevNyVwapStyle, 2, firstBarVisible, lastBarVisible, true);
            
            // Session VWAP
            if (ShowDayVwap && dayVwap.IsActive)
            {
                if (UseDynamicSessionColor)
                    RenderVwapLineDynamic(renderTarget, chartControl, chartScale, dayVwap, SessionVwapAboveColor, SessionVwapBelowColor, DayVwapStyle, 2, firstBarVisible, lastBarVisible);
                else
                    RenderVwapLine(renderTarget, chartControl, chartScale, dayVwap, DayVwapColor, DayVwapStyle, 2, firstBarVisible, lastBarVisible, false);
                
                if (ShowDayBands)
                {
                    for (int b = 1; b <= DayBandMult; b++)
                    {
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, dayVwap, b, true, DayBandColor, DayBandStyle, 1, firstBarVisible, lastBarVisible);
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, dayVwap, b, false, DayBandColor, DayBandStyle, 1, firstBarVisible, lastBarVisible);
                    }
                }
            }
            
            // Previous Session VWAP
            if (ShowPrevSessionVwap && prevSessionVwap.IsActive)
            {
                // Stop at the new session start (6 PM boundary)
                int prevSessionMaxBar = currentSessionAnchorBar > 0 ? currentSessionAnchorBar - 1 : -1;
                
                RenderVwapLine(renderTarget, chartControl, chartScale, prevSessionVwap, PrevSessionVwapColor, PrevSessionVwapStyle, 2, firstBarVisible, lastBarVisible, false, prevSessionMaxBar);
                
                if (ShowDayBands)
                {
                    for (int b = 1; b <= DayBandMult; b++)
                    {
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, prevSessionVwap, b, true, PrevSessionBandColor, PrevSessionBandStyle, 1, firstBarVisible, lastBarVisible, prevSessionMaxBar);
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, prevSessionVwap, b, false, PrevSessionBandColor, PrevSessionBandStyle, 1, firstBarVisible, lastBarVisible, prevSessionMaxBar);
                    }
                }
            }
            
            // HOD VWAP
            if (ShowHodVwap && hodVwap.IsActive)
            {
                RenderVwapLine(renderTarget, chartControl, chartScale, hodVwap, HodVwapColor, HodVwapStyle, 2, firstBarVisible, lastBarVisible, false);
                
                if (ShowHodBands)
                {
                    for (int b = 1; b <= HodBandMult; b++)
                    {
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, hodVwap, b, true, HodBandColor, HodBandStyle, 1, firstBarVisible, lastBarVisible);
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, hodVwap, b, false, HodBandColor, HodBandStyle, 1, firstBarVisible, lastBarVisible);
                    }
                }
            }
            
            // LOD VWAP
            if (ShowLodVwap && lodVwap.IsActive)
            {
                RenderVwapLine(renderTarget, chartControl, chartScale, lodVwap, LodVwapColor, LodVwapStyle, 2, firstBarVisible, lastBarVisible, false);
                
                if (ShowLodBands)
                {
                    for (int b = 1; b <= LodBandMult; b++)
                    {
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, lodVwap, b, true, LodBandColor, LodBandStyle, 1, firstBarVisible, lastBarVisible);
                        RenderVwapBandLine(renderTarget, chartControl, chartScale, lodVwap, b, false, LodBandColor, LodBandStyle, 1, firstBarVisible, lastBarVisible);
                    }
                }
            }
            
            // Month VWAP
            if (ShowMonthVwap && monthVwap.IsActive)
                RenderVwapLine(renderTarget, chartControl, chartScale, monthVwap, MonthVwapColor, MonthVwapStyle, 2, firstBarVisible, lastBarVisible, false);
            
            // Year VWAP
            if (ShowYearVwap && yearVwap.IsActive)
                RenderVwapLine(renderTarget, chartControl, chartScale, yearVwap, YearVwapColor, YearVwapStyle, 2, firstBarVisible, lastBarVisible, false);
            
            // HOY VWAP
            if (ShowHoyVwap && hoyVwap.IsActive)
                RenderVwapLine(renderTarget, chartControl, chartScale, hoyVwap, HoyVwapColor, HoyVwapStyle, 2, firstBarVisible, lastBarVisible, false);
            
            // ═══════════════════════════════════════════════
            // Render VWAP Labels
            // ═══════════════════════════════════════════════
            
            if (ShowVwapLabels)
            {
                if (ShowNyVwap && nyVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, nyVwap, NyVwapColor, "NY VWAP", VwapLabelFontSize, lastBarVisible);
                if (ShowPrevNyVwap && prevNyVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, prevNyVwap, PrevNyVwapColor, "Prev NY", VwapLabelFontSize, lastBarVisible);
                if (ShowDayVwap && dayVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, dayVwap, UseDynamicSessionColor ? SessionVwapAboveColor : DayVwapColor, "Session", VwapLabelFontSize, lastBarVisible);
                if (ShowPrevSessionVwap && prevSessionVwap.IsActive)
                {
                    int prevSessLabelBar = currentSessionAnchorBar > 0 ? Math.Min(currentSessionAnchorBar - 1, lastBarVisible) : lastBarVisible;
                    RenderVwapLabel(renderTarget, chartControl, chartScale, prevSessionVwap, PrevSessionVwapColor, "Prev Session", VwapLabelFontSize, prevSessLabelBar);
                }
                if (ShowHodVwap && hodVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, hodVwap, HodVwapColor, "HOD", VwapLabelFontSize, lastBarVisible);
                if (ShowLodVwap && lodVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, lodVwap, LodVwapColor, "LOD", VwapLabelFontSize, lastBarVisible);
                if (ShowMonthVwap && monthVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, monthVwap, MonthVwapColor, "Month", VwapLabelFontSize, lastBarVisible);
                if (ShowYearVwap && yearVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, yearVwap, YearVwapColor, "Year", VwapLabelFontSize, lastBarVisible);
                if (ShowHoyVwap && hoyVwap.IsActive)
                    RenderVwapLabel(renderTarget, chartControl, chartScale, hoyVwap, HoyVwapColor, "HOY", VwapLabelFontSize, lastBarVisible);
            }
            
            // ═══════════════════════════════════════════════
            // Render Ranges (with text merging for overlapping levels)
            // ═══════════════════════════════════════════════
            
            bool orActive = ShowNyOpeningRange && nyOpeningRange.High > 0;
            bool ibActive = ShowDayInitialBalance && dayInitialBalance.High > 0;
            
            // Determine merge threshold (half a tick or small pixel distance)
            double mergeTolerance = TickSize * 2;
            
            // Check for overlapping high/low between OR and IB
            bool highsMerged = false;
            bool lowsMerged = false;
            
            if (MergeOverlappingLevels && orActive && ibActive)
            {
                if (Math.Abs(nyOpeningRange.High - dayInitialBalance.High) <= mergeTolerance)
                    highsMerged = true;
                if (Math.Abs(nyOpeningRange.Low - dayInitialBalance.Low) <= mergeTolerance)
                    lowsMerged = true;
            }
            
            if (orActive)
            {
                string highLabel = highsMerged ? "OR High / IB High" : "OR High";
                string lowLabel = lowsMerged ? "OR Low / IB Low" : "OR Low";
                
                RenderRange(renderTarget, chartControl, chartScale, nyOpeningRange, NyRangeColor, NyRangeHighStyle, NyRangeLowStyle, 
                    NyRangeFillOpacity, NyRangeLineThickness, NyRangeLineOpacity, NyRangeFontSize, highLabel, lowLabel, ShowNyRangeText, firstBarVisible, lastBarVisible);
                
                foreach (var hr in nyRangeHistory)
                    RenderHistoricalRange(renderTarget, chartControl, chartScale, hr, NyRangeColor, NyRangeHighStyle, NyRangeLowStyle,
                        NyRangeFillOpacity, NyRangeLineThickness, NyRangeLineOpacity, NyRangeFontSize, "OR High", "OR Low", ShowNyRangeText, firstBarVisible, lastBarVisible);
            }
            
            if (ibActive)
            {
                // If merged, suppress IB text on the merged levels to avoid double labels
                string highLabel = highsMerged ? null : "IB High";
                string lowLabel = lowsMerged ? null : "IB Low";
                
                RenderRange(renderTarget, chartControl, chartScale, dayInitialBalance, DayIBColor, DayIBHighStyle, DayIBLowStyle, 
                    DayIBFillOpacity, DayIBLineThickness, DayIBLineOpacity, DayIBFontSize, highLabel, lowLabel, ShowIBText, firstBarVisible, lastBarVisible);
                
                foreach (var hr in dayRangeHistory)
                    RenderHistoricalRange(renderTarget, chartControl, chartScale, hr, DayIBColor, DayIBHighStyle, DayIBLowStyle,
                        DayIBFillOpacity, DayIBLineThickness, DayIBLineOpacity, DayIBFontSize, "IB High", "IB Low", ShowIBText, firstBarVisible, lastBarVisible);
            }
        }
        
        private void RenderVwapLine(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            VwapData vwap, System.Windows.Media.Brush brush, DashStyleHelper dashStyle, float width,
            int firstBar, int lastBar, bool nySessionOnly, int maxBar = -1)
        {
            if (!vwap.IsActive || vwap.AnchorBar < 0) return;
            
            int startBar = Math.Max(vwap.AnchorBar, firstBar);
            int endBar = Math.Min(CurrentBar, lastBar);
            if (maxBar >= 0) endBar = Math.Min(endBar, maxBar);
            
            if (startBar >= endBar) return;
            
            SharpDX.Color4 color4 = BrushToColor4(brush);
            
            // Enable antialiasing for smooth lines
            var oldAA = renderTarget.AntialiasMode;
            renderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            
            using (var sdxBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, color4))
            using (var strokeStyle = GetStrokeStyle(renderTarget, dashStyle))
            {
                // Build list of points, then draw as a single PathGeometry for smoothness
                var segments = new List<List<SharpDX.Vector2>>();
                var currentSegment = new List<SharpDX.Vector2>();
                
                double cumVol = 0;
                double cumTypVol = 0;
                
                for (int i = vwap.AnchorBar; i <= endBar; i++)
                {
                    int barsAgo = CurrentBar - i;
                    if (barsAgo < 0 || barsAgo >= Bars.Count) continue;
                    
                    double vol = Volume.GetValueAt(i);
                    double src = (Open.GetValueAt(i) + High.GetValueAt(i) + Low.GetValueAt(i) + Close.GetValueAt(i)) / 4.0;
                    
                    cumVol += vol;
                    cumTypVol += src * vol;
                    
                    if (cumVol <= 0) continue;
                    
                    double vwapVal = cumTypVol / cumVol;
                    
                    // For NY session VWAPs, break segments outside session
                    if (nySessionOnly)
                    {
                        DateTime barTimeEst = TimeZoneInfo.ConvertTime(Bars.GetTime(i), estZone);
                        TimeSpan tod = barTimeEst.TimeOfDay;
                        
                        // Estimate bar open time for proper multi-timeframe support
                        TimeSpan openTod = tod;
                        if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute)
                            openTod = tod - TimeSpan.FromMinutes(BarsPeriod.Value);
                        else if (BarsPeriod.BarsPeriodType == BarsPeriodType.Second)
                            openTod = tod - TimeSpan.FromSeconds(BarsPeriod.Value);
                        
                        bool inSession = openTod >= new TimeSpan(9, 30, 0) && openTod < new TimeSpan(16, 0, 0);
                        
                        if (!inSession)
                        {
                            if (currentSegment.Count > 1)
                                segments.Add(currentSegment);
                            currentSegment = new List<SharpDX.Vector2>();
                            continue;
                        }
                    }
                    
                    if (i < firstBar) continue;
                    
                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    float y = chartScale.GetYByValue(vwapVal);
                    
                    currentSegment.Add(new SharpDX.Vector2(x, y));
                }
                
                if (currentSegment.Count > 1)
                    segments.Add(currentSegment);
                
                // Draw each segment as a PathGeometry for smooth rendering
                foreach (var segment in segments)
                {
                    if (segment.Count < 2) continue;
                    
                    using (var path = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sink = path.Open())
                        {
                            sink.BeginFigure(segment[0], SharpDX.Direct2D1.FigureBegin.Hollow);
                            
                            for (int p = 1; p < segment.Count; p++)
                                sink.AddLine(segment[p]);
                            
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        
                        renderTarget.DrawGeometry(path, sdxBrush, width, strokeStyle);
                    }
                }
            }
            
            renderTarget.AntialiasMode = oldAA;
        }
        
        private void RenderVwapBandLine(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            VwapData vwap, double multiplier, bool isUpper, System.Windows.Media.Brush brush, DashStyleHelper dashStyle, 
            float width, int firstBar, int lastBar, int maxBar = -1)
        {
            if (!vwap.IsActive || vwap.AnchorBar < 0) return;
            
            int startBar = Math.Max(vwap.AnchorBar, firstBar);
            int endBar = Math.Min(CurrentBar, lastBar);
            if (maxBar >= 0) endBar = Math.Min(endBar, maxBar);
            
            if (startBar >= endBar) return;
            
            SharpDX.Color4 color4 = BrushToColor4(brush);
            
            var oldAA = renderTarget.AntialiasMode;
            renderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            
            using (var sdxBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, color4))
            using (var strokeStyle = GetStrokeStyle(renderTarget, dashStyle))
            {
                var points = new List<SharpDX.Vector2>();
                
                double cumVol = 0;
                double cumTypVol = 0;
                double cumVolSq = 0;
                
                for (int i = vwap.AnchorBar; i <= endBar; i++)
                {
                    int barsAgo = CurrentBar - i;
                    if (barsAgo < 0 || barsAgo >= Bars.Count) continue;
                    
                    double vol = Volume.GetValueAt(i);
                    double src = (Open.GetValueAt(i) + High.GetValueAt(i) + Low.GetValueAt(i) + Close.GetValueAt(i)) / 4.0;
                    
                    cumVol += vol;
                    cumTypVol += src * vol;
                    cumVolSq += src * src * vol;
                    
                    if (cumVol <= 0) continue;
                    
                    double vwapVal = cumTypVol / cumVol;
                    double variance = (cumVolSq / cumVol) - (vwapVal * vwapVal);
                    double stdDev = variance > 0 ? Math.Sqrt(variance) : 0;
                    double bandVal = isUpper ? vwapVal + stdDev * multiplier : vwapVal - stdDev * multiplier;
                    
                    if (i < firstBar) continue;
                    
                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    float y = chartScale.GetYByValue(bandVal);
                    
                    points.Add(new SharpDX.Vector2(x, y));
                }
                
                if (points.Count >= 2)
                {
                    using (var path = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sink = path.Open())
                        {
                            sink.BeginFigure(points[0], SharpDX.Direct2D1.FigureBegin.Hollow);
                            
                            for (int p = 1; p < points.Count; p++)
                                sink.AddLine(points[p]);
                            
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        
                        renderTarget.DrawGeometry(path, sdxBrush, width, strokeStyle);
                    }
                }
            }
            
            renderTarget.AntialiasMode = oldAA;
        }
        
        private void RenderVwapLineDynamic(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            VwapData vwap, System.Windows.Media.Brush aboveBrush, System.Windows.Media.Brush belowBrush, 
            DashStyleHelper dashStyle, float width, int firstBar, int lastBar)
        {
            if (!vwap.IsActive || vwap.AnchorBar < 0) return;
            
            int startBar = Math.Max(vwap.AnchorBar, firstBar);
            int endBar = Math.Min(CurrentBar, lastBar);
            
            if (startBar >= endBar) return;
            
            SharpDX.Color4 aboveColor = BrushToColor4(aboveBrush);
            SharpDX.Color4 belowColor = BrushToColor4(belowBrush);
            
            var oldAA = renderTarget.AntialiasMode;
            renderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;
            
            using (var aboveSdxBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, aboveColor))
            using (var belowSdxBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, belowColor))
            using (var strokeStyle = GetStrokeStyle(renderTarget, dashStyle))
            {
                // Build colored segments: each segment has a single color based on price vs VWAP
                var aboveSegments = new List<List<SharpDX.Vector2>>();
                var belowSegments = new List<List<SharpDX.Vector2>>();
                
                var currentAbove = new List<SharpDX.Vector2>();
                var currentBelow = new List<SharpDX.Vector2>();
                bool? wasAbove = null;
                
                double cumVol = 0;
                double cumTypVol = 0;
                
                for (int i = vwap.AnchorBar; i <= endBar; i++)
                {
                    int barsAgo = CurrentBar - i;
                    if (barsAgo < 0 || barsAgo >= Bars.Count) continue;
                    
                    double vol = Volume.GetValueAt(i);
                    double src = (Open.GetValueAt(i) + High.GetValueAt(i) + Low.GetValueAt(i) + Close.GetValueAt(i)) / 4.0;
                    double closePrice = Close.GetValueAt(i);
                    
                    cumVol += vol;
                    cumTypVol += src * vol;
                    
                    if (cumVol <= 0) continue;
                    
                    double vwapVal = cumTypVol / cumVol;
                    
                    if (i < firstBar) continue;
                    
                    float x = chartControl.GetXByBarIndex(ChartBars, i);
                    float y = chartScale.GetYByValue(vwapVal);
                    var point = new SharpDX.Vector2(x, y);
                    
                    bool isAbove = closePrice >= vwapVal;
                    
                    if (wasAbove.HasValue && isAbove != wasAbove.Value)
                    {
                        // Color changed — end current segment with this point, start new one from this point
                        if (wasAbove.Value)
                        {
                            currentAbove.Add(point);
                            if (currentAbove.Count > 1) aboveSegments.Add(currentAbove);
                            currentAbove = new List<SharpDX.Vector2>();
                        }
                        else
                        {
                            currentBelow.Add(point);
                            if (currentBelow.Count > 1) belowSegments.Add(currentBelow);
                            currentBelow = new List<SharpDX.Vector2>();
                        }
                    }
                    
                    if (isAbove)
                        currentAbove.Add(point);
                    else
                        currentBelow.Add(point);
                    
                    wasAbove = isAbove;
                }
                
                if (currentAbove.Count > 1) aboveSegments.Add(currentAbove);
                if (currentBelow.Count > 1) belowSegments.Add(currentBelow);
                
                // Draw above segments
                foreach (var segment in aboveSegments)
                {
                    if (segment.Count < 2) continue;
                    using (var path = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sink = path.Open())
                        {
                            sink.BeginFigure(segment[0], SharpDX.Direct2D1.FigureBegin.Hollow);
                            for (int p = 1; p < segment.Count; p++) sink.AddLine(segment[p]);
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        renderTarget.DrawGeometry(path, aboveSdxBrush, width, strokeStyle);
                    }
                }
                
                // Draw below segments
                foreach (var segment in belowSegments)
                {
                    if (segment.Count < 2) continue;
                    using (var path = new SharpDX.Direct2D1.PathGeometry(renderTarget.Factory))
                    {
                        using (var sink = path.Open())
                        {
                            sink.BeginFigure(segment[0], SharpDX.Direct2D1.FigureBegin.Hollow);
                            for (int p = 1; p < segment.Count; p++) sink.AddLine(segment[p]);
                            sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
                            sink.Close();
                        }
                        renderTarget.DrawGeometry(path, belowSdxBrush, width, strokeStyle);
                    }
                }
            }
            
            renderTarget.AntialiasMode = oldAA;
        }
        
        private void RenderRange(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            RangeData range, System.Windows.Media.Brush brush, DashStyleHelper highDashStyle, DashStyleHelper lowDashStyle, int fillOpacity,
            int lineThickness, int lineOpacity, int fontSize,
            string highLabel, string lowLabel, bool showText, int firstBar, int lastBar)
        {
            if (range.High <= 0) return;
            
            int startBar = Math.Max(range.StartBar, firstBar);
            int endBar = Math.Min(range.EndBar, lastBar);
            
            if (startBar > lastBar || endBar < firstBar) return;
            
            startBar = Math.Max(startBar, firstBar);
            endBar = Math.Min(endBar, lastBar);
            
            float x1 = chartControl.GetXByBarIndex(ChartBars, startBar);
            float x2 = chartControl.GetXByBarIndex(ChartBars, endBar);
            float yHigh = chartScale.GetYByValue(range.High);
            float yLow = chartScale.GetYByValue(range.Low);
            
            float lineAlpha = lineOpacity / 100f;
            SharpDX.Color4 baseColor = BrushToColor4(brush);
            SharpDX.Color4 lineColor = new SharpDX.Color4(baseColor.Red, baseColor.Green, baseColor.Blue, baseColor.Alpha * lineAlpha);
            float fillAlpha = fillOpacity / 100f;
            SharpDX.Color4 fillColor = new SharpDX.Color4(baseColor.Red, baseColor.Green, baseColor.Blue, (1f - fillAlpha) * baseColor.Alpha);
            
            using (var lineBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, lineColor))
            using (var fillBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, fillColor))
            using (var highStrokeStyle = GetStrokeStyle(renderTarget, highDashStyle))
            using (var lowStrokeStyle = GetStrokeStyle(renderTarget, lowDashStyle))
            {
                // Fill (only if not fully transparent)
                if (fillOpacity < 100)
                {
                    renderTarget.FillRectangle(
                        new SharpDX.RectangleF(x1, yHigh, x2 - x1, yLow - yHigh),
                        fillBrush);
                }
                
                // High line
                renderTarget.DrawLine(new SharpDX.Vector2(x1, yHigh), new SharpDX.Vector2(x2, yHigh), lineBrush, lineThickness, highStrokeStyle);
                // Low line
                renderTarget.DrawLine(new SharpDX.Vector2(x1, yLow), new SharpDX.Vector2(x2, yLow), lineBrush, lineThickness, lowStrokeStyle);
                
                // Text labels at right edge of each line
                if (showText)
                {
                    using (var textFormat = new SharpDX.DirectWrite.TextFormat(
                        NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", fontSize))
                    {
                        textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                        
                        float textOffset = 4f;
                        float textHeight = fontSize + 4f;
                        
                        if (!string.IsNullOrEmpty(highLabel))
                            renderTarget.DrawText(highLabel, textFormat,
                                new SharpDX.RectangleF(x2 + textOffset, yHigh - textHeight / 2f, 150, textHeight),
                                lineBrush);
                        
                        if (!string.IsNullOrEmpty(lowLabel))
                            renderTarget.DrawText(lowLabel, textFormat,
                                new SharpDX.RectangleF(x2 + textOffset, yLow - textHeight / 2f, 150, textHeight),
                                lineBrush);
                    }
                }
            }
        }
        
        private void RenderHistoricalRange(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            HistoricalRange range, System.Windows.Media.Brush brush, DashStyleHelper highDashStyle, DashStyleHelper lowDashStyle, int fillOpacity,
            int lineThickness, int lineOpacity, int fontSize,
            string highLabel, string lowLabel, bool showText, int firstBar, int lastBar)
        {
            RangeData tempRange = new RangeData
            {
                High = range.High,
                Low = range.Low,
                StartBar = range.StartBar,
                EndBar = range.EndBar,
                IsForming = false
            };
            
            RenderRange(renderTarget, chartControl, chartScale, tempRange, brush, highDashStyle, lowDashStyle, fillOpacity, lineThickness, lineOpacity, fontSize, highLabel, lowLabel, showText, firstBar, lastBar);
        }
        
        private void RenderVwapLabel(RenderTarget renderTarget, ChartControl chartControl, ChartScale chartScale,
            VwapData vwap, System.Windows.Media.Brush brush, string label, int fontSize, int lastBar)
        {
            if (!vwap.IsActive || vwap.AnchorBar < 0 || string.IsNullOrEmpty(label)) return;
            
            int endBar = Math.Min(CurrentBar, lastBar);
            if (endBar < vwap.AnchorBar) return;
            
            // Recalculate VWAP value at the last visible bar
            double cumVol = 0;
            double cumTypVol = 0;
            for (int i = vwap.AnchorBar; i <= endBar; i++)
            {
                int barsAgo = CurrentBar - i;
                if (barsAgo < 0 || barsAgo >= Bars.Count) continue;
                double vol = Volume.GetValueAt(i);
                double src = (Open.GetValueAt(i) + High.GetValueAt(i) + Low.GetValueAt(i) + Close.GetValueAt(i)) / 4.0;
                cumVol += vol;
                cumTypVol += src * vol;
            }
            if (cumVol <= 0) return;
            double vwapVal = cumTypVol / cumVol;
            
            float x = chartControl.GetXByBarIndex(ChartBars, endBar);
            float y = chartScale.GetYByValue(vwapVal);
            
            SharpDX.Color4 color4 = BrushToColor4(brush);
            
            using (var sdxBrush = new SharpDX.Direct2D1.SolidColorBrush(renderTarget, color4))
            using (var textFormat = new SharpDX.DirectWrite.TextFormat(
                NinjaTrader.Core.Globals.DirectWriteFactory, "Arial", fontSize))
            {
                textFormat.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;
                float textHeight = fontSize + 4f;
                renderTarget.DrawText(label, textFormat,
                    new SharpDX.RectangleF(x + 6f, y - textHeight / 2f, 200, textHeight),
                    sdxBrush);
            }
        }
        
        #endregion

        #region Properties
        
        // ═══════════════════════════════════════════════
        // NY Session VWAP
        // ═══════════════════════════════════════════════
        
        [NinjaScriptProperty]
        [Display(Name = "Show NY Session VWAP", Order = 1, GroupName = "1. NY Session VWAPs")]
        public bool ShowNyVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "NY VWAP Color", Order = 2, GroupName = "1. NY Session VWAPs")]
        public System.Windows.Media.Brush NyVwapColor { get; set; }
        
        [Browsable(false)]
        public string NyVwapColorSerialize
        {
            get { return Serialize.BrushToString(NyVwapColor); }
            set { NyVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "NY VWAP Line Style", Order = 3, GroupName = "1. NY Session VWAPs")]
        public DashStyleHelper NyVwapStyle { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical NY VWAPs", Order = 4, GroupName = "1. NY Session VWAPs")]
        public int NyVwapHistoryCount { get; set; }
        
        // Previous Day NY VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show Prev Day NY VWAP", Order = 5, GroupName = "1. NY Session VWAPs")]
        public bool ShowPrevNyVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Prev NY VWAP Color", Order = 6, GroupName = "1. NY Session VWAPs")]
        public System.Windows.Media.Brush PrevNyVwapColor { get; set; }
        
        [Browsable(false)]
        public string PrevNyVwapColorSerialize
        {
            get { return Serialize.BrushToString(PrevNyVwapColor); }
            set { PrevNyVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Prev NY VWAP Line Style", Order = 7, GroupName = "1. NY Session VWAPs")]
        public DashStyleHelper PrevNyVwapStyle { get; set; }
        
        // ═══════════════════════════════════════════════
        // Day VWAPs
        // ═══════════════════════════════════════════════
        
        [NinjaScriptProperty]
        [Display(Name = "Show Session VWAP", Order = 1, GroupName = "2. Session VWAP")]
        public bool ShowDayVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Session VWAP Color", Order = 2, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush DayVwapColor { get; set; }
        
        [Browsable(false)]
        public string DayVwapColorSerialize
        {
            get { return Serialize.BrushToString(DayVwapColor); }
            set { DayVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Session VWAP Line Style", Order = 3, GroupName = "2. Session VWAP")]
        public DashStyleHelper DayVwapStyle { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical Session VWAPs", Order = 4, GroupName = "2. Session VWAP")]
        public int DayVwapHistoryCount { get; set; }
        
        [Display(Name = "Dynamic Color (Price vs VWAP)", Order = 5, GroupName = "2. Session VWAP")]
        public bool UseDynamicSessionColor { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Above VWAP Color", Order = 6, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush SessionVwapAboveColor { get; set; }
        
        [Browsable(false)]
        public string SessionVwapAboveColorSerialize
        {
            get { return Serialize.BrushToString(SessionVwapAboveColor); }
            set { SessionVwapAboveColor = Serialize.StringToBrush(value); }
        }
        
        [XmlIgnore]
        [Display(Name = "Below VWAP Color", Order = 7, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush SessionVwapBelowColor { get; set; }
        
        [Browsable(false)]
        public string SessionVwapBelowColorSerialize
        {
            get { return Serialize.BrushToString(SessionVwapBelowColor); }
            set { SessionVwapBelowColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Show Session Bands", Order = 8, GroupName = "2. Session VWAP")]
        public bool ShowDayBands { get; set; }
        
        [Range(1, 5)]
        [Display(Name = "Number of Bands", Order = 9, GroupName = "2. Session VWAP")]
        public int DayBandMult { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Session Band Color", Order = 10, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush DayBandColor { get; set; }
        
        [Browsable(false)]
        public string DayBandColorSerialize
        {
            get { return Serialize.BrushToString(DayBandColor); }
            set { DayBandColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Session Band Line Style", Order = 11, GroupName = "2. Session VWAP")]
        public DashStyleHelper DayBandStyle { get; set; }
        
        // Previous Session VWAP
        [Display(Name = "Show Prev Session VWAP", Order = 12, GroupName = "2. Session VWAP")]
        public bool ShowPrevSessionVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Prev Session VWAP Color", Order = 13, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush PrevSessionVwapColor { get; set; }
        
        [Browsable(false)]
        public string PrevSessionVwapColorSerialize
        {
            get { return Serialize.BrushToString(PrevSessionVwapColor); }
            set { PrevSessionVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Prev Session VWAP Line Style", Order = 14, GroupName = "2. Session VWAP")]
        public DashStyleHelper PrevSessionVwapStyle { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Prev Session Band Color", Order = 15, GroupName = "2. Session VWAP")]
        public System.Windows.Media.Brush PrevSessionBandColor { get; set; }
        
        [Browsable(false)]
        public string PrevSessionBandColorSerialize
        {
            get { return Serialize.BrushToString(PrevSessionBandColor); }
            set { PrevSessionBandColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Prev Session Band Line Style", Order = 16, GroupName = "2. Session VWAP")]
        public DashStyleHelper PrevSessionBandStyle { get; set; }
        
        // HOD VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show HOD VWAP", Order = 1, GroupName = "3. Day VWAPs")]
        public bool ShowHodVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "HOD VWAP Color", Order = 2, GroupName = "3. Day VWAPs")]
        public System.Windows.Media.Brush HodVwapColor { get; set; }
        
        [Browsable(false)]
        public string HodVwapColorSerialize
        {
            get { return Serialize.BrushToString(HodVwapColor); }
            set { HodVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "HOD VWAP Line Style", Order = 3, GroupName = "3. Day VWAPs")]
        public DashStyleHelper HodVwapStyle { get; set; }
        
        [Display(Name = "Show HOD Bands", Order = 4, GroupName = "3. Day VWAPs")]
        public bool ShowHodBands { get; set; }
        
        [Range(1, 5)]
        [Display(Name = "Number of HOD Bands", Order = 5, GroupName = "3. Day VWAPs")]
        public int HodBandMult { get; set; }
        
        [XmlIgnore]
        [Display(Name = "HOD Band Color", Order = 6, GroupName = "3. Day VWAPs")]
        public System.Windows.Media.Brush HodBandColor { get; set; }
        
        [Browsable(false)]
        public string HodBandColorSerialize
        {
            get { return Serialize.BrushToString(HodBandColor); }
            set { HodBandColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "HOD Band Line Style", Order = 7, GroupName = "3. Day VWAPs")]
        public DashStyleHelper HodBandStyle { get; set; }
        
        // LOD VWAP
        [NinjaScriptProperty]
        [Display(Name = "Show LOD VWAP", Order = 8, GroupName = "3. Day VWAPs")]
        public bool ShowLodVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "LOD VWAP Color", Order = 9, GroupName = "3. Day VWAPs")]
        public System.Windows.Media.Brush LodVwapColor { get; set; }
        
        [Browsable(false)]
        public string LodVwapColorSerialize
        {
            get { return Serialize.BrushToString(LodVwapColor); }
            set { LodVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "LOD VWAP Line Style", Order = 10, GroupName = "3. Day VWAPs")]
        public DashStyleHelper LodVwapStyle { get; set; }
        
        [Display(Name = "Show LOD Bands", Order = 11, GroupName = "3. Day VWAPs")]
        public bool ShowLodBands { get; set; }
        
        [Range(1, 5)]
        [Display(Name = "Number of LOD Bands", Order = 12, GroupName = "3. Day VWAPs")]
        public int LodBandMult { get; set; }
        
        [XmlIgnore]
        [Display(Name = "LOD Band Color", Order = 13, GroupName = "3. Day VWAPs")]
        public System.Windows.Media.Brush LodBandColor { get; set; }
        
        [Browsable(false)]
        public string LodBandColorSerialize
        {
            get { return Serialize.BrushToString(LodBandColor); }
            set { LodBandColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "LOD Band Line Style", Order = 14, GroupName = "3. Day VWAPs")]
        public DashStyleHelper LodBandStyle { get; set; }
        
        // ═══════════════════════════════════════════════
        // Higher Timeframe VWAPs
        // ═══════════════════════════════════════════════
        
        [NinjaScriptProperty]
        [Display(Name = "Show Month VWAP", Order = 1, GroupName = "4. Monthly & Yearly VWAPs")]
        public bool ShowMonthVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Month VWAP Color", Order = 2, GroupName = "4. Monthly & Yearly VWAPs")]
        public System.Windows.Media.Brush MonthVwapColor { get; set; }
        
        [Browsable(false)]
        public string MonthVwapColorSerialize
        {
            get { return Serialize.BrushToString(MonthVwapColor); }
            set { MonthVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Month VWAP Line Style", Order = 3, GroupName = "4. Monthly & Yearly VWAPs")]
        public DashStyleHelper MonthVwapStyle { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical Month VWAPs", Order = 4, GroupName = "4. Monthly & Yearly VWAPs")]
        public int MonthVwapHistoryCount { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show Year VWAP", Order = 5, GroupName = "4. Monthly & Yearly VWAPs")]
        public bool ShowYearVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Year VWAP Color", Order = 6, GroupName = "4. Monthly & Yearly VWAPs")]
        public System.Windows.Media.Brush YearVwapColor { get; set; }
        
        [Browsable(false)]
        public string YearVwapColorSerialize
        {
            get { return Serialize.BrushToString(YearVwapColor); }
            set { YearVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "Year VWAP Line Style", Order = 7, GroupName = "4. Monthly & Yearly VWAPs")]
        public DashStyleHelper YearVwapStyle { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical Year VWAPs", Order = 8, GroupName = "4. Monthly & Yearly VWAPs")]
        public int YearVwapHistoryCount { get; set; }
        
        [NinjaScriptProperty]
        [Display(Name = "Show HOY VWAP", Order = 9, GroupName = "4. Monthly & Yearly VWAPs")]
        public bool ShowHoyVwap { get; set; }
        
        [XmlIgnore]
        [Display(Name = "HOY VWAP Color", Order = 10, GroupName = "4. Monthly & Yearly VWAPs")]
        public System.Windows.Media.Brush HoyVwapColor { get; set; }
        
        [Browsable(false)]
        public string HoyVwapColorSerialize
        {
            get { return Serialize.BrushToString(HoyVwapColor); }
            set { HoyVwapColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "HOY VWAP Line Style", Order = 11, GroupName = "4. Monthly & Yearly VWAPs")]
        public DashStyleHelper HoyVwapStyle { get; set; }
        
        // ═══════════════════════════════════════════════
        // NY Opening Range
        // ═══════════════════════════════════════════════
        
        [NinjaScriptProperty]
        [Display(Name = "Show NY Opening Range", Order = 1, GroupName = "5. NY Opening Range")]
        public bool ShowNyOpeningRange { get; set; }
        
        [Display(Name = "Opening Range Start (EST)", Order = 2, GroupName = "5. NY Opening Range")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime NyOpeningRangeStart { get; set; }
        
        [Display(Name = "Opening Range End (EST)", Order = 3, GroupName = "5. NY Opening Range")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime NyOpeningRangeEnd { get; set; }
        
        [XmlIgnore]
        [Display(Name = "Range Line Color", Order = 4, GroupName = "5. NY Opening Range")]
        public System.Windows.Media.Brush NyRangeColor { get; set; }
        
        [Browsable(false)]
        public string NyRangeColorSerialize
        {
            get { return Serialize.BrushToString(NyRangeColor); }
            set { NyRangeColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "High Line Style", Order = 5, GroupName = "5. NY Opening Range")]
        public DashStyleHelper NyRangeHighStyle { get; set; }
        
        [Display(Name = "Low Line Style", Order = 6, GroupName = "5. NY Opening Range")]
        public DashStyleHelper NyRangeLowStyle { get; set; }
        
        [Range(1, 10)]
        [Display(Name = "Range Line Thickness", Order = 7, GroupName = "5. NY Opening Range")]
        public int NyRangeLineThickness { get; set; }
        
        [Range(0, 100)]
        [Display(Name = "Line Opacity (0=transparent, 100=solid)", Order = 8, GroupName = "5. NY Opening Range")]
        public int NyRangeLineOpacity { get; set; }
        
        [Range(0, 100)]
        [Display(Name = "Fill Opacity (0=solid, 100=transparent)", Order = 9, GroupName = "5. NY Opening Range")]
        public int NyRangeFillOpacity { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical Opening Ranges", Order = 10, GroupName = "5. NY Opening Range")]
        public int NyRangeHistoryCount { get; set; }
        
        [Display(Name = "Show Text Label", Order = 11, GroupName = "5. NY Opening Range")]
        public bool ShowNyRangeText { get; set; }
        
        [Range(6, 30)]
        [Display(Name = "Font Size", Order = 12, GroupName = "5. NY Opening Range")]
        public int NyRangeFontSize { get; set; }
        
        // ═══════════════════════════════════════════════
        // Day Initial Balance
        // ═══════════════════════════════════════════════
        
        [NinjaScriptProperty]
        [Display(Name = "Show Day Initial Balance", Order = 1, GroupName = "6. Day Initial Balance")]
        public bool ShowDayInitialBalance { get; set; }
        
        [Display(Name = "IB Range Start (EST)", Order = 2, GroupName = "6. Day Initial Balance")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime DayIBStart { get; set; }
        
        [Display(Name = "IB Range End (EST)", Order = 3, GroupName = "6. Day Initial Balance")]
        [PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
        public DateTime DayIBEnd { get; set; }
        
        [XmlIgnore]
        [Display(Name = "IB Line Color", Order = 4, GroupName = "6. Day Initial Balance")]
        public System.Windows.Media.Brush DayIBColor { get; set; }
        
        [Browsable(false)]
        public string DayIBColorSerialize
        {
            get { return Serialize.BrushToString(DayIBColor); }
            set { DayIBColor = Serialize.StringToBrush(value); }
        }
        
        [Display(Name = "IB High Line Style", Order = 5, GroupName = "6. Day Initial Balance")]
        public DashStyleHelper DayIBHighStyle { get; set; }
        
        [Display(Name = "IB Low Line Style", Order = 6, GroupName = "6. Day Initial Balance")]
        public DashStyleHelper DayIBLowStyle { get; set; }
        
        [Range(1, 10)]
        [Display(Name = "IB Line Thickness", Order = 7, GroupName = "6. Day Initial Balance")]
        public int DayIBLineThickness { get; set; }
        
        [Range(0, 100)]
        [Display(Name = "Line Opacity (0=transparent, 100=solid)", Order = 8, GroupName = "6. Day Initial Balance")]
        public int DayIBLineOpacity { get; set; }
        
        [Range(0, 100)]
        [Display(Name = "Fill Opacity (0=solid, 100=transparent)", Order = 9, GroupName = "6. Day Initial Balance")]
        public int DayIBFillOpacity { get; set; }
        
        [Range(0, int.MaxValue)]
        [Display(Name = "Historical Balance Ranges", Order = 10, GroupName = "6. Day Initial Balance")]
        public int DayIBHistoryCount { get; set; }
        
        [Display(Name = "Show Text Label", Order = 11, GroupName = "6. Day Initial Balance")]
        public bool ShowIBText { get; set; }
        
        [Range(6, 30)]
        [Display(Name = "Font Size", Order = 12, GroupName = "6. Day Initial Balance")]
        public int DayIBFontSize { get; set; }
        
        // ═══════════════════════════════════════════════
        // General Display Settings
        // ═══════════════════════════════════════════════
        
        [Display(Name = "Merge Overlapping OR/IB Levels", Order = 1, GroupName = "7. General Display")]
        public bool MergeOverlappingLevels { get; set; }
        
        [Display(Name = "Show VWAP Labels", Order = 2, GroupName = "7. General Display")]
        public bool ShowVwapLabels { get; set; }
        
        [Range(6, 30)]
        [Display(Name = "VWAP Label Font Size", Order = 3, GroupName = "7. General Display")]
        public int VwapLabelFontSize { get; set; }
        
        // ═══════════════════════════════════════════════
        // Voice Alerts
        // ═══════════════════════════════════════════════
        
        [Display(Name = "Enable Voice Alerts", Description = "Auto-generate spoken alerts with instrument name (e.g., 'MNQ has touched the Session VWAP'). Uses edge-tts neural voice with SAPI5 fallback.", Order = 1, GroupName = "8. Voice Alerts")]
        public bool EnableVoiceAlerts { get; set; }
        
        [Range(-10, 10)]
        [Display(Name = "Voice Speed", Description = "Speech rate (-10 slowest to 10 fastest, 0 normal, 2 recommended)", Order = 2, GroupName = "8. Voice Alerts")]
        public int VoiceAlertRate { get; set; }
        
        [Display(Name = "Alert on VWAP Touch", Description = "Alert when price bar touches/crosses a VWAP level", Order = 3, GroupName = "8. Voice Alerts")]
        public bool AlertOnTouch { get; set; }
        
        [Display(Name = "Alert on VWAP Approach", Description = "Alert when price is within approach distance of a VWAP", Order = 4, GroupName = "8. Voice Alerts")]
        public bool AlertOnApproach { get; set; }
        
        [Range(1, 50)]
        [Display(Name = "Approach Distance (Ticks)", Description = "How close price needs to be to trigger an approach alert", Order = 5, GroupName = "8. Voice Alerts")]
        public int ApproachTicks { get; set; }
        
        [Range(5, 300)]
        [Display(Name = "Alert Cooldown (Seconds)", Description = "Minimum time between repeated alerts for the same VWAP", Order = 6, GroupName = "8. Voice Alerts")]
        public int AlertCooldownSeconds { get; set; }
        
        [Display(Name = "Fallback Sound File", Description = "Sound file used if voice generation fails (e.g., 'Alert1.wav')", Order = 7, GroupName = "8. Voice Alerts")]
        public string AlertFallbackSound { get; set; }
        
        #endregion
    }
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private RedTailAutoVWAP[] cacheRedTailAutoVWAP;
		public RedTailAutoVWAP RedTailAutoVWAP(bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			return RedTailAutoVWAP(Input, showNyVwap, showPrevNyVwap, showDayVwap, showHodVwap, showLodVwap, showMonthVwap, showYearVwap, showHoyVwap, showNyOpeningRange, showDayInitialBalance);
		}

		public RedTailAutoVWAP RedTailAutoVWAP(ISeries<double> input, bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			if (cacheRedTailAutoVWAP != null)
				for (int idx = 0; idx < cacheRedTailAutoVWAP.Length; idx++)
					if (cacheRedTailAutoVWAP[idx] != null && cacheRedTailAutoVWAP[idx].ShowNyVwap == showNyVwap && cacheRedTailAutoVWAP[idx].ShowPrevNyVwap == showPrevNyVwap && cacheRedTailAutoVWAP[idx].ShowDayVwap == showDayVwap && cacheRedTailAutoVWAP[idx].ShowHodVwap == showHodVwap && cacheRedTailAutoVWAP[idx].ShowLodVwap == showLodVwap && cacheRedTailAutoVWAP[idx].ShowMonthVwap == showMonthVwap && cacheRedTailAutoVWAP[idx].ShowYearVwap == showYearVwap && cacheRedTailAutoVWAP[idx].ShowHoyVwap == showHoyVwap && cacheRedTailAutoVWAP[idx].ShowNyOpeningRange == showNyOpeningRange && cacheRedTailAutoVWAP[idx].ShowDayInitialBalance == showDayInitialBalance && cacheRedTailAutoVWAP[idx].EqualsInput(input))
						return cacheRedTailAutoVWAP[idx];
			return CacheIndicator<RedTailAutoVWAP>(new RedTailAutoVWAP(){ ShowNyVwap = showNyVwap, ShowPrevNyVwap = showPrevNyVwap, ShowDayVwap = showDayVwap, ShowHodVwap = showHodVwap, ShowLodVwap = showLodVwap, ShowMonthVwap = showMonthVwap, ShowYearVwap = showYearVwap, ShowHoyVwap = showHoyVwap, ShowNyOpeningRange = showNyOpeningRange, ShowDayInitialBalance = showDayInitialBalance }, input, ref cacheRedTailAutoVWAP);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.RedTailAutoVWAP RedTailAutoVWAP(bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			return indicator.RedTailAutoVWAP(Input, showNyVwap, showPrevNyVwap, showDayVwap, showHodVwap, showLodVwap, showMonthVwap, showYearVwap, showHoyVwap, showNyOpeningRange, showDayInitialBalance);
		}

		public Indicators.RedTailAutoVWAP RedTailAutoVWAP(ISeries<double> input , bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			return indicator.RedTailAutoVWAP(input, showNyVwap, showPrevNyVwap, showDayVwap, showHodVwap, showLodVwap, showMonthVwap, showYearVwap, showHoyVwap, showNyOpeningRange, showDayInitialBalance);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.RedTailAutoVWAP RedTailAutoVWAP(bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			return indicator.RedTailAutoVWAP(Input, showNyVwap, showPrevNyVwap, showDayVwap, showHodVwap, showLodVwap, showMonthVwap, showYearVwap, showHoyVwap, showNyOpeningRange, showDayInitialBalance);
		}

		public Indicators.RedTailAutoVWAP RedTailAutoVWAP(ISeries<double> input , bool showNyVwap, bool showPrevNyVwap, bool showDayVwap, bool showHodVwap, bool showLodVwap, bool showMonthVwap, bool showYearVwap, bool showHoyVwap, bool showNyOpeningRange, bool showDayInitialBalance)
		{
			return indicator.RedTailAutoVWAP(input, showNyVwap, showPrevNyVwap, showDayVwap, showHodVwap, showLodVwap, showMonthVwap, showYearVwap, showHoyVwap, showNyOpeningRange, showDayInitialBalance);
		}
	}
}

#endregion
