#!/usr/bin/env python3
"""Builds the native bse_luau library for Windows x64 and installs it into the Unity project.

    python3 Native/scripts/build_native.py windows-x64 --run-tests    cross build with zig (Linux or macOS; the tests need wine)
    python Native/scripts/build_native.py host --run-tests            local compiler, for example Visual Studio on Windows

host installs the library only when this machine is Windows; anywhere else it is a test build.
"""

import argparse
import glob
import os
import shlex
import shutil
import stat
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(ROOT, "Native", "bse_luau")
PLUGIN = os.path.join(ROOT, "Assets", "BoardSandbox", "Plugins", "Windows", "x86_64", "bse_luau.dll")
BUILD_ROOT = os.path.join(ROOT, "Native", "build")

ZIG_TARGET = "x86_64-windows-gnu"
WINE_LOCATIONS = ("/usr/lib/wine/wine64", "/usr/lib64/wine/wine64", "/usr/lib/wine64/wine64", "/opt/wine-stable/bin/wine64")


def run(command, **kwargs):
    print("+ " + " ".join(shlex.quote(part) for part in command), flush=True)
    subprocess.run(command, check=True, **kwargs)


def zig_command():
    found = shutil.which("zig")
    if found:
        return [found]
    try:
        import ziglang  # noqa: F401
    except ImportError:
        sys.exit("zig was not found. Install it from https://ziglang.org or run: pip install ziglang")
    return [sys.executable, "-m", "ziglang"]


def write_wrapper(directory, name, zig, subcommand, triple):
    path = os.path.join(directory, name)
    words = " ".join(shlex.quote(part) for part in zig)
    extra = " -target " + triple if triple else ""
    with open(path, "w", newline="\n") as handle:
        handle.write("#!/bin/sh\nexec %s %s%s \"$@\"\n" % (words, subcommand, extra))
    os.chmod(path, os.stat(path).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)
    return path


def configure(build_dir, extra_args, luau_src):
    command = ["cmake", "-S", SOURCE, "-B", build_dir, "-DCMAKE_BUILD_TYPE=Release"] + extra_args
    if luau_src:
        command.append("-DLUAU_SOURCE_DIR=" + os.path.abspath(luau_src))
    run(command)


def build(build_dir, with_tests):
    targets = ["bse_luau"] + (["bse_luau_tests"] if with_tests else [])
    run(["cmake", "--build", build_dir, "--config", "Release", "--parallel", "--target"] + targets)


def find_artifact(build_dir, file_name):
    matches = glob.glob(os.path.join(build_dir, "**", file_name), recursive=True)
    matches = [path for path in matches if os.path.isfile(path) and os.sep + "luau" + os.sep not in path.replace(build_dir, "")]
    if not matches:
        sys.exit("The build did not produce " + file_name)
    return sorted(matches, key=len)[0]


def build_windows_zig(luau_src, with_tests):
    if os.name == "nt":
        sys.exit("windows-x64 is the zig cross build for other systems; on Windows build the host target.")
    zig = zig_command()
    build_dir = os.path.join(BUILD_ROOT, "windows-x64")
    toolchain = os.path.join(build_dir, "toolchain")
    os.makedirs(toolchain, exist_ok=True)

    arguments = [
        "-G", "Ninja",
        "-DCMAKE_C_COMPILER=" + write_wrapper(toolchain, "cc", zig, "cc", ZIG_TARGET),
        "-DCMAKE_CXX_COMPILER=" + write_wrapper(toolchain, "c++", zig, "c++", ZIG_TARGET),
        "-DCMAKE_AR=" + write_wrapper(toolchain, "ar", zig, "ar", None),
        "-DCMAKE_RANLIB=" + write_wrapper(toolchain, "ranlib", zig, "ranlib", None),
        "-DCMAKE_SYSTEM_NAME=Windows",
        "-DCMAKE_SYSTEM_PROCESSOR=AMD64",
        "-DCMAKE_SHARED_LINKER_FLAGS=-s",
    ]
    configure(build_dir, arguments, luau_src)
    build(build_dir, with_tests)
    return build_dir, find_artifact(build_dir, "bse_luau.dll")


def build_host(luau_src, with_tests):
    build_dir = os.path.join(BUILD_ROOT, "host")
    arguments = []
    if shutil.which("ninja") and os.name != "nt":
        arguments += ["-G", "Ninja"]
    configure(build_dir, arguments, luau_src)
    build(build_dir, with_tests)
    file_name = {"nt": "bse_luau.dll", "posix": "libbse_luau.so"}[os.name]
    if sys.platform == "darwin":
        file_name = "libbse_luau.dylib"
    return build_dir, find_artifact(build_dir, file_name)


def find_wine():
    for command in ("wine64", "wine"):
        found = shutil.which(command)
        if found:
            return found
    for path in WINE_LOCATIONS:
        if os.path.isfile(path) and os.access(path, os.X_OK):
            return path
    return None


def run_tests(build_dir, target):
    windows = target == "windows-x64" or os.name == "nt"
    executable = find_artifact(build_dir, "bse_luau_tests" + (".exe" if windows else ""))
    command = [executable]
    if target == "windows-x64":
        wine = find_wine()
        if not wine:
            print("wine was not found; the Windows tests were built but not run")
            return
        command = [wine, executable]
    run(command, env=dict(os.environ, WINEDEBUG="-all"))


def install(source):
    os.makedirs(os.path.dirname(PLUGIN), exist_ok=True)
    shutil.copyfile(source, PLUGIN)
    print("installed " + os.path.relpath(PLUGIN, ROOT))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("targets", nargs="+", choices=["windows-x64", "host"])
    parser.add_argument("--luau-src", help="an existing Luau checkout; it is downloaded from GitHub when omitted")
    parser.add_argument("--run-tests", action="store_true", help="also build and run the native tests")
    parser.add_argument("--no-install", action="store_true", help="leave the Unity project untouched")
    options = parser.parse_args()

    for target in dict.fromkeys(options.targets):
        if target == "host":
            build_dir, artifact = build_host(options.luau_src, options.run_tests)
        else:
            build_dir, artifact = build_windows_zig(options.luau_src, options.run_tests)
        if options.run_tests:
            run_tests(build_dir, target)
        if not options.no_install and (target == "windows-x64" or os.name == "nt"):
            install(artifact)


if __name__ == "__main__":
    main()
