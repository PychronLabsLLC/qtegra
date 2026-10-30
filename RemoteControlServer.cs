// RegisterAssembly: DefinitionsCore.dll
// RegisterAssembly: BasicHardware.dll
// RegisterAssembly: PluginManager.dll
// RegisterAssembly: HardwareClient.dll
// RegisterSystemAssembly: System.dll
// RegisterAssembly: SpectrumLibrary.dll
// RegisterSystemAssembly: System.Xml.dll
// RegisterAssembly: Util.dll

/*
Copyright 2011 Jake Ross
 Licensed under the Apache License, Version 2.0 (the "License"); you may not use
 this file except in compliance with the License. You may obtain a copy of the
 License at
    http://www.apache.org/licenses/LICENSE-2.0
Unless required by applicable law or agreed to in writing, software distributed
under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR
CONDITIONS OF ANY KIND, either express or implied. See the License for the
specific language governing permissions and limitations under the License.


Modified by Axel Suckow, Alec Deslandes, Christoph.Gerber (2019)

__version__=20.2
*/


using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Thermo.Imhotep.BasicHardware;
using Thermo.Imhotep.Definitions.Core;
using Thermo.Imhotep.SpectrumLibrary;
using Thermo.Imhotep.Util;
static class Config
{
    public static List<string> ARGUS_DETECTOR_NAMES = new List<string>(new string[]{
                                                                  "CUP 4,H2",
                                                                  "CUP 3,H1",
                                                                  "CUP 2,AX",
                                                                  "CUP 1,L1",
                                                                  "CUP 0,L2",
                                                                  "CDD 0,CDD"});

    public static List<string> HELIX_MC_DETECTOR_NAMES = new List<string>(new string[]{
                                                                 "CUP 4,H2",
                                                                 "CUP 3,H1",
																"CUP 2,AX",
																"CUP 1,L1",
																"CUP 0,L2",
																"CDD 0,L2(CDD)",
																"CDD 1,L1(CDD)",
																"CDD 2,AX(CDD)",
																"CDD 3,H1(CDD)",
																"CDD 4,H2(CDD)"});

	public static List<string> HELIX_SFT_DETECTOR_NAMES = new List<string>(new string[]{
	                                                           "CUP 1,H1",
	                                                           "CUP 0,AX",
	                                                           "CDD 0,CDD"});



    // user configurable attributes
    public static int port = 1069;
    public static bool use_udp= true;
    public static bool tag_data= true;
    public static bool use_beam_blank= true;
    public static double magnet_move_threshold_rel_mass=0.4;//relative mass threshold to trigger stepped magnet move
    public static double magnet_move_threshold=0.25;// threshold to trigger stepped magnet move in DAC units
    public static int magnet_steps=20; // number of steps to divide the total magnet displacement
    public static int magnet_step_time=100; //ms to delay between magnet steps
    public static bool probe_scan_data=true; // log the structure of the first scan received. also see ProbeScanData command
    public static bool debug=false; // also log the polled commands (GetData, GetDataN, GetNextData, Ping)
    public static int tcp_read_timeout=5000; // ms a TCP client has to send its command before the connection is closed
    public static int tcp_idle_timeout=60000; // ms a KeepAlive connection may sit idle before it is closed
    public static int request_id_window=10000; // ms during which a repeated "#id command" gets the cached reply instead of running again

}


class RemoteControl
{

    private static Socket UDP_SOCK;
    private static Thread SERVER_THREAD;
    private static TcpListener TCPLISTENER;
    private static volatile bool RUNNING=true;

    private static double LAST_Y_SYMMETRY=0;
    private static bool IsBLANKED=false;
    private static Dictionary<string, double> m_defl = new Dictionary<string, double>();

    private const string VERSION = "20.2"; // keep in sync with __version__ in the header
    private const int PROTOCOL_VERSION = 1; // goes up when a command or reply changes in a way clients must handle
    private static string INSTRUMENT_NAME = "";

    private const int OPEN = 1;
    private const int CLOSE = 0;

    public static IRMSBaseCore Instrument;
    public static IRMSBaseMeasurementInfo m_restoreMeasurementInfo;

    private static string SCAN_DATA;
    private static long SCAN_COUNT=0; // number of scans received. guarded by m_lock, which is pulsed on every scan
    private static object m_lock=new object();

    // hardware commands run one at a time, whichever client or thread they come from
    private static object m_command_lock=new object();
    private static List<TcpClient> TCP_CLIENTS=new List<TcpClient>();


	private static List<string> DETECTOR_NAMES;

    private static volatile bool MAGNET_MOVING=false;
    private static object m_magnet_lock=new object();
    private static bool CDDS_ARE_ON = false; // keeps track of CDD state as long as this is done through "ActivateAllCDDs" and "DeactivateAllCDDs"
    private static volatile bool PROBE_PENDING = Config.probe_scan_data;

    public static void Main ()
    {

        // Select the instrument: leave exactly ONE line uncommented
        // ================================================================================
        //Instrument = ArgusMC;    INSTRUMENT_NAME = "ArgusMC";   DETECTOR_NAMES = Config.ARGUS_DETECTOR_NAMES;
        //Instrument = HelixMC;    INSTRUMENT_NAME = "HelixMC";   DETECTOR_NAMES = Config.HELIX_MC_DETECTOR_NAMES;
        Instrument = HelixSFT;     INSTRUMENT_NAME = "HelixSFT";  DETECTOR_NAMES = Config.HELIX_SFT_DETECTOR_NAMES;
        // ================================================================================

        RegisterCommands();

        //init parameters
        Instrument.GetParameter("Y-Symmetry Set", out LAST_Y_SYMMETRY);

        GetTuningSettings();
        PrepareEnvironment();

        //mReportGains();

        //list attributes
        //listattributes(Instrument.GetType(), "instrument");

        //setup data recording
        InitializeDataRecord();

        if (Config.use_udp)
        {
            UDPServeForever();
        }
        else
        {
            TCPServeForever();
        }

    }

    // Commands are defined in RegisterCommands. PROTOCOL.md is generated from those definitions.

	private static string ParseAndExecuteCommand (string cmd)
	{
		string result;
		string name = cmd.Trim().Split(' ')[0];
		bool quiet = !Config.debug && QUIET_COMMANDS.Contains(name);
		if (!quiet)
		{
			Logger.Log(LogLevel.Debug, String.Format("Executing {0}", cmd));
		}
		try
		{
			result = ExecuteCommand(cmd);
		}
		catch (IndexOutOfRangeException)
		{
			result = String.Format("Error: missing argument for {0}", cmd);
		}
		catch (FormatException)
		{
			result = String.Format("Error: invalid number in {0}", cmd);
		}
		catch (Exception e)
		{
			Logger.Log(LogLevel.UserError, String.Format("{0} failed. {1}", cmd, e));
			result = String.Format("Error: {0}", e.Message);
		}

		if (result == null)
		{
			result = "Error: no result";
		}
		if (!quiet)
		{
			log(String.Format("{0} => {1}", cmd, result));
		}
		return result;
	}

	// A request may carry an id: "#<id> <command>" is answered "#<id> <reply>", so a client can match
	// replies to requests. A repeat of the same id and command from the same client within
	// Config.request_id_window ms (a UDP retry) gets the cached reply instead of running the command again.
	private class CachedReply
	{
		public string Command;
		public string Reply;
		public DateTime Time;
	}
	private static Dictionary<string, CachedReply> m_replies=new Dictionary<string, CachedReply>();
	private static Queue<string> m_reply_keys=new Queue<string>();

	private static string HandleRequest(string raw, string client)
	{
		string msg = raw.Trim();
		if (!msg.StartsWith("#"))
		{
			return ParseAndExecuteCommand(msg);
		}

		int sp = msg.IndexOf(' ');
		if (sp < 0)
		{
			return String.Format("{0} Error: Invalid Command", msg);
		}
		string id = msg.Substring(0, sp);
		string cmd = msg.Substring(sp+1).Trim();
		string key = client + " " + id;

		CachedReply cached;
		lock(m_replies)
		{
			if (m_replies.TryGetValue(key, out cached) && cached.Command == cmd &&
				(DateTime.UtcNow - cached.Time).TotalMilliseconds < Config.request_id_window)
			{
				Logger.Log(LogLevel.Debug, String.Format("Repeated request {0} {1}, sending cached reply", id, cmd));
				return id + " " + cached.Reply;
			}
		}

		string reply = ParseAndExecuteCommand(cmd);

		lock(m_replies)
		{
			if (!m_replies.ContainsKey(key))
			{
				m_reply_keys.Enqueue(key);
				if (m_reply_keys.Count > 256)
				{
					m_replies.Remove(m_reply_keys.Dequeue());
				}
			}
			cached = new CachedReply();
			cached.Command = cmd;
			cached.Reply = reply;
			cached.Time = DateTime.UtcNow;
			m_replies[key] = cached;
		}
		return id + " " + reply;
	}

	// args: the command split on spaces, args[0] is the command name
	// rest: everything after the command name, rejoined with single spaces
	private delegate string CommandHandler(string[] args, string rest);

	// Every command is defined once, in RegisterCommands, with its arguments, reply and description.
	// "Help", "Describe commands", PROTOCOL.md and protocol.json (tools/offline_check/run.sh --update-protocol)
	// all come from these definitions.
	//
	// Arguments are written as they appear on the wire, with a type in each <name:type>:
	//    "<mass:number>[,<cup:text>]"   [ ] optional, the characters between arguments are the separators
	//    "<a:text>[,<a:text>...]"       the same name twice: repeated, comma separated
	//    types: number, integer, word (no spaces), text (may contain spaces), enum(A|B)
	// Replies are ok, text, bool, number[:unit], list:<type>, lines (CR separated), scan, counted_scan
	// or stream:<type>. Any command can also reply "Error: ...".
	private class CommandInfo
	{
		public string Name;
		public string Section;
		public string Args;
		public string Usage; // Args without the types, for people
		public string Reply;
		public string Description;
		public bool Quick; // does not touch the hardware: skips m_command_lock and answers straight away
		public CommandHandler Handler;
	}

	private static Dictionary<string, CommandInfo> COMMANDS = new Dictionary<string, CommandInfo>();
	private static List<CommandInfo> COMMAND_LIST = new List<CommandInfo>();
	private static string SECTION = "";
	// polled commands, only logged when Config.debug is true
	private static List<string> QUIET_COMMANDS = new List<string>(new string[]{"GetData", "GetDataN", "GetNextData", "Ping"});

	private static void Section(string name)
	{
		SECTION = name;
	}

	private static void Register(string name, string args, string reply, string description, CommandHandler handler)
	{
		Add(name, args, reply, description, handler, false);
	}

	private static void RegisterQuick(string name, string args, string reply, string description, CommandHandler handler)
	{
		Add(name, args, reply, description, handler, true);
	}

	private static void Add(string name, string args, string reply, string description, CommandHandler handler, bool quick)
	{
		CommandInfo info = new CommandInfo();
		info.Name = name;
		info.Section = SECTION;
		info.Args = args;
		info.Usage = ARG.Replace(args, delegate(Match m) { return m.Groups[3].Success ? m.Groups[3].Value : "<" + m.Groups[1].Value + ">"; });
		info.Reply = reply;
		info.Description = description;
		info.Quick = quick;
		info.Handler = handler;
		COMMANDS[name] = info;
		COMMAND_LIST.Add(info);
	}

	private static string Help(string name)
	{
		if (name.Length == 0)
		{
			List<string> names = new List<string>();
			foreach (CommandInfo c in COMMAND_LIST)
			{
				names.Add(c.Name);
			}
			return String.Join(",", names.ToArray());
		}
		CommandInfo info;
		if (!COMMANDS.TryGetValue(name, out info))
		{
			return String.Format("Error: unknown command {0}", name);
		}
		return String.Format("{0}{1} -> {2}. {3}", info.Name, (info.Usage.Length > 0) ? " " + info.Usage : "", info.Reply, info.Description);
	}

	// <name:type> or <name:enum(A|B)> in an argument definition
	private static readonly Regex ARG = new Regex(@"<([a-z_]+):([a-z]+)(?:\(([^)]*)\))?>");

	// "Describe": what a client needs to know on connecting. "Describe commands": every command definition
	private static string Describe(string section)
	{
		if (section == "commands")
		{
			return CommandsJson();
		}
		if (section.Length > 0)
		{
			return String.Format("Error: unknown section {0}", section);
		}

		List<string> detectors = new List<string>();
		foreach (string detname in DETECTOR_NAMES)
		{
			string[] p = detname.Split(',');
			detectors.Add(String.Format("{{\"id\":{0},\"name\":{1}}}", Json(p[0]), Json(p[1])));
		}
		return String.Format("{{\"protocol\":{0},\"version\":{1},\"instrument\":{2},\"transport\":{3},\"tag_data\":{4},"
			+ "\"features\":[\"keepalive\",\"subscribe\",\"request_ids\",\"scan_count\",\"describe\"],\"detectors\":[{5}]}}",
			PROTOCOL_VERSION, Json(VERSION), Json(INSTRUMENT_NAME), Json(Config.use_udp ? "udp" : "tcp"),
			Config.tag_data ? "true" : "false", String.Join(",", detectors.ToArray()));
	}

	private static string CommandsJson()
	{
		List<string> commands = new List<string>();
		foreach (CommandInfo c in COMMAND_LIST)
		{
			commands.Add(String.Format("{{\"name\":{0},\"section\":{1},\"args\":[{2}],\"reply\":{3},\"hardware\":{4},\"description\":{5}}}",
				Json(c.Name), Json(c.Section), ArgsJson(c.Args), ReplyJson(c.Reply), c.Quick ? "false" : "true", Json(c.Description)));
		}
		return String.Format("{{\"protocol\":{0},\"version\":{1},\"commands\":[{2}]}}",
			PROTOCOL_VERSION, Json(VERSION), String.Join(",", commands.ToArray()));
	}

	private static string ArgsJson(string spec)
	{
		List<string> args = new List<string>();
		MatchCollection matches = ARG.Matches(spec);
		for (int i = 0; i < matches.Count; i++)
		{
			Match m = matches[i];
			string name = m.Groups[1].Value;
			if (i > 0 && matches[i-1].Groups[1].Value == name)
			{
				continue; // the second half of a repeated argument
			}
			bool repeated = i + 1 < matches.Count && matches[i+1].Groups[1].Value == name;
			string before = spec.Substring(0, m.Index);
			bool optional = before.LastIndexOf('[') > before.LastIndexOf(']');
			string bare = before.TrimEnd('[');
			string sep = (bare.Length > 0) ? bare.Substring(bare.Length - 1) : " ";

			string values = "";
			if (m.Groups[3].Success)
			{
				List<string> names = new List<string>();
				foreach (string v in m.Groups[3].Value.Split('|'))
				{
					names.Add(Json(v));
				}
				values = String.Format(",\"values\":[{0}]", String.Join(",", names.ToArray()));
			}
			args.Add(String.Format("{{\"name\":{0},\"type\":{1}{2},\"optional\":{3},\"repeated\":{4},\"separator\":{5}}}",
				Json(name), Json(m.Groups[2].Value), values, optional ? "true" : "false", repeated ? "true" : "false", Json(sep)));
		}
		return String.Join(",", args.ToArray());
	}

	private static string ReplyJson(string reply)
	{
		string[] p = reply.Split(':');
		if (p.Length == 1)
		{
			return String.Format("{{\"type\":{0}}}", Json(p[0]));
		}
		return String.Format("{{\"type\":{0},{1}:{2}}}", Json(p[0]), (p[0] == "number") ? "\"unit\"" : "\"of\"", Json(p[1]));
	}

	private static string Json(string s)
	{
		StringBuilder sb = new StringBuilder("\"");
		foreach (char c in s)
		{
			if (c == '"' || c == '\\')
			{
				sb.Append('\\').Append(c);
			}
			else if (c < ' ')
			{
				sb.Append(String.Format("\\u{0:x4}", (int)c));
			}
			else
			{
				sb.Append(c);
			}
		}
		return sb.Append('"').ToString();
	}

	private static void RegisterCommands()
	{
		Section("General");
		RegisterQuick("GetVersion", "", "text", "Version of this script.",
			(a, rest) => VERSION);
		RegisterQuick("Help", "[<command:word>]", "text", "Comma separated command names, or the usage of one command.",
			(a, rest) => Help(rest));
		RegisterQuick("Describe", "[<section:enum(commands)>]", "text", "JSON on one line. No argument: protocol version, script version, instrument, transport, features, detectors. commands: every command definition (use TCP: this is several kB).",
			(a, rest) => Describe(rest));
		RegisterQuick("Ping", "", "ok", "Check that the server is answering.",
			(a, rest) => "OK");
		RegisterQuick("KeepAlive", "", "ok", "TCP only, and only as the first command on a connection: keep the connection open. Commands end in CR, LF or CRLF; replies end in LF.",
			(a, rest) => "Error: KeepAlive must be the first command on a TCP connection");
		Register("GetTuningSettingsList", "", "list:text", "Saved tune settings.",
			(a, rest) => GetTuningSettings());
		Register("SetTuningSettings", "<name:word>", "ok", "Load a saved tune setting.",
			(a, rest) => SetTuningSettings(a[1]) ? "OK" : String.Format("Error: could not set tuning settings {0}", a[1]));
		Register("GetIntegrationTime", "", "number:s", "Integration time of the monitor scan.",
			(a, rest) => GetIntegrationTime());
		Register("SetIntegrationTime", "<seconds:number>", "ok", "Restart the monitor scan with a new integration time.",
			(a, rest) => SetIntegrationTime(ToDouble(a[1])));
		Register("BlankBeam", "<blank:enum(true|false)>", "ok", "true remembers Y-Symmetry and sets it to -50, false restores it. Does nothing when Config.use_beam_blank is false.",
			(a, rest) => BlankBeam(a[1]));

		Section("Scan data");
		RegisterQuick("GetData", "", "scan", "Latest scan. Name,value pairs when Config.tag_data is true, values only when false. \"Error: no data\" before the first scan.",
			(a, rest) => SCAN_DATA ?? "Error: no data");
		RegisterQuick("GetDataN", "", "counted_scan", "Latest scan with its scan count, which goes up by one for every scan. <data> is as for GetData.",
			(a, rest) => GetDataN());
		RegisterQuick("GetNextData", "[<timeout_s:number>] [<scan_count:integer>]", "counted_scan", "Wait for a scan newer than <scan count> (default: the latest scan now). Timeout defaults to 5 s. Over UDP this holds up other commands while it waits.",
			(a, rest) => GetNextData(a));
		RegisterQuick("Subscribe", "", "stream:counted_scan", "TCP only: the server writes one line per scan until the client disconnects. Allowed as the first command or after KeepAlive.",
			(a, rest) => "Error: Subscribe needs a TCP connection");
		RegisterQuick("ProbeScanData", "", "ok", "Diagnostic: log the structure of the next scan to the Qtegra log.",
			(a, rest) => { PROBE_PENDING=true; return "OK"; });

		Section("Cup and sub cup configurations");
		Register("GetCupConfigurationList", "", "lines", "Cup configuration names, separated by carriage returns.",
			(a, rest) => string.Join("\r", GetCupConfigurations().ToArray()));
		Register("GetSubCupConfigurationList", "<cup:word>", "lines", "Sub cup configuration names of a cup configuration.",
			(a, rest) => GetSubCupConfigurationList(a[1]));
		Register("GetActiveCupConfiguration", "", "text", "",
			(a, rest) => Instrument.CupConfigurationDataList.GetActiveCupConfiguration().Name);
		Register("GetActiveSubCupConfiguration", "", "text", "",
			(a, rest) => Instrument.CupConfigurationDataList.GetActiveSubCupConfiguration().Name);
		Register("GetSubCupParameters", "", "text", "parameter,value;parameter,value...: the deflection of each detector of the selected instrument, then CDD Supply Set. Unreadable parameters are left out.",
			(a, rest) => GetSubCupParameters());
		Register("ActivateCupConfiguration", "<cup:word> <sub_cup:word>", "ok", "Names cannot contain spaces.",
			(a, rest) => mActivateCup(a[1], a[2]));

		Section("Ion counters (CDD)");
		Register("ActivateIonCounter", "<detector:word>", "ok", "Activate and unprotect one CDD, then restart the monitor scan.",
			(a, rest) => SetIonCounterState(a[1], true));
		Register("DeactivateIonCounter", "<detector:word>", "ok", "Deactivate and protect one CDD, then restart the monitor scan.",
			(a, rest) => SetIonCounterState(a[1], false));
		Register("ActivateAllCDDs", "", "ok", "Activate and unprotect every CDD with a mass in the active cup configuration.",
			(a, rest) => ActivateAllCDDs());
		Register("DeactivateAllCDDs", "", "ok", "Deactivate and protect all CDDs.",
			(a, rest) => DeactivateAllCDDs());

		Section("Valves");
		Register("Open", "<valve:text>", "ok", "Set the valve parameter to 1, e.g. Open Valve Ion Pump Set.",
			(a, rest) => SetParameter(rest, OPEN));
		Register("Close", "<valve:text>", "ok", "Set the valve parameter to 0.",
			(a, rest) => SetParameter(rest, CLOSE));
		Register("GetValveState", "<valve:text>", "bool", "True when open.",
			(a, rest) => GetValveState(rest));

		Section("Magnet");
		Register("GetMagnetDAC", "", "number:V", "Field Set, 0-10 V.",
			(a, rest) => ReadParameter("Field Set", 1));
		Register("SetMagnetDAC", "<dac:number>", "ok", "Set Field Set, 0-10 V. Moves larger than Config.magnet_move_threshold are stepped in the background: poll GetMagnetMoving.",
			(a, rest) => SetMagnetDAC(ToDouble(a[1])));
		RegisterQuick("GetMagnetMoving", "", "bool", "True while a SetMagnetDAC or SetMass move is running.",
			(a, rest) => GetMagnetMoving());
		Register("SetMass", "<mass:number>[,<cup:text>]", "ok", "Put <mass> on <cup> (default AX). Deactivates the CDDs first; large moves are stepped.",
			(a, rest) =>
			{
				//Adapted from CSIRORemoteControlServer.cs
				string[] p = rest.Split(',');
				return SetMass(ToDouble(p[0]), (p.Length>1) ? p[1] : null);
			});

		Section("Source");
		Register("GetHighVoltage", "", "number:V", "Acceleration Reference Set in V.",
			(a, rest) => ReadParameter("Acceleration Reference Set", 1000));
		Register("SetHighVoltage", "<volts:number>", "ok", "",
			(a, rest) => SetParameter("Acceleration Reference Set", ToDouble(a[1])/1000.0));
		Register("GetHV", "", "number:kV", "Acceleration Reference Set in kV. Note: SetHV takes V.",
			(a, rest) => ReadParameter("Acceleration Reference Set", 1));
		Register("SetHV", "<volts:number>", "ok", "Same as SetHighVoltage. Note: takes V although GetHV returns kV.",
			(a, rest) => SetParameter("Acceleration Reference Set", ToDouble(a[1])/1000.0));

		Section("Detectors");
		Register("ProtectDetector", "<detector:word>,<state:enum(On|Off)>", "ok", "On saves the deflection and sets it to 2000, Off restores it.",
			(a, rest) =>
			{
				string[] p = a[1].Split(',');
				ProtectDetector(p[0], p[1]);
				return "OK";
			});
		Register("GetDeflection", "<detector:text>", "number", "Deflection <detector> Set.",
			(a, rest) => ReadParameter(String.Format("Deflection {0} Set", rest), 1));
		Register("SetDeflection", "<detector:text>,<value:number>", "ok", "",
			(a, rest) =>
			{
				string[] p = rest.Split(',');
				return SetParameter(String.Format("Deflection {0} Set", p[0]), ToDouble(p[1]));
			});
		Register("GetDeflections", "<detector:text>[,<detector:text>...]", "list:number", "0 for a deflection that cannot be read.",
			(a, rest) => GetDeflections(a));
		Register("GetIonCounterVoltage", "", "number", "CDD Supply Set.",
			(a, rest) => ReadParameter("CDD Supply Set", 1));
		Register("SetIonCounterVoltage", "<value:number>", "ok", "",
			(a, rest) => SetParameter("CDD Supply Set", ToDouble(a[1])));
		Register("GetGain", "<detector:text>", "number", "Detector gain from the calibration data.",
			(a, rest) => GetGain(rest));
		Register("SetGain", "<detector:text>,<value:number>", "ok", "",
			(a, rest) =>
			{
				string[] p = rest.Split(',');
				return SetGain(p[0], ToDouble(p[1]));
			});

		Section("Hardware parameters");
		Register("GetParameter", "<parameter:text>", "number", "Any item in the instrument's hardware database, e.g. Field Set. See \"Finding parameter names\".",
			(a, rest) => ReadParameter(rest, 1));
		Register("SetParameter", "<parameter:text>,<value:number>", "ok", "",
			(a, rest) =>
			{
				string[] p = rest.Split(',');
				return SetParameter(p[0], ToDouble(p[1]));
			});
		Register("GetParameters", "<parameter:text>[,<parameter:text>...]", "list:number", "0 for a parameter that cannot be read: use CheckParameters to tell.",
			(a, rest) => GetParameters(rest));
		Register("CheckParameters", "<parameter:text>[,<parameter:text>...]", "list:bool", "True for each parameter that can be read.",
			(a, rest) => CheckParameters(rest));
		Register("ProbeInstrument", "", "ok", "Diagnostic: log the instrument object's properties and members to the Qtegra log, to look for a list of its parameters.",
			(a, rest) => ProbeInstrument());
	}

	private static string ExecuteCommand (string cmd)
	{
		string[] args = cmd.Trim().Split (new char[]{' '}, StringSplitOptions.RemoveEmptyEntries);
		CommandInfo info;
		if (args.Length == 0 || !COMMANDS.TryGetValue(args[0], out info))
		{
			return "Error: Invalid Command";
		}
		string rest = String.Join(" ", Slice(args, 1, 0));
		if (info.Quick)
		{
			return info.Handler(args, rest);
		}
		lock(m_command_lock)
		{
			return info.Handler(args, rest);
		}
	}

	// "<scan count>|<data>". the count goes up by one for every scan, so a client can tell new data from old
	private static string GetDataN()
	{
		lock(m_lock)
		{
			if (SCAN_DATA == null)
			{
				return "Error: no data";
			}
			return String.Format("{0}|{1}", SCAN_COUNT, SCAN_DATA);
		}
	}

	// GetNextData [timeout seconds, default 5] [scan count]
	// waits for a scan newer than the given count (default: the latest scan now) and returns it like GetDataN
	private static string GetNextData(string[] a)
	{
		double timeout = (a.Length > 1) ? ToDouble(a[1]) : 5;
		lock(m_lock)
		{
			long after = (a.Length > 2) ? Int64.Parse(a[2], CultureInfo.InvariantCulture) : SCAN_COUNT;
			DateTime end = DateTime.UtcNow.AddSeconds(timeout);
			while (SCAN_COUNT <= after)
			{
				int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
				if (left <= 0 || !RUNNING)
				{
					return "Error: timeout";
				}
				Monitor.Wait(m_lock, left);
			}
			return String.Format("{0}|{1}", SCAN_COUNT, SCAN_DATA);
		}
	}

	private static string BlankBeam(string arg)
	{
		if (!Config.use_beam_blank)
		{
			return "OK";
		}

		if (arg.ToLower()=="true")
		{
			if (IsBLANKED)
			{
				return "OK";
			}
			//remember the non blanking Y-Symmetry value
			Instrument.GetParameter("Y-Symmetry Set", out LAST_Y_SYMMETRY);
			IsBLANKED=true;
			return SetParameter("Y-Symmetry Set", -50.0);
		}

		if (!IsBLANKED)
		{
			return "OK";
		}
		IsBLANKED=false;
		return SetParameter("Y-Symmetry Set", LAST_Y_SYMMETRY);
	}

	private static string GetSubCupConfigurationList(string config_name)
	{
		List<string> sub_names = GetSubCupConfigurations (config_name);
		if (sub_names == null)
		{
			return String.Format("Error: could not find cup configuration {0}", config_name);
		}
		return string.Join ("\r", sub_names.ToArray ());
	}
//============================================================================================
//    EOCommands
//============================================================================================

	private static bool PrepareEnvironment()
    {
        m_restoreMeasurementInfo=Instrument.MeasurementInfo;
        return Instrument.ScanTransitionController.InitializeScriptScan(m_restoreMeasurementInfo);
    }

    public static void InitializeDataRecord()
    {
        // attach a handler to the ScanDataAvailable Event
        Instrument.ScanDataAvailable+=ScanDataAvailable;
    }

    public static void Dispose()
    {
        Logger.Log (LogLevel.UserInfo, "Stop Server");

        // deattach the handler from the ScanDataAvailable Event
        Instrument.ScanDataAvailable-=ScanDataAvailable;

        //shutdown the server. the UDP loop wakes every 500 ms to check RUNNING; stopping the TCP listener
        //unblocks the accept loop
        RUNNING=false;
        if (TCPLISTENER != null)
        {
            TCPLISTENER.Stop();
        }
        lock(TCP_CLIENTS)
        {
            foreach (TcpClient client in TCP_CLIENTS)
            {
                client.Close();
            }
        }
        lock(m_lock)
        {
            Monitor.PulseAll(m_lock);
        }

        if (SERVER_THREAD != null && !SERVER_THREAD.Join(2000))
        {
            try
            {
                SERVER_THREAD.Abort();
            }
            catch (Exception)
            {
            }
        }
        // closed after the loop has stopped: closing a socket that another thread is reading can block
        if (UDP_SOCK != null)
        {
            UDP_SOCK.Close();
        }

    }

//====================================================================================================================================
//Qtegra Methods
//====================================================================================================================================
    public static string GetDeflections(string[] args)
    {
        List<string> data = new List<string>();
        double v;

        string dets = String.Join(" ", Slice(args,1,0));

        foreach(string k in dets.Split(','))
        {
            if(!Instrument.GetParameter(String.Format("Deflection {0} Set", k), out v))
            {
                v = 0;
            }
            data.Add(Fmt(v));
        }

        return string.Join(",",data.ToArray());
    }
    public static string GetParameters(string names)
    {
        List<string> data = new List<string>();
        double v;
        foreach(string k in names.Split(','))
        {
           if (!Instrument.GetParameter(k.Trim(), out v))
           {
                v = 0;
           }
           data.Add(Fmt(v));
        }

        return string.Join(",",data.ToArray());
    }

    // True for each parameter Qtegra can read, False otherwise
    public static string CheckParameters(string names)
    {
        List<string> data = new List<string>();
        double v;
        foreach(string k in names.Split(','))
        {
            data.Add(Instrument.GetParameter(k.Trim(), out v) ? "True" : "False");
        }
        return string.Join(",",data.ToArray());
    }

    // Diagnostic: log what the instrument object exposes, to look for a list of its hardware parameters
    private static string ProbeInstrument()
    {
        log("==== ProbeInstrument start ====");
        log(String.Format("Instrument type={0}, .NET={1}", Instrument.GetType().FullName, Environment.Version));
        logproperties(Instrument, "Instrument");
        listattributes(Instrument.GetType(), "Instrument");
        log("==== ProbeInstrument end ====");
        return "OK";
    }
	public static string mActivateCup(string a, string b)
    {
        string result;
        if(ActivateCupConfiguration(a, b))
        {
            result="OK";
        }
        else
        {
            result=String.Format("Error: could not set cup={0}, sub cup to {1} ", a, b);
        }
        return result;
    }
    public static string GetMagnetMoving()
    {
        if (MAGNET_MOVING)
        {
            return "True";
        }
        else
        {
            return "False";
        }

    }
    public static void ProtectDetector(string detname, string state)
    {
        string param=String.Format("Deflection {0} Set",detname);
        if (state.ToLower()=="on")
        {
            double v;
            Instrument.GetParameter(param, out v);
            m_defl[detname]=v;
            SetParameter(param, 2000);
        }
        else
        {
            if (m_defl.ContainsKey(detname))
            {
                SetParameter(param, m_defl[detname]);
            }

	    }
	}
	public static String SetIonCounterState(String name, bool state)
	{
		Logger.Log (LogLevel.UserInfo, String.Format("Setting IonCounterState {0} {1}", name, state));

        // Adapted from CSIRORemoteControlServer.cs
		if (!ActivateCounterCup(state, name))
		{
		    return String.Format("Error: could not set {0} active={1}", name, state);
		}
		Thread.Sleep(1000); // Wait for a little time for the activation to take effect...

		// Restart the monitor scan, so data is (or is no longer) reported for the CDD...
		if (!RunMonitorScan(Instrument.MeasurementInfo.ActualScanMass))
		{
			return "Error: could not restart monitor scan.";
		}

	   return "OK";
	}

	public static string GetValveState(string hwname)
	{
		string result="Error";
		double rawValue;
		if(Instrument.GetParameter(hwname,out rawValue))
		{
			if (rawValue==OPEN)
			{
				result="True";
			}
			else
			{
				result="False";
			}
		}
		return result;

	}

	public static string SetParameter(string hwname, int val)
	{
		string result="OK";
		if (!Instrument.SetParameter(hwname, val))
		{
			result=String.Format("Error: could not set {0} to {1}", hwname, val);
		}
		return result;
	}

	public static string SetParameter(string hwname, double val)
	{	string result="OK";

		if (!Instrument.SetParameter(hwname, val))
		{
			result=String.Format("Error: could not set {0} to {1}", hwname, Fmt(val));
		}
		return result;
	}

	// read a hardware parameter, multiplied by scale, formatted for the client
	public static string ReadParameter(string hwname, double scale)
	{
		double r;
		if (!Instrument.GetParameter(hwname, out r))
		{
			return String.Format("Error: could not read {0}", hwname);
		}
		return Fmt(r*scale);
	}

	// numbers to and from clients always use "." as the decimal separator, whatever the Windows locale
	private static double ToDouble(string s)
	{
		return Double.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
	}

	private static string Fmt(double v)
	{
		return v.ToString(CultureInfo.InvariantCulture);
	}

    private static string ActivateAllCDDs()
	{
		// activate the CDDs...
		if (!ActivateAllCounterCups(true))
		{
		    return "Could not activate CDDs.";
		}
		Thread.Sleep(1000); // Wait for a little time for the activation to take effect...

		// Restart the monitor scan, to ensure data is reported for the CDD...
		if (!RunMonitorScan(Instrument.MeasurementInfo.ActualScanMass))
		{
			return "Could not restart monitor scan.";
		}

		return "OK";
	}

	private static string DeactivateAllCDDs()
	{

		if (!ActivateAllCounterCups(false))
		{
			return "Could not deactivate CDDs.";
		}

		//This ensures that after the deactivation, data is not reported for the CDD...
		if (!RunMonitorScan(Instrument.MeasurementInfo.ActualScanMass))
		{
			return "Could not restart monitor scan.";
		}
		Thread.Sleep(1000); //need to wait for a second before changes are done

		return "OK";
	}

	// Adapted from activate_ion_counter / SampleFunctionIncludeScript.cs
	private static bool ActivateAllCounterCups(bool activate)
	{
		try
		{
			Instrument.SetCDDActivationMode(new CollectorActivationMode(activate ? DetectorActivateMode.Standby : DetectorActivateMode.Off));
			IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
			foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
			{
				if ((col.CollectorType == IRMSBaseCollectorType.CounterCup) && col.Mass.HasValue)
				{
					col.Active = activate;
				}
			}
			UpdateCounterCupProtection(activeCupData);
		}
		catch (Exception e)
		{
			Logger.Log(LogLevel.UserError, String.Format("Could not set counter cups active={0}. {1}", activate, e));
			return false;
		}
		return true;
	}

	private static bool ActivateCounterCup(bool activate, string name)
	{
		try
		{
			string id = mGetDetectorIdentifier(name);
			bool found = false;
			IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
			foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
			{
				if ((col.CollectorType == IRMSBaseCollectorType.CounterCup) && col.Mass.HasValue &&
					(col.Identifier == id || col.Appearance.Label == name))
				{
					col.Active = activate;
					found = true;
				}
			}
			if (!found)
			{
				Logger.Log(LogLevel.UserError, String.Format("Counter cup {0} not found in active cup configuration", name));
				return false;
			}
			if (activate)
			{
				Instrument.SetCDDActivationMode(new CollectorActivationMode(DetectorActivateMode.Standby));
			}
			UpdateCounterCupProtection(activeCupData);
		}
		catch (Exception e)
		{
			Logger.Log(LogLevel.UserError, String.Format("Could not set {0} active={1}. {2}", name, activate, e));
			return false;
		}
		return true;
	}

	// Protect all counter cups, then unprotect only the ones that are still active
	private static void UpdateCounterCupProtection(IRMSBaseCupConfigurationData activeCupData)
	{
		List<string> activeCups = new List<string>();
		foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
		{
			if ((col.CollectorType == IRMSBaseCollectorType.CounterCup) && col.Active)
			{
				activeCups.Add(col.Appearance.Label);
			}
		}

		Instrument.ProtectAllCounterCupsWithPostDelay(Instrument.BaseSettingsData.CounterProtectionPreDelay);
		if (activeCups.Count > 0)
		{
			Instrument.UnprotectRequiredCounterCups(activeCups);
		}
	}

	// Mass on a cup = GammaCorrection * axial mass (see GetActualMass in CSIRORemoteControlServer.cs)
	private static double? ConvertMassToAxial(string name, double mass)
	{
		string id = mGetDetectorIdentifier(name);
		IRMSBaseCupConfigurationData activeCupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
		foreach(IRMSBaseCollectorItem col in activeCupData.CollectorItemList)
		{
			if ((col.Identifier == id || col.Appearance.Label == name) && col.GammaCorrection != 0)
			{
				return mass / col.GammaCorrection;
			}
		}
		return null;
	}

    private static string SetMass(double m, string cup)
	{
		if (!TryStartMagnetMove())
		{
			return "Error: magnet is moving";
		}
		try
		{
			return mSetMass(m, cup);
		}
		finally
		{
			MAGNET_MOVING=false;
		}
	}

	private static string mSetMass(double m, string cup)
	{
		// First deactivate CDDs to protect them.
		if (!(DeactivateAllCDDs()=="OK"))
		{
			return "Error: Could not deactivate CDDs. Mass not changed.";
		}

		// If mass is not for AX cup, convert the mass to a value for the AX cup
		double AXMass = m;
		log(Fmt(AXMass));
		if (cup!=null && cup.Trim()!="AX")
		{
			double? tempMass = ConvertMassToAxial(cup.Trim(),m);
			if (!tempMass.HasValue)
			{
				return "Error: Cup not recognized";
			}
			else
			{
				AXMass = tempMass ?? 0;
			}
		}

		// Now change the mass.
		double initialMass = Instrument.MeasurementInfo.ActualScanMass;
		double currentMass = initialMass;
		int count = 0;
		while (currentMass != AXMass) //For large changes, do it stepwise to avoid ringing
		{
			if (currentMass<=0 || count>=100 ||
				(Math.Abs(currentMass-AXMass)/currentMass) < Config.magnet_move_threshold_rel_mass) //for small changes
			{
				currentMass = AXMass;
			}
			else // for large changes only change by a certain amount and repeat
			{
				currentMass += currentMass*Config.magnet_move_threshold_rel_mass*((AXMass>currentMass) ? 1 : -1);
			}
			if (!RunMonitorScan(currentMass))
			{
				return "Error: Could not change mass.";
			}
			if (Config.magnet_step_time>0)
            {
                Thread.Sleep(Config.magnet_step_time);
            }
            count++;
		}
        Logger.Log(LogLevel.UserInfo,String.Format("Mass was changed from {0} to {1} on the AX in {2} step(s).",initialMass,currentMass,count));

		return "OK";
	}

	public static string SetMagnetDAC(double d)
	{

		string result="OK";
		double current_dac;

		if (!Instrument.GetParameter("Field Set", out current_dac))
		{
			return "Error: could not read Field Set";
		}
		if (!TryStartMagnetMove())
		{
			return "Error: magnet is moving";
		}

		double dev=Math.Abs(d-current_dac);
		if (dev>Config.magnet_move_threshold)
		{
            // MAGNET_MOVING stays true until the stepping thread finishes
            Thread t= new Thread(delegate(){mSetMagnetDAC(d,dev,current_dac);});
            t.Start();
        }
        else
        {
            try
            {
                result=SetParameter("Field Set", d);
            }
            finally
            {
                MAGNET_MOVING=false;
            }
        }
        return result;

    }

    public static void mSetMagnetDAC(double d, double dev, double current_dac)
    {
        try
        {
            //incrementally move the magnet to eliminate "ringing"
            double step=dev/Config.magnet_steps;
            int sign=1;

            if (current_dac>d)
            {
                sign=-1;
            }

            for(int i=1; i<=Config.magnet_steps; i++)
            {
                SetParameter("Field Set", current_dac+sign*i*step);
                if (Config.magnet_step_time>0)
                {
                    Thread.CurrentThread.Join(Config.magnet_step_time);
                }
            }
        }
        catch (Exception e)
        {
            Logger.Log(LogLevel.UserError, String.Format("Magnet move failed. {0}", e));
        }
        finally
        {
            MAGNET_MOVING=false;
        }
    }

    // only one magnet move (SetMagnetDAC or SetMass) at a time
    private static bool TryStartMagnetMove()
    {
        lock(m_magnet_lock)
        {
            if (MAGNET_MOVING)
            {
                return false;
            }
            MAGNET_MOVING=true;
            return true;
        }
    }

    public static string GetIntegrationTime()
    {
        string result=Fmt(Instrument.MeasurementInfo.IntegrationTime*0.001);
        return result;
    }

    public static string SetIntegrationTime(double t)
    {
        //t in ms
        string result="OK";
        IRMSBaseMeasurementInfo nMI= new IRMSBaseMeasurementInfo(Instrument.MeasurementInfo);
        nMI.IntegrationTime = t*1000;
        if (!Instrument.ScanTransitionController.StartMonitoring (nMI))
        {
            Logger.Log(LogLevel.UserError, "Could not start the modified monitor");
            result=String.Format("Error: could not set integration time to {0}",Fmt(t));
        }

        return result;
    }

    public static string SetGain(String name, double v)
    {
        string id = mGetDetectorIdentifier(name);
        foreach (UFCCalibrationData item in Instrument.UFCCalibrationData)
        {
            if (item.Identifier==id)
            {
                item.Gain=v;
                return "OK";
            }
        }
        return String.Format("Error: unknown detector {0}", name);
    }

    public static string GetGain(String name)
    {
        string id = mGetDetectorIdentifier(name);
        foreach (UFCCalibrationData item in Instrument.UFCCalibrationData)
        {
            if (item.Identifier==id)
            {
                return Fmt(Convert.ToDouble(item.Gain));
            }
        }
        return String.Format("Error: unknown detector {0}", name);
	}

    private static string get_cup_name(IRMSBaseCollectorItem item)
    {
        foreach (string detname in DETECTOR_NAMES)
        {
            string[] args=detname.Split(',');
            if(args[0]==item.Identifier)
            {
                return args[1];
            }
        }
        return "foo";
    }

	public static void ScanDataAvailable(object sender, EventArgs<Spectrum> e)
	{
		lock(m_lock)
		{

			List<string> data = new List<string>();
			Spectrum spec = e.Value.Clone() as Spectrum;
			IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();

			if (PROBE_PENDING)
			{
				PROBE_PENDING=false;
				ProbeScanData(spec, cupData);
			}

			foreach (Series series in spec)
			{
				foreach (SpectrumData point in series)
				{
					//get the name of the detector
					foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
					{
						if (item.Mass==point.Mass)
						{	string cupName=get_cup_name(item);


							data.Add(Fmt(point.Analog));
							if (Config.tag_data)
							{
								data.Add(cupName);
							}

							break;
						}
					}

				}
			}
			data.Reverse();
			SCAN_DATA=string.Join(",",data.ToArray());
			SCAN_COUNT++;
			Monitor.PulseAll(m_lock); // wake GetNextData and Subscribe
		}
	}

	// Diagnostic: log what a scan contains so we can tell whether a Series knows which
	// detector it came from, and how far point masses drift from the cup configuration masses.
	private static void ProbeScanData(Spectrum spec, IRMSBaseCupConfigurationData cupData)
	{
		try
		{
			log("==== ProbeScanData start ====");
			log(String.Format("ActualScanMass={0}", Instrument.MeasurementInfo.ActualScanMass));
			foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
			{
				log(String.Format("Collector Identifier={0}, Label={1}, Type={2}, Active={3}, Mass={4}, GammaCorrection={5}",
					item.Identifier, item.Appearance.Label, item.CollectorType, item.Active, item.Mass, item.GammaCorrection));
			}

			logproperties(spec, "Spectrum");
			listattributes(spec.GetType(), "Spectrum");

			int i=0;
			foreach (Series series in spec)
			{
				logproperties(series, String.Format("Series[{0}]", i));
				if (i==0)
				{
					listattributes(series.GetType(), "Series");
				}

				int j=0;
				foreach (SpectrumData point in series)
				{
					if (j>=20)
					{
						log(String.Format("Series[{0}] has more points, not logged", i));
						break;
					}
					logproperties(point, String.Format("Series[{0}].Point[{1}]", i, j));
					if (i==0 && j==0)
					{
						listattributes(point.GetType(), "SpectrumData");
					}
					j++;
				}
				i++;
			}
			log("==== ProbeScanData end ====");
		}
		catch (Exception e)
		{
			Logger.Log(LogLevel.Warning, String.Format("ProbeScanData failed. {0}", e));
		}
	}

	private static void logproperties(object obj, string identifier)
	{
		foreach (PropertyInfo pi in obj.GetType().GetProperties())
		{
			if (pi.GetIndexParameters().Length>0)
			{
				continue;
			}
			string value;
			try
			{
				value=String.Format("{0}", pi.GetValue(obj, null));
			}
			catch (Exception e)
			{
				value=String.Format("<{0}>", e.GetType().Name);
			}
			log(String.Format("{0}.{1}={2}", identifier, pi.Name, value));
		}
	}

//	private static bool UpdateMonitorScan(bool enable)
//	{
//        IRMSBaseCupConfigurationData config = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
//        IRMSBaseMeasurementInfo monitor_measurement_info = new IRMSBaseMeasurementInfo(
//            Instrument.MeasurementInfo.ScanType,
//            Instrument.MeasurementInfo.IntegrationTime,
//            Instrument.MeasurementInfo.SettlingTime,
//            config.CollectorItemList.GetMasterCollectorItem().Mass.Value,
//            config.CollectorItemList,
//            config.MassCalibration
//        );
//        success = Instrument.ScanTransitionController.StartMonitoring(monitor_measurement_info);
//        success = Instrument.ScanTransitionController.StartMonitoring(monitor_measurement_info);
//        if (!success) Logger.Log(LogLevel.UserError, "Failed to update monitoring.");
//        else if (enable) Logger.Log(LogLevel.UserInfo, "Monitoring has been updated.");
//        return success;
//	}

	private static bool RunMonitorScan (double? mass)
	{
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
		IRMSBaseMeasurementInfo newMeasurementInfo =
			new IRMSBaseMeasurementInfo(
				Instrument.MeasurementInfo.ScanType,
				Instrument.MeasurementInfo.IntegrationTime,
				Instrument.MeasurementInfo.SettlingTime,
				(mass.HasValue) ? mass.Value : cupData.CollectorItemList.GetMasterCollectorItem().Mass.Value,
				cupData.CollectorItemList,
				cupData.MassCalibration
				);
		if (!Instrument.ScanTransitionController.StartMonitoring(newMeasurementInfo))
		{
			Logger.Log(LogLevel.UserError, "Could not start monitor scan");
			return false;
		}
		return true;
	}

	private static string GetTuningSettings()
	{
		TuneSettingsManager tsm = new TuneSettingsManager(Instrument.Id);
		List<string> result = new List<string>();
		tsm.EntryType= typeof(TuneSettings);
		result.AddRange(tsm.GetEntries());
		return string.Join(",",result.ToArray());
	}

	private static bool SetTuningSettings(string name)
	{
		TuneSettingsManager tsm = new TuneSettingsManager(Instrument.Id);
		TuneSettings tuneSettings = tuneSettings = tsm.ReadEntry(name) as TuneSettings; ;
		//TuneSettings tuneSettings =tsm.ReadEntry(name);
		TuneParameterBlock tuneBlock = null;
		if (tuneSettings == null)
		{
			Logger.Log(LogLevel.UserError, String.Format("Could not load tune setting \'{0}\'.", name));
			return false;
		}
		else
		{
			tuneBlock = tuneSettings.Object as TuneParameterBlock;
			if (tuneBlock == null)
			{
				Logger.Log(LogLevel.UserError, string.Format("Tune setting \'{0}\' could not convert to tuneblock.", name));
				return false;
			}
			else
			{
				Instrument.TuneParameters.Parameters = tuneBlock;
				Logger.Log(LogLevel.UserInfo, string.Format("Tune setting : \'{0}\' successfully load!", name));
			}
		}
		return true;
	}

	private static string GetSubCupParameters()
	{
		// deflection of each detector of the selected instrument, then the ion counter supply
		List<string> parameters = new List<string>();
		foreach (string detname in DETECTOR_NAMES)
		{
			parameters.Add(String.Format("Deflection {0} Set", detname.Split(',')[1]));
		}
		parameters.Add("CDD Supply Set");

		double value=0.0;
		List<string> data = new List<string>();
		foreach (var item in parameters)
		{
			if(Instrument.GetParameter(item, out value))
			{
				data.Add(String.Format("{0},{1}", item, Fmt(value)));
			}
		}
		return String.Join(";", data.ToArray());
	}
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Get all cup configurations names from the system.
	// </summary>
	// <returns>A list of cup configuration names.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static List<string> GetCupConfigurations ()
	{
		List<string> result = new List<string>();
		foreach (var item in Instrument.CupConfigurationDataList) {
			result.Add (item.Name);
		}
		return result;
	}
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Get all sub cup configurations names from a cup cupconfiguration.
	// </summary>
	// <param name="cupConfigurationName"></param>
	// <returns>A list of sub cup configuration names.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static List<string> GetSubCupConfigurations (string cupConfigurationName)
	{
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.FindCupConfigurationByName (cupConfigurationName);
		if (cupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find cup configuration \'{0}\'.", cupConfigurationName));
			return null;
		}

		List<string> result = new List<string>();
		foreach (var item in cupData.SubCupConfigurationList) {
			result.Add (item.Name);
		}


		return result;
	}

	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	// <summary>
	// Activate a cup configuration and sub cup configuration.
	// </summary>
	// <param name="cupConfigurationName">The cup configuration name.</param>
	// <param name="subCupConfigurationName">The sub cup configuration name.</param>
	// <param name="mass">A nullable mass value. If has a value then use the mass for the monitor scan; otherwise use the master cup mass for the monitor scan.</param>
	// <returns><c>True</c> if successfull; otherwise <c>false</c>.</returns>
	//-------------------------------------------------------------------------------------------------------------------------------------------------------------
	private static bool ActivateCupConfiguration (string cupConfigurationName, string subCupConfigurationName)
	{

		//Console.WriteLine (cupConfigurationName);
		//Console.WriteLine (subCupConfigurationName);
		IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.FindCupConfigurationByName (cupConfigurationName);
		if (cupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find cup configuration \'{0}\'.", cupConfigurationName));
			return false;
		}
		IRMSBaseSubCupConfigurationData subCupData = cupData.SubCupConfigurationList.FindSubCupConfigurationByName (subCupConfigurationName);
		if (subCupData == null) {
			Logger.Log (LogLevel.UserError, String.Format ("Could not find sub cup configuration \'{0}\' in cup configuration.", subCupConfigurationName, cupConfigurationName));
			return false;
		}
		Instrument.CupConfigurationDataList.SetActiveItem (cupData.Identifier, subCupData.Identifier, Instrument.CupSettingDataList, null);
		Instrument.SetHardwareParameters (cupData, subCupData);
		bool success = Instrument.RequestCupConfigurationChange (Instrument.CupConfigurationDataList);
		if (!success) {
			Logger.Log (LogLevel.UserError, "Could not request a cup configuration change.");
			return false;
		}
		return true;
	}



//====================================================================================================================================
//Server Methods
//====================================================================================================================================

    private static void TCPServeForever ()
    {
        Logger.Log (LogLevel.UserInfo, "Starting TCP Server.");
        TCPLISTENER = new TcpListener (IPAddress.Any, Config.port);
        TCPLISTENER.Start ();


        SERVER_THREAD = new Thread (new ThreadStart (TCPListen));
        SERVER_THREAD.Start ();

    }
    private static void UDPServeForever()
    {
        Logger.Log (LogLevel.UserInfo, "Starting UDP Server.");
        SERVER_THREAD = new Thread (new ThreadStart (UDPListen));
        SERVER_THREAD.Start ();

    }
    private static void UDPListen()
    {
        Logger.Log (LogLevel.UserInfo, "UDP Listening.");
        int recv;
        byte[] data= new byte[1024];

        IPEndPoint ipep = new IPEndPoint(IPAddress.Any, Config.port);
        UDP_SOCK = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Windows: stop a reply to a client that has gone away from making the next ReceiveFrom fail
            const int SIO_UDP_CONNRESET = -1744830452;
            UDP_SOCK.IOControl(SIO_UDP_CONNRESET, new byte[]{0, 0, 0, 0}, null);
        }
        catch (Exception)
        {
        }
        UDP_SOCK.Bind(ipep);
        UDP_SOCK.ReceiveTimeout = 500; // wake up regularly to check RUNNING

        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        EndPoint remote = (EndPoint)(sender);

        while(RUNNING)
        {
            try
            {
                recv=UDP_SOCK.ReceiveFrom(data, ref remote);
                string rdata = Encoding.ASCII.GetString(data,0, recv);

                string result = HandleRequest(rdata, remote.ToString());

                //Logger.Log(LogLevel.Debug, String.Format("Sending back {0}", result));
                UDP_SOCK.SendTo(Encoding.ASCII.GetBytes(result), remote);
            } catch (Exception e) {
                SocketException se = e as SocketException;
                if (se != null && se.SocketErrorCode == SocketError.TimedOut)
                {
                    continue; // nothing received in the last 500 ms
                }
                if (!RUNNING)
                {
                    break;
                }
                string error = string.Format ("Could not read from UDP sock. {0}", e.ToString ());
                Logger.Log(LogLevel.Warning, error);
            }

        }
    }
    private static void TCPListen ()
    {

        Logger.Log (LogLevel.UserInfo, "TCP Listening");

        while (RUNNING) {
            try
            {
                // one thread per connection, so a slow or silent client does not block the others
                TcpClient client = TCPLISTENER.AcceptTcpClient ();
                Thread clientThread = new Thread (delegate() { TCPHandle(client); });
                clientThread.IsBackground = true;
                clientThread.Start ();
            }
            catch (Exception e)
            {
                if (!RUNNING)
                {
                    break;
                }
                Logger.Log(LogLevel.Warning, String.Format("TCP accept failed. {0}", e));
            }
        }
    }

    // A connection handles one command and is closed after the reply, as clients have always used it,
    // unless the first command is
    //    KeepAlive  - reply "OK\n", then handle commands ending in CR, LF or CRLF, each answered with reply + "\n",
    //                 until the client disconnects or is idle for Config.tcp_idle_timeout ms
    //    Subscribe  - write "<scan count>|<data>\n" for every scan until the client disconnects
    private static void TCPHandle (TcpClient tcpClient)
    {
        lock(TCP_CLIENTS)
        {
            TCP_CLIENTS.Add(tcpClient);
        }
        try
        {
            tcpClient.ReceiveTimeout = Config.tcp_read_timeout;
            tcpClient.SendTimeout = Config.tcp_read_timeout;
            NetworkStream stream = tcpClient.GetStream ();
            string client = tcpClient.Client.RemoteEndPoint.ToString();

            string first = TCPRead(stream);
            if (first.Length == 0)
            {
                return;
            }

            int nl = first.IndexOfAny(LINE_ENDS);
            string firstcmd = ((nl < 0) ? first : first.Substring(0, nl)).Trim();
            if (firstcmd == "KeepAlive")
            {
                TCPWrite(stream, "OK\n");
                tcpClient.ReceiveTimeout = Config.tcp_idle_timeout;
                TCPKeepAlive(stream, client, new StringBuilder((nl < 0) ? "" : first.Substring(nl + 1)));
            }
            else if (firstcmd == "Subscribe")
            {
                TCPSubscribe(stream);
            }
            else
            {
                TCPWrite(stream, HandleRequest(first, client));
            }
        }
        catch (Exception e)
        {
            // timeouts and clients going away end up here
            Logger.Log(LogLevel.Debug, String.Format("TCP connection closed. {0}", e.Message));
        }
        finally
        {
            lock(TCP_CLIENTS)
            {
                TCP_CLIENTS.Remove(tcpClient);
            }
            tcpClient.Close();
        }
    }

    private static void TCPKeepAlive (NetworkStream stream, string client, StringBuilder buffer)
    {
        string line;
        while (RUNNING && (line = TCPReadLine(stream, buffer)) != null)
        {
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line == "Subscribe")
            {
                TCPSubscribe(stream);
                return;
            }
            TCPWrite(stream, HandleRequest(line, client) + "\n");
        }
    }

    private static void TCPSubscribe (NetworkStream stream)
    {
        long last;
        lock(m_lock)
        {
            last = SCAN_COUNT;
        }
        while (RUNNING)
        {
            string line = null;
            lock(m_lock)
            {
                if (SCAN_COUNT <= last)
                {
                    Monitor.Wait(m_lock, 1000);
                }
                if (SCAN_COUNT > last)
                {
                    last = SCAN_COUNT;
                    line = String.Format("{0}|{1}\n", SCAN_COUNT, SCAN_DATA);
                }
            }
            if (line != null)
            {
                // throws once the client has gone away, which ends the subscription
                TCPWrite(stream, line);
            }
        }
    }
    //====================================================================================================================================
    //Network Methods
    //====================================================================================================================================

    private static void TCPWrite (NetworkStream stream, string cmd)
    {
        if (stream.CanWrite) {
            Byte[] sendBytes = Encoding.ASCII.GetBytes (cmd);
            stream.Write (sendBytes, 0, sendBytes.Length);
        }
    }

    // read whatever the client has sent so far. "" if the client closed the connection
    private static string TCPRead (NetworkStream stream)
    {
        byte[] data = new byte[1024];
        StringBuilder myCompleteMessage = new StringBuilder ();
        int numberOfBytesRead = 0;

        // Incoming message may be larger than the buffer size.
        do {
            numberOfBytesRead = stream.Read (data, 0, data.Length);
            myCompleteMessage.Append (Encoding.ASCII.GetString (data, 0, numberOfBytesRead));
        } while (numberOfBytesRead > 0 && stream.DataAvailable);

        return myCompleteMessage.ToString ();
    }

    // CR, LF and CRLF all end a command. the empty line left by CRLF is skipped by the caller
    private static readonly char[] LINE_ENDS = new char[]{'\r', '\n'};

    // next line, using buffer for data already received. null if the client closed the connection
    private static string TCPReadLine (NetworkStream stream, StringBuilder buffer)
    {
        byte[] data = new byte[1024];
        while (true)
        {
            string pending = buffer.ToString();
            int nl = pending.IndexOfAny(LINE_ENDS);
            if (nl >= 0)
            {
                buffer.Remove(0, nl + 1);
                return pending.Substring(0, nl);
            }
            int n = stream.Read(data, 0, data.Length);
            if (n == 0)
            {
                return null;
            }
            buffer.Append(Encoding.ASCII.GetString(data, 0, n));
        }
    }


    //====================================================================================================================================
    //Helper Methods
    //====================================================================================================================================
    private static void mReportGains()
    {
        foreach (string d in DETECTOR_NAMES)
        {
            string dd=d.Split(',')[1];
            log(String.Format("Gain {0}={1}", dd, GetGain(dd)));
        }
    }

    private static void log(string msg)
    {
        Logger.Log(LogLevel.UserInfo, msg);
    }

    private static void listattributes(Type t, string identifier)
    {   Logger.Log(LogLevel.UserInfo, "List detector attributes");
        //Type t = item.GetType();
        //PropertyInfo[] pia = t.GetProperties();
        //Logger.Log(LogLevel.UserInfo, String.Format("GammaCor={0}", item.GammaCorrection));
        //foreach (PropertyInfo pi in pia)
        //{
        //  Logger.Log(LogLevel.UserInfo, String.Format("Name={0},Property {1}", item.Identifier,
        //          pi.ToString()));
        //}
        MemberInfo[] ms = t.GetMembers();
        foreach (MemberInfo mi in ms)
        {
        Logger.Log(LogLevel.UserInfo, String.Format("Name={0}, Member {1}",identifier,
                    mi.ToString()));
        }
        /*IRMSBaseCupConfigurationData cupData = Instrument.CupConfigurationDataList.GetActiveCupConfiguration();
        foreach (IRMSBaseCollectorItem item in cupData.CollectorItemList)
        {   Type t = item.GetType();
            PropertyInfo[] pia = t.GetProperties();
            //Logger.Log(LogLevel.UserInfo, String.Format("GammaCor={0}", item.GammaCorrection));
            foreach (PropertyInfo pi in pia)
            {
                Logger.Log(LogLevel.UserInfo, String.Format("Name={0},Property {1}", item.Identifier,
                        pi.ToString()));
            }
            MemberInfo[] ms = t.GetMembers();
            foreach (MemberInfo mi in ms)
            {
            Logger.Log(LogLevel.UserInfo, String.Format("Name={0}, Member {1}", item.Identifier,
                        mi.ToString()));
            }
        }*/
    }

    private static string mGetDetectorIdentifier(string name)
    {
        string ret="";
        foreach (string detname in DETECTOR_NAMES)
        {
            string[] args=detname.Split(',');
            if(args[1]==name)
            {
                ret=args[0];
                break;
            }
        }
        return ret;
    }

    //adapted from http://www.dotnetperls.com/array-slice
    private static string[] Slice(string[] source, int start, int end)
    {
    // Handles negative ends.
    if (end <= 0)
    {
        end = source.Length + end;
    }
    int len = end - start;

    // Return new array.
    string[] res = new string[len];
    for (int i = 0; i < len; i++)
    {
        res[i] = source[i + start];
    }
    return res;
    }
}

