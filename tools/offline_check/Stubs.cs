// Minimal stand-ins for the Qtegra (Thermo.Imhotep) types used by RemoteControlServer.cs.
// Only for a local compile check; signatures are guesses from how the script uses them.
using System;
using System.Collections;
using System.Collections.Generic;

namespace Thermo.Imhotep.Util
{
    public enum LogLevel { Debug, UserInfo, UserError, Warning }
    public static class Logger { public static void Log(LogLevel l, string m) {} }
    public class EventArgs<T> : EventArgs { public T Value; }
}

namespace Thermo.Imhotep.SpectrumLibrary
{
    public class SpectrumData { public double Mass; public double Analog; }
    public class Series : List<SpectrumData> {}
    public class Spectrum : List<Series> { public object Clone() { return this; } }
}

namespace Thermo.Imhotep.Definitions.Core
{
    public class TuneSettings { public object Object; }
    public class TuneParameterBlock {}
    public class TuneSettingsManager
    {
        public TuneSettingsManager(object id) {}
        public Type EntryType;
        public IEnumerable<string> GetEntries() { return null; }
        public object ReadEntry(string n) { return null; }
    }
}

namespace Thermo.Imhotep.BasicHardware
{
    using Thermo.Imhotep.SpectrumLibrary;
    using Thermo.Imhotep.Util;

    public enum IRMSBaseCollectorType { CounterCup, Faraday }
    public enum DetectorActivateMode { Standby, Off }
    public class CollectorActivationMode { public CollectorActivationMode(DetectorActivateMode m) {} }
    public class Appearance { public string Label; }
    public class IRMSBaseCollectorItem
    {
        public string Identifier; public double? Mass; public bool Active; public double GammaCorrection;
        public IRMSBaseCollectorType CollectorType; public Appearance Appearance;
    }
    public class CollectorItemList : List<IRMSBaseCollectorItem>
    { public IRMSBaseCollectorItem GetMasterCollectorItem() { return null; } }
    public class IRMSBaseSubCupConfigurationData { public string Name; public object Identifier; }
    public class SubCupConfigurationList : List<IRMSBaseSubCupConfigurationData>
    { public IRMSBaseSubCupConfigurationData FindSubCupConfigurationByName(string n) { return null; } }
    public class IRMSBaseCupConfigurationData
    {
        public string Name; public object Identifier; public object MassCalibration;
        public CollectorItemList CollectorItemList; public SubCupConfigurationList SubCupConfigurationList;
    }
    public class CupConfigurationDataList : List<IRMSBaseCupConfigurationData>
    {
        public IRMSBaseCupConfigurationData GetActiveCupConfiguration() { return null; }
        public IRMSBaseSubCupConfigurationData GetActiveSubCupConfiguration() { return null; }
        public IRMSBaseCupConfigurationData FindCupConfigurationByName(string n) { return null; }
        public void SetActiveItem(object a, object b, object c, object d) {}
    }
    public class IRMSBaseMeasurementInfo
    {
        public IRMSBaseMeasurementInfo(IRMSBaseMeasurementInfo o) {}
        public IRMSBaseMeasurementInfo(object scanType, double integrationTime, object settlingTime, double mass, CollectorItemList l, object cal) {}
        public double ActualScanMass; public double IntegrationTime; public object SettlingTime; public object ScanType;
    }
    public class ScanTransitionController
    {
        public bool InitializeScriptScan(IRMSBaseMeasurementInfo m) { return true; }
        public bool StartMonitoring(IRMSBaseMeasurementInfo m) { return true; }
    }
    public class UFCCalibrationData { public string Identifier; public double Gain; }
    public class TuneParameters { public object Parameters; }
    public class BaseSettingsData { public int CounterProtectionPreDelay; }
    public class IRMSBaseCore
    {
        public object Id; public IRMSBaseMeasurementInfo MeasurementInfo;
        public ScanTransitionController ScanTransitionController;
        public CupConfigurationDataList CupConfigurationDataList; public object CupSettingDataList;
        public List<UFCCalibrationData> UFCCalibrationData; public TuneParameters TuneParameters;
        public BaseSettingsData BaseSettingsData;
        public event EventHandler<EventArgs<Spectrum>> ScanDataAvailable;
        public bool GetParameter(string n, out double v) { v = 0; return true; }
        public bool SetParameter(string n, double v) { return true; }
        public bool SetParameter(string n, int v) { return true; }
        public void SetHardwareParameters(object a, object b) {}
        public bool RequestCupConfigurationChange(object l) { return true; }
        public void SetCDDActivationMode(CollectorActivationMode m) {}
        public void UnprotectRequiredCounterCups(List<string> names) {}
        public void ProtectAllCounterCupsWithPostDelay(int d) {}
    }
}

// Qtegra compiles scripts with System and System.Threading in scope and provides the instrument globals.
partial class RemoteControl
{
    static Thermo.Imhotep.BasicHardware.IRMSBaseCore HelixSFT;
}
