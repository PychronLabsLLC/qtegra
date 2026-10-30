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
    rcs_protocol.txt                command reference
    RemoteControlServerChanges.txt  change log
    CSIRORemoteControlServer.cs     fork maintained by CSIRO (Suckow, Deslandes, Gerber)
    SampleFunctionScript.cs,
    SampleFunctionIncludeScript.cs,
    activate_ion_counter            Thermo sample code the server borrows from
    protocol.txt                    Pychron system/laser protocol (not Qtegra)
    tools/offline_check/            compile + dispatcher check without Qtegra
    tools/smoke_test.py             read-only test client for a running server

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

Protocol
========
Commands are ASCII, case sensitive and CamelCase, with arguments separated from
the command by a space, e.g. "SetMagnetDAC 5.1" or "SetDeflection AX,100".
Numbers always use "." as the decimal separator, whatever the Windows locale.
Each command gets exactly one reply: a value, "OK", or a line starting with
"Error:".

    Help        lists every command
    GetVersion  returns the script version

See rcs_protocol.txt for the full command list.

Over TCP the server handles one command per connection: connect, send the
command, read the reply until the server closes the connection.

Diagnostics
===========
ProbeScanData logs what the next scan contains (collector masses, gamma
corrections, and the properties of Spectrum, Series and SpectrumData) to the
Qtegra log. It also runs once on the first scan when probe_scan_data is true.
Use it to see how scan masses compare to the cup configuration after a magnet
move.

Testing
=======
Without Qtegra (needs the .NET SDK):

    tools/offline_check/run.sh

This compiles RemoteControlServer.cs against stand-ins for the Thermo types and
runs the command dispatcher with a fake instrument. The stand-ins are guessed
from how the script uses the Thermo API, so passing here does not guarantee the
script compiles in Qtegra.

Against a running server (Python 3, read-only commands only):

    python3 tools/smoke_test.py <host> [--port 1069] [--tcp] [--detectors H1,AX,CDD]

Adding commands
===============
Register the command in RegisterCommands():

    Register("GetFoo", (a, rest) => ReadParameter("Foo Set", 1));

a is the command split on spaces (a[0] is the command name), rest is
everything after the command name. Return a value, "OK" or "Error: ...".
Missing arguments and bad numbers are turned into error replies for you.

Instrument is the entry point into the Qtegra core, e.g.

    Instrument.GetParameter("Field Set", out value);
    Instrument.SetParameter("Field Set", value);

Notes
=====
SetIntegrationTime was disabled for a while because Qtegra 1.0.597.6 behaved
unexpectedly when Mass Spec changed the integration time. As of 1/21/14 this
was only an issue with Mass Spec; SetIntegrationTime is enabled from version 009.

GetHV returns kV but SetHV takes V. Left as is so existing clients keep working.
