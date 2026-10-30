#!/usr/bin/env python3
"""Read-only smoke test for a running RemoteControlServer.

Sends only Get* commands plus a few malformed commands that the server rejects
before touching any hardware, and checks every reply.

    python3 tools/smoke_test.py <host> [--port 1069] [--tcp] [--detectors H1,AX,CDD]

Exit code is 0 when every check passes, 1 otherwise.
"""
import argparse
import socket
import sys


def send_udp(host, port, cmd, timeout):
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
        s.settimeout(timeout)
        s.sendto(cmd.encode("ascii"), (host, port))
        data, _ = s.recvfrom(65535)
        return data.decode("ascii", "replace")


def send_tcp(host, port, cmd, timeout):
    # the server handles one command per connection and closes it after replying
    with socket.create_connection((host, port), timeout=timeout) as s:
        s.sendall(cmd.encode("ascii"))
        chunks = []
        while True:
            chunk = s.recv(4096)
            if not chunk:
                break
            chunks.append(chunk)
        return b"".join(chunks).decode("ascii", "replace")


def is_number(reply):
    try:
        float(reply)
        return True
    except ValueError:
        return False


def is_error(reply):
    return reply.startswith("Error")


def ok_reply(reply):
    return not is_error(reply)


def build_checks(detectors):
    """(command, check, description). Commands here must never change hardware state."""
    checks = [
        ("GetVersion", ok_reply, "version string"),
        ("Help", lambda r: "GetData" in r.split(","), "command list includes GetData"),
        ("Ping", lambda r: r == "OK", "OK"),
        ("#7 Ping", lambda r: r == "#7 OK", "reply tagged with the request id"),
        ("GetData", lambda r: ok_reply(r) or r == "Error: no data", "scan data or no data yet"),
        ("GetDataN", lambda r: r.split("|")[0].isdigit() or r == "Error: no data", "<count>|<data>"),
        ("GetNextData 5", lambda r: r.split("|")[0].isdigit(), "a new scan within 5 s (is the monitor scan running?)"),
        ("GetIntegrationTime", is_number, "number"),
        ("GetTuningSettingsList", ok_reply, "tune settings"),
        ("GetCupConfigurationList", ok_reply, "cup configurations"),
        ("GetActiveCupConfiguration", ok_reply, "active cup configuration"),
        ("GetActiveSubCupConfiguration", ok_reply, "active sub cup configuration"),
        ("GetSubCupParameters", ok_reply, "sub cup parameters"),
        ("GetMagnetDAC", is_number, "number"),
        ("GetMagnetMoving", lambda r: r in ("True", "False"), "True or False"),
        ("GetHighVoltage", is_number, "number (V)"),
        ("GetHV", is_number, "number (kV)"),
        ("GetIonCounterVoltage", is_number, "number"),
        ("GetDeflections " + ",".join(detectors),
         lambda r: len(r.split(",")) == len(detectors) and all(is_number(v) for v in r.split(",")),
         "one number per detector"),
        # rejected before anything is changed
        ("NoSuchCommand", lambda r: r == "Error: Invalid Command", "invalid command error"),
        ("SetMagnetDAC", lambda r: r.startswith("Error: missing argument"), "missing argument error"),
        ("SetMagnetDAC abc", lambda r: r.startswith("Error: invalid number"), "invalid number error"),
    ]
    for det in detectors:
        checks.append(("GetDeflection " + det, is_number, "number"))
        checks.append(("GetGain " + det, is_number, "number"))
    return checks


def check_keepalive(host, port, timeout):
    """KeepAlive connection: several newline terminated commands on one connection. Returns failures."""
    expected = ["OK", "OK", "#2 OK"]
    try:
        with socket.create_connection((host, port), timeout=timeout) as s:
            f = s.makefile("rw", encoding="ascii", newline="\n")
            f.write("KeepAlive\nPing\n#2 Ping\n")
            f.flush()
            replies = [f.readline().rstrip("\n") for _ in expected]
    except OSError as e:
        replies = ["<{}>".format(e)]
    passed = replies == expected
    print("{:4} {:40} {}".format("ok" if passed else "FAIL", "KeepAlive, Ping, #2 Ping", replies))
    return 0 if passed else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("host")
    parser.add_argument("--port", type=int, default=1069)
    parser.add_argument("--tcp", action="store_true", help="use TCP instead of UDP")
    parser.add_argument("--timeout", type=float, default=5.0)
    parser.add_argument("--detectors", default="H1,AX,CDD",
                        help="comma separated detector names (default: Helix SFT)")
    args = parser.parse_args()

    send = send_tcp if args.tcp else send_udp
    detectors = [d.strip() for d in args.detectors.split(",") if d.strip()]

    failures = 0
    for cmd, check, expected in build_checks(detectors):
        try:
            reply = send(args.host, args.port, cmd, args.timeout)
            passed = check(reply)
        except (OSError, socket.timeout) as e:
            reply, passed = "<{}>".format(e), False
        if not passed:
            failures += 1
        print("{:4} {:40} {:.80}{}".format("ok" if passed else "FAIL", cmd, reply,
                                          "" if passed else "   (expected {})".format(expected)))

    if args.tcp:
        failures += check_keepalive(args.host, args.port, args.timeout)

    print("\n{} failed".format(failures) if failures else "\nall passed")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
