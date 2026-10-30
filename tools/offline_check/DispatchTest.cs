// Runs commands through RemoteControl.ParseAndExecuteCommand with a fake instrument (see Stubs.cs)
// and checks the replies. Built and run by run.sh.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Thermo.Imhotep.BasicHardware;

static class DispatchTest
{
    static int Main()
    {
        // a comma-decimal locale, to prove numbers do not depend on the Windows locale
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

        var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        var t = typeof(RemoteControl);
        var instrument = new IRMSBaseCore();
        instrument.UFCCalibrationData = new List<UFCCalibrationData> {
            new UFCCalibrationData { Identifier = "CUP 1", Gain = 1.25 } };
        t.GetField("Instrument", flags).SetValue(null, instrument);
        t.GetField("DETECTOR_NAMES", flags).SetValue(null, Config.HELIX_SFT_DETECTOR_NAMES);
        t.GetMethod("RegisterCommands", flags).Invoke(null, null);
        var run = t.GetMethod("ParseAndExecuteCommand", flags);

        var cases = new List<KeyValuePair<string, Func<string, bool>>>
        {
            Case("GetVersion", r => r.Length > 0 && !r.StartsWith("Error")),
            Case("Help", r => r.Contains("GetData") && r.Contains("SetMass") && r.Contains("GetParameters")),
            Case("NoSuchCommand", r => r == "Error: Invalid Command"),
            Case("", r => r == "Error: Invalid Command"),
            Case("SetMagnetDAC", r => r == "Error: missing argument for SetMagnetDAC"),
            Case("SetMagnetDAC abc", r => r == "Error: invalid number in SetMagnetDAC abc"),
            Case("SetMagnetDAC  0.1", r => r == "OK"),
            Case("GetMagnetMoving", r => r == "False"),
            Case("GetData", r => r == "Error: no data"),
            Case("ProbeScanData", r => r == "OK"),
            Case("GetParameter Field Set", r => r == "0"),
            Case("SetParameter Field Set,1.5", r => r == "OK"),
            Case("GetSubCupParameters", r => r == "Deflection H1 Set,0;Deflection AX Set,0;Deflection CDD Set,0;CDD Supply Set,0"),
            Case("GetGain H1", r => r == "1.25"),
            Case("SetGain H1,2.5", r => r == "OK"),
            Case("GetGain H1", r => r == "2.5"),
            Case("GetGain XX", r => r == "Error: unknown detector XX"),
            Case("BlankBeam true", r => r == "OK"),
            Case("BlankBeam false", r => r == "OK"),
            Case("GetDeflections H1,AX", r => r == "0,0"),
        };

        int failures = 0;
        foreach (var c in cases)
        {
            string reply = (string)run.Invoke(null, new object[] { c.Key });
            bool passed = c.Value(reply);
            if (!passed) failures++;
            Console.WriteLine("{0,-4} {1,-30} {2}", passed ? "ok" : "FAIL", "[" + c.Key + "]", reply);
        }
        Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    static KeyValuePair<string, Func<string, bool>> Case(string cmd, Func<string, bool> check)
    {
        return new KeyValuePair<string, Func<string, bool>>(cmd, check);
    }
}
