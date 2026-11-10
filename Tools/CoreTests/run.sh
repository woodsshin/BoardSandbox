#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
lib_dir="${BSE_NATIVE_DIR:-$root/Native/build/host}"
export LD_LIBRARY_PATH="$lib_dir:${LD_LIBRARY_PATH:-}"
export BSE_MODS_DIR="${BSE_MODS_DIR:-$root/Assets/StreamingAssets/Mods}"
exec dotnet run --project "$here/CoreTests.csproj" -c Release -- "$@"
