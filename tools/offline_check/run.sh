#!/bin/sh
# Compile RemoteControlServer.cs against stand-ins for the Thermo types, check that
# PROTOCOL.md and protocol.json match the command definitions, and run the tests.
# Needs the .NET SDK (dotnet). Does not need Qtegra.
#
#   run.sh                     check and test
#   run.sh --update-protocol   regenerate PROTOCOL.md and protocol.json, then check and test
set -e

here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
dotnet=${DOTNET:-$(command -v dotnet || echo /usr/local/share/dotnet/dotnet)}
dotnet_root=$(dirname "$(readlink -f "$dotnet" 2>/dev/null || echo "$dotnet")")

csc=$(ls -d "$dotnet_root"/sdk/*/Roslyn/bincore/csc.dll | sort -V | tail -1)
refs=$(ls -d "$dotnet_root"/packs/Microsoft.NETCore.App.Ref/*/ref/net*.0 | sort -V | tail -1)
tfm=$(basename "$refs")

build=$(mktemp -d)
trap 'rm -rf "$build"' EXIT

# Qtegra compiles scripts with System and System.Threading in scope and provides
# the instrument globals (HelixSFT etc.), which Stubs.cs declares on a partial class.
# -langversion:4 keeps the script to C# 4, an assumption about the compiler Qtegra uses.
{
    echo "using System; using System.Threading;"
    sed 's/^class RemoteControl[[:space:]]*$/partial class RemoteControl/' "$root/RemoteControlServer.cs"
} > "$build/Script.cs"

ref_args=""
for f in "$refs"/*.dll; do ref_args="$ref_args -r:$f"; done

export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# shellcheck disable=SC2086
"$dotnet" "$csc" -nologo -t:exe -main:DispatchTest -warnaserror -langversion:4 \
    -nowarn:CS0067,CS0169,CS0414,CS0649,SYSLIB0006 \
    -out:"$build/check.dll" $ref_args \
    "$here/Stubs.cs" "$build/Script.cs" "$here/DispatchTest.cs"
echo "compiled"

printf '{"runtimeOptions":{"tfm":"%s","rollForward":"LatestMajor","framework":{"name":"Microsoft.NETCore.App","version":"%s"}}}' \
    "$tfm" "${tfm#net}" > "$build/check.runtimeconfig.json"
"$dotnet" "$build/check.dll" --protocol "$here/protocol_header.md" "$build/PROTOCOL.md" "$build/protocol.json"
if command -v python3 >/dev/null; then
    python3 -m json.tool "$build/protocol.json" > /dev/null
fi
for doc in PROTOCOL.md protocol.json; do
    if [ "$1" = "--update-protocol" ]; then
        cp "$build/$doc" "$root/$doc"
        echo "updated $doc"
    elif ! cmp -s "$build/$doc" "$root/$doc"; then
        diff -u "$root/$doc" "$build/$doc" | head -40 || true
        echo "$doc is out of date: run tools/offline_check/run.sh --update-protocol"
        exit 1
    fi
done

"$dotnet" "$build/check.dll"
