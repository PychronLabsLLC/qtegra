// Runs RemoteControlServer.cs with a fake instrument (see Stubs.cs) and checks the replies:
// commands through the dispatcher, request ids, scan data, and the UDP and TCP servers.
// Built and run by run.sh.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Thermo.Imhotep.BasicHardware;
using Thermo.Imhotep.SpectrumLibrary;
using Thermo.Imhotep.Util;

static class DispatchTest
{
    const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    const int TcpPort = 18071;
    const int UdpPort = 18072;
    static int failures = 0;

    static int Main(string[] argv)
    {
        // --protocol <header.md> <PROTOCOL.md> <protocol.json>: write the docs instead of running the tests
        if (argv.Length == 4 && argv[0] == "--protocol")
        {
            typeof(RemoteControl).GetMethod("RegisterCommands", Flags).Invoke(null, null);
            File.WriteAllText(argv[2], ProtocolMarkdown(File.ReadAllText(argv[1])));
            File.WriteAllText(argv[3], Run("Describe commands").Replace(",{\"name\":", ",\n{\"name\":").Replace("[{\"name\":\"GetVersion", "[\n{\"name\":\"GetVersion") + "\n");
            return 0;
        }

        // a comma-decimal locale, to prove numbers do not depend on the Windows locale
        Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
        Config.probe_scan_data = false;

        var t = typeof(RemoteControl);
        var instrument = new IRMSBaseCore();
        instrument.UFCCalibrationData = new List<UFCCalibrationData> {
            new UFCCalibrationData { Identifier = "CUP 1", Gain = 1.25 } };
        instrument.CupConfigurationDataList = new CupConfigurationDataList();
        instrument.CupConfigurationDataList.Active = new IRMSBaseCupConfigurationData {
            CollectorItemList = new CollectorItemList {
                new IRMSBaseCollectorItem { Identifier = "CUP 1", Mass = 40, Appearance = new Appearance { Label = "H1" } } } };
        t.GetField("Instrument", Flags).SetValue(null, instrument);
        t.GetField("DETECTOR_NAMES", Flags).SetValue(null, Config.HELIX_SFT_DETECTOR_NAMES);
        t.GetField("INSTRUMENT_NAME", Flags).SetValue(null, "HelixSFT");
        t.GetMethod("RegisterCommands", Flags).Invoke(null, null);

        Section("dispatcher");
        Check("GetVersion", Run("GetVersion"), r => r.Length > 0 && !r.StartsWith("Error"));
        Check("Help", Run("Help"), r => r.Contains("GetData") && r.Contains("SetMass") && r.Contains("GetNextData"));
        Check("Ping", Run("Ping"), r => r == "OK");
        Check("Help SetMass", Run("Help SetMass"), r => r.StartsWith("SetMass <mass>[,<cup>] -> ok."));
        Check("Help BlankBeam", Run("Help BlankBeam"), r => r.StartsWith("BlankBeam true|false -> ok."));
        Check("Describe", Run("Describe"), r => r.StartsWith("{\"protocol\":1,") && r.Contains("\"instrument\":\"HelixSFT\"")
              && r.Contains("\"transport\":\"udp\"") && r.Contains("\"keepalive\"") && r.Contains("{\"id\":\"CUP 1\",\"name\":\"H1\"}"));
        Check("Describe nope", Run("Describe nope"), r => r == "Error: unknown section nope");
        Check("Describe commands: optional argument", Run("Describe commands"),
              r => r.Contains("{\"name\":\"cup\",\"type\":\"text\",\"optional\":true,\"repeated\":false,\"separator\":\",\"}"));
        Check("Describe commands: enum", Run("Describe commands"),
              r => r.Contains("{\"name\":\"state\",\"type\":\"enum\",\"values\":[\"On\",\"Off\"],\"optional\":false,\"repeated\":false,\"separator\":\",\"}"));
        Check("Describe commands: repeated", Run("Describe commands"),
              r => r.Contains("{\"name\":\"parameter\",\"type\":\"text\",\"optional\":false,\"repeated\":true,\"separator\":\" \"}"));
        Check("Describe commands: reply", Run("Describe commands"), r => r.Contains("\"reply\":{\"type\":\"number\",\"unit\":\"kV\"}"));
        Check("Help Nope", Run("Help Nope"), r => r == "Error: unknown command Nope");
        Check("KeepAlive over dispatcher", Run("KeepAlive"), r => r.StartsWith("Error: KeepAlive must be the first"));
        Check("GetParameter Nope Set", Run("GetParameter Nope Set"), r => r == "Error: could not read Nope Set");
        Check("GetParameters with spaces", Run("GetParameters Field Set, Nope Set"), r => r == "0,0");
        Check("CheckParameters", Run("CheckParameters Field Set,Nope Set"), r => r == "True,False");
        Check("ProbeInstrument", Run("ProbeInstrument"), r => r == "OK");
        Check("NoSuchCommand", Run("NoSuchCommand"), r => r == "Error: Invalid Command");
        Check("<empty>", Run(""), r => r == "Error: Invalid Command");
        Check("SetMagnetDAC", Run("SetMagnetDAC"), r => r == "Error: missing argument for SetMagnetDAC");
        Check("SetMagnetDAC abc", Run("SetMagnetDAC abc"), r => r == "Error: invalid number in SetMagnetDAC abc");
        Check("SetMagnetDAC  0.1", Run("SetMagnetDAC  0.1"), r => r == "OK");
        Check("GetMagnetMoving", Run("GetMagnetMoving"), r => r == "False");
        Check("GetParameter Field Set", Run("GetParameter Field Set"), r => r == "0.1");
        Check("SetParameter Field Set,1.5", Run("SetParameter Field Set,1.5"), r => r == "OK");
        Check("GetSubCupParameters", Run("GetSubCupParameters"),
              r => r == "Deflection H1 Set,0;Deflection AX Set,0;Deflection CDD Set,0;CDD Supply Set,0");
        Check("GetGain H1", Run("GetGain H1"), r => r == "1.25");
        Check("SetGain H1,2.5", Run("SetGain H1,2.5"), r => r == "OK");
        Check("GetGain H1 after set", Run("GetGain H1"), r => r == "2.5");
        Check("GetGain XX", Run("GetGain XX"), r => r == "Error: unknown detector XX");
        Check("BlankBeam true", Run("BlankBeam true"), r => r == "OK");
        Check("BlankBeam false", Run("BlankBeam false"), r => r == "OK");
        Check("GetDeflections H1,AX", Run("GetDeflections H1,AX"), r => r == "0,0");
        Check("SetDeflection L1,150", Run("SetDeflection L1,150"), r => r == "OK");
        Check("ProtectDetector Off without On", Run("ProtectDetector L1,Off"), r => r == "OK");
        Check("  ...leaves the deflection", Run("GetDeflection L1"), r => r == "150");
        Check("ProtectDetector On", Run("ProtectDetector L1,On"), r => r == "OK");
        Check("  ...deflects", Run("GetDeflection L1"), r => r == "2000");
        Check("ProtectDetector On again", Run("ProtectDetector L1,On"), r => r == "OK");
        Check("ProtectDetector Off", Run("ProtectDetector L1,Off"), r => r == "OK");
        Check("  ...restores the original", Run("GetDeflection L1"), r => r == "150");
        Check("ProtectDetector Off again", Run("ProtectDetector L1,Off"), r => r == "OK");
        Check("  ...still the original", Run("GetDeflection L1"), r => r == "150");
        Check("ProtectDetector On after Off", Run("ProtectDetector L1,On"), r => r == "OK");
        Check("SetDeflection L1,175 while protected", Run("SetDeflection L1,175"), r => r == "OK");
        Check("ProtectDetector Off", Run("ProtectDetector L1,Off"), r => r == "OK");
        Check("  ...restores the saved value", Run("GetDeflection L1"), r => r == "150");
        Check("ProtectDetector On, unreadable deflection", Run("ProtectDetector Nope,On"), r => r == "Error: could not read Deflection Nope Set");
        Check("Subscribe over dispatcher", Run("Subscribe"), r => r == "Error: Subscribe needs a TCP connection");

        Section("scan data");
        Check("GetData before scan", Run("GetData"), r => r == "Error: no data");
        Check("GetDataN before scan", Run("GetDataN"), r => r == "Error: no data");
        Scan(1.5);
        Check("GetData", Run("GetData"), r => r == "H1,1.5");
        Check("GetDataN", Run("GetDataN"), r => r == "1|H1,1.5");
        Check("GetNextData 0.2 (no new scan)", Run("GetNextData 0.2"), r => r == "Error: timeout");
        Check("GetNextData 1 0 (already newer)", Run("GetNextData 1 0"), r => r == "1|H1,1.5");
        ScanLater(150, 2.5);
        Check("GetNextData 2 (scan arrives)", Run("GetNextData 2"), r => r == "2|H1,2.5");
        Check("GetNextData 1 x", Run("GetNextData 1 x"), r => r.StartsWith("Error: invalid number"));

        Section("request ids");
        Check("#7 Ping", Request("#7 Ping", "a"), r => r == "#7 OK");
        Check("#8 alone", Request("#8", "a"), r => r == "#8 Error: Invalid Command");
        Check("#9 GetDataN", Request("#9 GetDataN", "a"), r => r == "#9 2|H1,2.5");
        Scan(3.5);
        Check("#9 GetDataN repeated: cached", Request("#9 GetDataN", "a"), r => r == "#9 2|H1,2.5");
        Check("#9 GetDataN other client: runs", Request("#9 GetDataN", "b"), r => r == "#9 3|H1,3.5");
        Check("#9 different command: runs", Request("#9 Ping", "a"), r => r == "#9 OK");

        Section("servers");
        Config.tcp_read_timeout = 300;
        Config.port = UdpPort;
        t.GetMethod("UDPServeForever", Flags).Invoke(null, null);
        Thread.Sleep(300);
        Config.port = TcpPort;
        t.GetMethod("TCPServeForever", Flags).Invoke(null, null);
        Thread.Sleep(200);

        Check("UDP #1 Ping", Udp("#1 Ping"), r => r == "#1 OK");
        Check("UDP GetDataN", Udp("GetDataN"), r => r == "3|H1,3.5");
        Check("TCP one-shot Ping", TcpOneShot("Ping"), r => r == "OK");
        Check("TCP one-shot with CRLF", TcpOneShot("GetVersion\r\n"), r => r.Length > 0 && !r.StartsWith("Error"));
        Check("TCP one-shot #3 Ping", TcpOneShot("#3 Ping"), r => r == "#3 OK");

        using (var c = Connect())
        {
            var s = c.GetStream();
            var reader = new StreamReader(s, Encoding.ASCII);
            Write(s, "KeepAlive\n");
            Check("KeepAlive", reader.ReadLine(), r => r == "OK");
            Write(s, "Ping\n#5 GetDataN\n");
            Check("KeepAlive Ping", reader.ReadLine(), r => r == "OK");
            Check("KeepAlive #5 GetDataN", reader.ReadLine(), r => r == "#5 3|H1,3.5");
            Write(s, "Subscribe\n");
            ScanLater(100, 4.5);
            Check("KeepAlive then Subscribe", reader.ReadLine(), r => r == "4|H1,4.5");
        }

        foreach (var end in new[] { "\r", "\r\n" })
        {
            using (var c = Connect())
            {
                var s = c.GetStream();
                var reader = new StreamReader(s, Encoding.ASCII);
                string label = end == "\r" ? "CR" : "CRLF";
                Write(s, "KeepAlive" + end);
                Check("KeepAlive " + label, reader.ReadLine(), r => r == "OK");
                Write(s, "Ping" + end);
                Check("KeepAlive " + label + " Ping", reader.ReadLine(), r => r == "OK");
                Write(s, "#6 Ping" + end + "GetDataN" + end);
                Check("KeepAlive " + label + " two commands", reader.ReadLine(), r => r == "#6 OK");
                Check("  ...second", reader.ReadLine(), r => r == "4|H1,4.5");
            }
        }

        using (var idle = Connect())
        {
            // a silent client must not block anyone else, and is dropped after tcp_read_timeout
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Check("TCP Ping while another client is silent", TcpOneShot("Ping"), r => r == "OK");
            Check("  ...answered without waiting for it", sw.ElapsedMilliseconds.ToString(), r => long.Parse(r) < 250);
            idle.ReceiveTimeout = 3000;
            int n;
            try { n = idle.GetStream().Read(new byte[16], 0, 16); } catch (IOException) { n = -1; }
            Check("silent client closed by server", n.ToString(), r => r == "0");
        }

        using (var sub = Connect())
        {
            var s = sub.GetStream();
            var reader = new StreamReader(s, Encoding.ASCII);
            Write(s, "Subscribe");
            Thread.Sleep(100);
            Scan(5.5);
            Check("Subscribe first scan", reader.ReadLine(), r => r == "5|H1,5.5");
            Scan(6.5);
            Check("Subscribe second scan", reader.ReadLine(), r => r == "6|H1,6.5");
        }

        t.GetMethod("Dispose", Flags).Invoke(null, null);

        Console.WriteLine(failures == 0 ? "\nall passed" : "\n" + failures + " failed");
        return failures == 0 ? 0 : 1;
    }

    // PROTOCOL.md: the header, then one table per section from the command definitions
    static string ProtocolMarkdown(string header)
    {
        var list = (System.Collections.IList)typeof(RemoteControl).GetField("COMMAND_LIST", Flags).GetValue(null);
        var sb = new StringBuilder(header.TrimEnd() + "\n");
        string section = null;
        foreach (object c in list)
        {
            Func<string, string> field = name => (string)c.GetType().GetField(name).GetValue(c);
            if (field("Section") != section)
            {
                section = field("Section");
                sb.Append("\n## " + section + "\n\n| Command | Arguments | Reply | Description |\n|---|---|---|---|\n");
            }
            sb.AppendFormat("| `{0}` | {1} | {2} | {3} |\n", field("Name"), Code(field("Usage")), Code(field("Reply")),
                            field("Description").Replace("<", "&lt;").Replace(">", "&gt;").Replace("|", "\\|"));
        }
        return sb.ToString();
    }

    static string Code(string text)
    {
        return text.Length == 0 ? "" : "`" + text.Replace("|", "\\|") + "`";
    }

    static string Run(string cmd)
    {
        return (string)typeof(RemoteControl).GetMethod("ParseAndExecuteCommand", Flags).Invoke(null, new object[] { cmd });
    }

    static string Request(string raw, string client)
    {
        return (string)typeof(RemoteControl).GetMethod("HandleRequest", Flags).Invoke(null, new object[] { raw, client });
    }

    static void Scan(double value)
    {
        var spec = new Spectrum { new Series { new SpectrumData { Mass = 40, Analog = value } } };
        RemoteControl.ScanDataAvailable(null, new EventArgs<Spectrum> { Value = spec });
    }

    static void ScanLater(int ms, double value)
    {
        new Thread(() => { Thread.Sleep(ms); Scan(value); }).Start();
    }

    static string Udp(string cmd)
    {
        using (var u = new UdpClient())
        {
            u.Client.ReceiveTimeout = 2000;
            var b = Encoding.ASCII.GetBytes(cmd);
            u.Send(b, b.Length, "127.0.0.1", UdpPort);
            var from = new IPEndPoint(IPAddress.Any, 0);
            return Encoding.ASCII.GetString(u.Receive(ref from));
        }
    }

    static TcpClient Connect()
    {
        var c = new TcpClient();
        c.Connect("127.0.0.1", TcpPort);
        c.ReceiveTimeout = 3000;
        return c;
    }

    static void Write(NetworkStream s, string text)
    {
        var b = Encoding.ASCII.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    static string TcpOneShot(string cmd)
    {
        using (var c = Connect())
        {
            var s = c.GetStream();
            Write(s, cmd);
            return new StreamReader(s, Encoding.ASCII).ReadToEnd();
        }
    }

    static void Section(string name)
    {
        Console.WriteLine("\n-- " + name);
    }

    static void Check(string name, string reply, Func<string, bool> check)
    {
        bool passed;
        try { passed = reply != null && check(reply); } catch (Exception) { passed = false; }
        if (!passed) failures++;
        Console.WriteLine("{0,-4} {1,-42} {2}", passed ? "ok" : "FAIL", name,
                          reply == null ? "<null>" : (reply.Length > 70 ? reply.Substring(0, 70) + "..." : reply));
    }
}
