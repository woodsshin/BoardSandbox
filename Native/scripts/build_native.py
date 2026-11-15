#!/usr/bin/env python3
"""Builds the native bse_luau library and installs it into the Unity project.

    python3 Native/scripts/build_native.py windows-x64 --run-tests    Windows DLL, cross build with zig (Linux or macOS; the tests need wine)
    python Native/scripts/build_native.py host --run-tests            local compiler, for example Visual Studio on Windows
    python Native/scripts/build_native.py android                     libbse_luau.so for arm64-v8a, armeabi-v7a and x86_64 (Android NDK)
    python Native/scripts/build_native.py android-arm64               one ABI only: android-arm64, android-arm32 or android-x64

host installs the library only when this machine is Windows; anywhere else it is a test build.

The Android build needs the Android NDK, CMake and Ninja (pip install cmake ninja). The NDK is taken from --ndk, from
ANDROID_NDK_HOME (or ANDROID_NDK_ROOT), from the NDK that Unity installed with its Android Build Support for the editor
version in ProjectSettings/ProjectVersion.txt, or from the Android SDK, in this order. Every library is checked by
check_android_lib.py and then installed into Assets/BoardSandbox/Plugins/Android/<abi>/. The tests of the library do not
run for Android; run them with the host target.
"""

import argparse
import glob
import os
import re
import shlex
import shutil
import stat
import subprocess
import sys

import check_android_lib

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE = os.path.join(ROOT, "Native", "bse_luau")
PLUGIN = os.path.join(ROOT, "Assets", "BoardSandbox", "Plugins", "Windows", "x86_64", "bse_luau.dll")
ANDROID_PLUGINS = os.path.join(ROOT, "Assets", "BoardSandbox", "Plugins", "Android")
ANDROID_LIBRARY = "libbse_luau.so"
BUILD_ROOT = os.path.join(ROOT, "Native", "build")

ZIG_TARGET = "x86_64-windows-gnu"
WINE_LOCATIONS = ("/usr/lib/wine/wine64", "/usr/lib64/wine/wine64", "/usr/lib/wine64/wine64", "/opt/wine-stable/bin/wine64")

# target -> Android ABI, which is also the name of the folder the library goes into
ANDROID_TARGETS = {
    "android-arm64": "arm64-v8a",
    "android-arm32": "armeabi-v7a",
    "android-x64": "x86_64",
}
NDK_VARIABLES = ("ANDROID_NDK_HOME", "ANDROID_NDK_ROOT", "ANDROID_NDK", "NDK_ROOT")


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


def version_key(name):
    return tuple(int(part) if part.isdigit() else 0 for part in name.split("."))


def unity_editor_version():
    try:
        with open(os.path.join(ROOT, "ProjectSettings", "ProjectVersion.txt"), encoding="utf-8") as handle:
            match = re.search(r"^m_EditorVersion:\s*(\S+)", handle.read(), re.MULTILINE)
    except OSError:
        return None
    return match.group(1) if match else None


def unity_ndks():
    """Where Unity Hub puts the NDK of the editor version this project uses."""
    version = unity_editor_version()
    if not version:
        return []
    if sys.platform == "win32":
        bases = [os.environ.get("ProgramFiles"), os.environ.get("ProgramW6432"), "C:\\Program Files"]
        return [os.path.join(base, "Unity", "Hub", "Editor", version, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "NDK")
                for base in bases if base]
    if sys.platform == "darwin":
        return [os.path.join("/Applications/Unity/Hub/Editor", version, "PlaybackEngines", "AndroidPlayer", "NDK")]
    return [os.path.expanduser(os.path.join("~/Unity/Hub/Editor", version, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "NDK"))]


def sdk_ndks():
    homes = [os.environ.get("ANDROID_HOME"), os.environ.get("ANDROID_SDK_ROOT")]
    if sys.platform == "win32":
        homes.append(os.path.join(os.environ.get("LOCALAPPDATA", ""), "Android", "Sdk"))
    elif sys.platform == "darwin":
        homes.append(os.path.expanduser("~/Library/Android/sdk"))
    else:
        homes.append(os.path.expanduser("~/Android/Sdk"))
    found = []
    for home in homes:
        if not home:
            continue
        side_by_side = os.path.join(home, "ndk")
        if os.path.isdir(side_by_side):
            found += [os.path.join(side_by_side, name) for name in sorted(os.listdir(side_by_side), key=version_key, reverse=True)]
        found.append(os.path.join(home, "ndk-bundle"))
    return found


def valid_ndk(path):
    return bool(path) and os.path.isfile(os.path.join(path, "build", "cmake", "android.toolchain.cmake"))


def ndk_revision(ndk):
    try:
        with open(os.path.join(ndk, "source.properties"), encoding="utf-8") as handle:
            match = re.search(r"^Pkg\.Revision\s*=\s*(\S+)", handle.read(), re.MULTILINE)
    except OSError:
        return "unknown"
    return match.group(1) if match else "unknown"


def find_ndk(explicit):
    if explicit:
        if not valid_ndk(explicit):
            sys.exit("--ndk %s is not an Android NDK (build/cmake/android.toolchain.cmake is missing)." % explicit)
        return os.path.abspath(explicit)
    candidates = [os.environ.get(name) for name in NDK_VARIABLES] + unity_ndks() + sdk_ndks()
    for candidate in candidates:
        if valid_ndk(candidate):
            return os.path.abspath(candidate)
    sys.exit("The Android NDK was not found. Pass --ndk <folder> or set ANDROID_NDK_HOME. Unity's own NDK is in\n"
             "  <Unity Hub>/Editor/%s/Editor/Data/PlaybackEngines/AndroidPlayer/NDK\n"
             "when the editor was installed with Android Build Support." % (unity_editor_version() or "<version>"))


def android_api():
    """The lowest Android version the Unity project supports; the library is built against its headers."""
    return check_android_lib.project_min_api()


def require_tool(command, install):
    found = shutil.which(command)
    if not found:
        sys.exit("%s was not found. Install it with: %s" % (command, install))
    return found


def build_android(target, ndk, luau_src, api):
    abi = ANDROID_TARGETS[target]
    ninja = require_tool("ninja", "pip install ninja")
    require_tool("cmake", "pip install cmake")
    build_dir = os.path.join(BUILD_ROOT, target)
    toolchain = os.path.join(ndk, "build", "cmake", "android.toolchain.cmake")
    arguments = [
        "-G", "Ninja",
        "-DCMAKE_MAKE_PROGRAM=" + ninja.replace("\\", "/"),
        "-DCMAKE_TOOLCHAIN_FILE=" + toolchain.replace("\\", "/"),
        "-DANDROID_ABI=" + abi,
        "-DANDROID_PLATFORM=android-%d" % api,
        "-DANDROID_STL=c++_static",
        "-DANDROID_SUPPORT_FLEXIBLE_PAGE_SIZES=ON",
    ]
    configure(build_dir, arguments, luau_src)
    build(build_dir, False)
    artifact = find_artifact(build_dir, ANDROID_LIBRARY)
    check_android_library(artifact, abi)
    return build_dir, artifact


def check_android_library(artifact, abi):
    errors, warnings, details = check_android_lib.check(artifact, abi)
    print("checked %s for %s: %s" % (ANDROID_LIBRARY, abi, "; ".join(details)), flush=True)
    for line in warnings:
        print("warning: " + line)
    if errors:
        sys.exit("The %s library cannot be used:\n  %s" % (abi, "\n  ".join(errors)))


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


def install_android(source, abi):
    destination = os.path.join(ANDROID_PLUGINS, abi, ANDROID_LIBRARY)
    os.makedirs(os.path.dirname(destination), exist_ok=True)
    shutil.copyfile(source, destination)
    print("installed " + os.path.relpath(destination, ROOT))


def expand(targets):
    result = []
    for target in targets:
        result += list(ANDROID_TARGETS) if target == "android" else [target]
    return list(dict.fromkeys(result))


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("targets", nargs="+", choices=["windows-x64", "host", "android"] + list(ANDROID_TARGETS))
    parser.add_argument("--luau-src", help="an existing Luau checkout; it is downloaded from GitHub when omitted")
    parser.add_argument("--run-tests", action="store_true", help="also build and run the native tests (not for the Android targets)")
    parser.add_argument("--no-install", action="store_true", help="leave the Unity project untouched")
    parser.add_argument("--ndk", help="the Android NDK folder for the android targets; see above for where it is looked for otherwise")
    parser.add_argument("--android-api", type=int, help="the Android API level to build against; ProjectSettings' minimum when omitted")
    options = parser.parse_args()

    ndk = None
    for target in expand(options.targets):
        android = target in ANDROID_TARGETS
        if android:
            if ndk is None:
                ndk = find_ndk(options.ndk)
                print("Android NDK %s in %s" % (ndk_revision(ndk), ndk), flush=True)
            api = options.android_api or android_api()
            build_dir, artifact = build_android(target, ndk, options.luau_src, api)
        elif target == "host":
            build_dir, artifact = build_host(options.luau_src, options.run_tests)
        else:
            build_dir, artifact = build_windows_zig(options.luau_src, options.run_tests)
        if options.run_tests:
            if android:
                print("the tests do not run for Android; they were skipped for " + target)
            else:
                run_tests(build_dir, target)
        if options.no_install:
            continue
        if android:
            install_android(artifact, ANDROID_TARGETS[target])
        elif target == "windows-x64" or os.name == "nt":
            install(artifact)


if __name__ == "__main__":
    main()
