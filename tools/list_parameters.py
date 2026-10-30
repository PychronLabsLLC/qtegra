#!/usr/bin/env python3
"""List candidate hardware parameter names from a Qtegra hardware database (.imhwd) file.

The file is on the instrument PC, usually
    C:\\ProgramData\\Thermo\\Qtegra\\_Application Data\\PluginData\\<instrument>\\<instrument>.imhwd

    python3 tools/list_parameters.py HelixSFT.imhwd > parameters.txt
    python3 tools/smoke_test.py <host> --parameters parameters.txt

The .imhwd format is not documented here, so this does not parse it: it collects the
strings in the file (XML text and attributes when it is XML, otherwise ASCII and UTF-16
strings) that look like item names, e.g. "Field Set" or "Ion Gauge MS Readback".
By default only names ending in a usual item suffix are listed; --all lists every
name-like string. Check the result against a running server with smoke_test.py.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET

SUFFIXES = ("Set", "Readback", "Script", "Status", "Enabled", "Monitor")
NAME = re.compile(r"^[A-Za-z][A-Za-z0-9 ()/._+-]{1,62}[A-Za-z0-9)]$")


def xml_strings(data):
    root = ET.fromstring(data)
    for element in root.iter():
        if element.text and element.text.strip():
            yield element.text.strip()
        for value in element.attrib.values():
            yield value.strip()


def binary_strings(data):
    for m in re.finditer(rb"[\x20-\x7e]{3,}", data):
        yield m.group().decode("ascii")
    for m in re.finditer(rb"(?:[\x20-\x7e]\x00){3,}", data):
        yield m.group().decode("utf-16-le")


def candidates(data, all_names):
    try:
        strings = list(xml_strings(data))
    except ET.ParseError:
        strings = list(binary_strings(data))
    names = set()
    for s in strings:
        s = " ".join(s.split())
        if not NAME.match(s):
            continue
        if all_names or s.split()[-1] in SUFFIXES:
            names.add(s)
    return sorted(names)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("file", help=".imhwd hardware database file")
    parser.add_argument("--all", action="store_true", help="list every name-like string, not only *Set, *Readback, ...")
    args = parser.parse_args()

    with open(args.file, "rb") as f:
        data = f.read()
    names = candidates(data, args.all)
    for name in names:
        print(name)
    print("{} names".format(len(names)), file=sys.stderr)
    return 0 if names else 1


if __name__ == "__main__":
    sys.exit(main())
