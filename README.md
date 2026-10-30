qtegra
======

Remote Control Server for Qtegra

Intro
=====
RemoteControlServer.cs is a Qtegra-specific C# implementation of
inter-process communication (IPC) for Qtegra, Thermo's software suite for
control of Argus VI, Helix MC, Helix SFT and other Thermo mass spectrometers.
RemoteControlServer.cs was developed by Jake Ross at New Mexico Geochronology
Research Laboratory to enable independent client applications, such as Mass
Spec and Pychron, to control Thermo mass spectrometers running Qtegra. The
script listens over UDP or TCP for ASCII commands from a client application.
When a command is received it is executed by Qtegra and the result, or a
descriptive error, is returned to the client.

Files
=====
    RemoteControlServer.cs          the server script, run inside Qtegra
    PROTOCOL.md                     protocol and command reference (generated)
    RemoteControlServerChanges.txt  change log
    CSIRORemoteControlServer.cs     fork maintained by CSIRO (Suckow, Deslandes, Gerber)
    SampleFunctionScript.cs,
    SampleFunctionIncludeScript.cs,
    activate_ion_counter            Thermo sample code the server borrows from
    tools/offline_check/            compile, PROTOCOL.md and server checks without Qtegra
    tools/smoke_test.py             read-only test client for a running server
    tools/list_parameters.py        candidate parameter names from a .imhwd file

Running
=======
Open RemoteControlServer.cs in Qtegra (Status Panel - Script List) and run it.

Configuration
=============
Select the instrument in Main(). Leave exactly one line uncommented:

    //Instrument = ArgusMC;    DETECTOR_NAMES = Config.ARGUS_DETECTOR_NAMES;
    //Instrument = HelixMC;    DETECTOR_NAMES = Config.HELIX_MC_DETECTOR_NAMES;
    Instrument = HelixSFT;     DETECTOR_NAMES = Config.HELIX_SFT_DETECTOR_NAMES;

DETECTOR_NAMES maps Qtegra collector identifiers to the names clients use,
e.g. "CUP 4,H2". Edit the lists in the Config class if your collectors differ.

Everything else is in the Config class at the top of the script:

    port                            port to listen on (default 1069)
    use_udp                         true for UDP (default), false for TCP
    tag_data                        GetData format. false for MassSpec 7.868
                                    (values only), true for MassSpec 7.873+
                                    (name,value pairs)
    use_beam_blank                  false makes BlankBeam a no-op
    magnet_move_threshold           DAC change above which SetMagnetDAC steps
    magnet_steps, magnet_step_time  number of steps and ms between them
    magnet_move_threshold_rel_mass  relative mass change above which SetMass steps
    probe_scan_data                 log the structure of the first scan (diagnostic)
    debug                           also log polled commands (GetData, GetDataN,
                                    GetNextData, Ping). off by default
    tcp_read_timeout                ms a TCP client has to send its command (5000)
    tcp_idle_timeout                ms an idle KeepAlive connection stays open (60000)
    request_id_window               ms a repeated "#id command" is answered from
                                    cache instead of running again (10000)

Protocol
========
See PROTOCOL.md: message format, request ids, UDP and TCP (KeepAlive,
Subscribe), error replies, every command, and how to find hardware parameter
names. A running server also answers "Help" and "Help <command>".

Diagnostics
===========
ProbeInstrument logs the instrument object's properties and members, to look
for a way to list its hardware parameters from the script.

ProbeScanData logs what the next scan contains (collector masses, gamma
corrections, and the properties of Spectrum, Series and SpectrumData) to the
Qtegra log. It also runs once on the first scan when probe_scan_data is true.
Use it to see how scan masses compare to the cup configuration after a magnet
move.

Testing
=======
Without Qtegra (needs the .NET SDK):

    tools/offline_check/run.sh

This compiles RemoteControlServer.cs as C# 4 against stand-ins for the Thermo
types, checks PROTOCOL.md is up to date, then runs commands, request ids, scan
data and the UDP and TCP servers with a fake instrument. The stand-ins are guessed from how the script uses the
Thermo API, so passing here does not guarantee the script compiles in Qtegra.

Against a running server (Python 3, read-only commands only):

    python3 tools/smoke_test.py <host> [--port 1069] [--tcp] [--detectors H1,AX,CDD]
                                [--parameters names.txt]

Adding commands
===============
Register the command in RegisterCommands(), under the right Section(...):

    Register("GetFoo", "", "<value> | Error", "Foo Set, in V.",
        (a, rest) => ReadParameter("Foo Set", 1));

The arguments are: name, arguments, reply, description, handler. Use
RegisterQuick for commands that do not touch the hardware. In the handler, a is
the command split on spaces (a[0] is the command name) and rest is everything
after the command name. Return a value, "OK" or "Error: ...". Missing arguments
and bad numbers are turned into error replies for you.

Then regenerate the docs:

    tools/offline_check/run.sh --update-protocol

Instrument is the entry point into the Qtegra core, e.g.

    Instrument.GetParameter("Field Set", out value);
    Instrument.SetParameter("Field Set", value);

Notes
=====
SetIntegrationTime was disabled for a while because Qtegra 1.0.597.6 behaved
unexpectedly when Mass Spec changed the integration time. As of 1/21/14 this
was only an issue with Mass Spec; SetIntegrationTime is enabled from version 009.

GetHV returns kV but SetHV takes V. Left as is so existing clients keep working.
