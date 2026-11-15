#!/usr/bin/env python3
"""Checks that a built libbse_luau.so is a library the Unity Android player can load, without running it.

    python3 Native/scripts/check_android_lib.py Assets/BoardSandbox/Plugins/Android/arm64-v8a/libbse_luau.so
    python3 Native/scripts/check_android_lib.py --abi x86_64 build/libbse_luau.so

The ABI is taken from the folder name (arm64-v8a, armeabi-v7a, x86_64) unless --abi is given. The script reads the
ELF file itself, so it needs neither the NDK nor readelf. It fails when the library
  - was built for another CPU than its folder says,
  - needs a library the player does not have (above all libc++_shared.so: the C++ runtime has to be inside),
  - does not export exactly the bse_* functions of include/bse_luau.h,
  - is a 64-bit library whose segments are not aligned to 16 KB (devices with 16 KB pages refuse it),
  - contains text relocations, which Android no longer loads, or
  - uses a feature that the oldest Android of the project (AndroidMinSdkVersion in ProjectSettings) does not have:
    a thread-local storage segment before API 29, or relative relocations (RELR) before API 28 and 30.
"""

import argparse
import os
import re
import struct
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
HEADER = os.path.join(ROOT, "Native", "bse_luau", "include", "bse_luau.h")

# ABI -> ELF machine, word size, name of the CPU
ABIS = {
    "arm64-v8a": (183, 64, "AArch64"),
    "armeabi-v7a": (40, 32, "ARM"),
    "x86_64": (62, 64, "x86-64"),
}
MACHINE_NAMES = {3: "x86", 8: "MIPS", 40: "ARM", 62: "x86-64", 183: "AArch64", 243: "RISC-V"}

# What the Android system provides to every app. Anything else a library needs has to be inside the app.
SYSTEM_LIBRARIES = ("libc.so", "libm.so", "libdl.so", "liblog.so")
PAGE_SIZE = 16384
DEFAULT_MIN_API = 25

ET_DYN = 3
PT_LOAD = 1
PT_TLS = 7
SHT_DYNAMIC = 6
SHT_DYNSYM = 11
DT_NEEDED = 1
DT_SONAME = 14
DT_TEXTREL = 22
DT_FLAGS = 30
DF_TEXTREL = 4
DT_RELR = 36
DT_ANDROID_RELR = 0x6fffe000


class ElfError(Exception):
    pass


class Elf(object):
    def __init__(self, data):
        if data[:4] != b"\x7fELF":
            raise ElfError("not an ELF file")
        if data[4] not in (1, 2):
            raise ElfError("unknown ELF class %d" % data[4])
        if data[5] != 1:
            raise ElfError("not little-endian")
        self.data = data
        self.bits = 64 if data[4] == 2 else 32
        try:
            if self.bits == 64:
                fields = struct.unpack_from("<HHIQQQIHHHHHH", data, 16)
            else:
                fields = struct.unpack_from("<HHIIIIIHHHHHH", data, 16)
        except struct.error:
            raise ElfError("truncated ELF header")
        (self.type, self.machine, _version, _entry, self.phoff, self.shoff, _flags, _ehsize,
         self.phentsize, self.phnum, self.shentsize, self.shnum, self.shstrndx) = fields
        self.segments = self._read_segments()
        self.sections = self._read_sections()

    def _unpack(self, fmt, offset):
        try:
            return struct.unpack_from(fmt, self.data, offset)
        except struct.error:
            raise ElfError("truncated ELF file")

    def _string(self, table_offset, index):
        start = table_offset + index
        end = self.data.find(b"\0", start)
        if end < 0:
            raise ElfError("unterminated string")
        return self.data[start:end].decode("utf-8", "replace")

    def _read_segments(self):
        result = []
        for i in range(self.phnum):
            offset = self.phoff + i * self.phentsize
            if self.bits == 64:
                p_type, _flags, p_offset, p_vaddr, _paddr, _filesz, _memsz, align = self._unpack("<IIQQQQQQ", offset)
            else:
                p_type, p_offset, p_vaddr, _paddr, _filesz, _memsz, _flags, align = self._unpack("<IIIIIIII", offset)
            result.append({"type": p_type, "offset": p_offset, "vaddr": p_vaddr, "align": align})
        return result

    def _read_sections(self):
        raw = []
        for i in range(self.shnum):
            offset = self.shoff + i * self.shentsize
            if self.bits == 64:
                name, kind, _flags, _addr, file_offset, size, link, _info, _align, entsize = self._unpack("<IIQQQQIIQQ", offset)
            else:
                name, kind, _flags, _addr, file_offset, size, link, _info, _align, entsize = self._unpack("<IIIIIIIIII", offset)
            raw.append({"name_offset": name, "type": kind, "offset": file_offset, "size": size, "link": link, "entsize": entsize})
        if raw and self.shstrndx < len(raw):
            names = raw[self.shstrndx]["offset"]
            for section in raw:
                section["name"] = self._string(names, section["name_offset"])
        else:
            for section in raw:
                section["name"] = ""
        return raw

    def dynamic_entries(self):
        """The (tag, value) pairs of the dynamic section and the string table that its names point into."""
        for section in self.sections:
            if section["type"] != SHT_DYNAMIC:
                continue
            strings = self.sections[section["link"]]["offset"]
            size = 16 if self.bits == 64 else 8
            fmt = "<qQ" if self.bits == 64 else "<iI"
            entries = []
            for i in range(section["size"] // size):
                tag, value = self._unpack(fmt, section["offset"] + i * size)
                if tag == 0:
                    break
                entries.append((tag, value))
            return entries, strings
        return [], 0

    def needed(self):
        entries, strings = self.dynamic_entries()
        return [self._string(strings, value) for tag, value in entries if tag == DT_NEEDED]

    def soname(self):
        entries, strings = self.dynamic_entries()
        for tag, value in entries:
            if tag == DT_SONAME:
                return self._string(strings, value)
        return None

    def has_text_relocations(self):
        entries, _ = self.dynamic_entries()
        for tag, value in entries:
            if tag == DT_TEXTREL or (tag == DT_FLAGS and value & DF_TEXTREL):
                return True
        return False

    def exported_symbols(self):
        """The names of the symbols that the library defines for other libraries to use."""
        names = []
        for section in self.sections:
            if section["type"] != SHT_DYNSYM:
                continue
            strings = self.sections[section["link"]]["offset"]
            size = 24 if self.bits == 64 else 16
            for i in range(1, section["size"] // size):
                offset = section["offset"] + i * size
                if self.bits == 64:
                    name, info, other, index, _value, _size = self._unpack("<IBBHQQ", offset)
                else:
                    name, _value, _size, info, other, index = self._unpack("<IIIBBH", offset)
                binding = info >> 4
                if index != 0 and binding in (1, 2) and (other & 3) == 0:
                    names.append(self._string(strings, name))
        return names

    def has_tag(self, wanted):
        entries, _ = self.dynamic_entries()
        return any(tag == wanted for tag, _ in entries)

    def has_section(self, name):
        return any(section["name"] == name for section in self.sections)


def project_min_api():
    """The lowest Android version the Unity project supports (AndroidMinSdkVersion in ProjectSettings)."""
    try:
        with open(os.path.join(ROOT, "ProjectSettings", "ProjectSettings.asset"), "r", encoding="utf-8") as handle:
            match = re.search(r"^\s*AndroidMinSdkVersion:\s*(\d+)", handle.read(), re.MULTILINE)
    except OSError:
        return DEFAULT_MIN_API
    level = int(match.group(1)) if match else 0
    return level if level >= 21 else DEFAULT_MIN_API


def declared_functions():
    """The functions include/bse_luau.h declares with BSE_API: the ones the library has to export."""
    with open(HEADER, "r", encoding="utf-8") as handle:
        text = handle.read()
    return set(re.findall(r"^BSE_API\b[^;(]*?\b(bse_[A-Za-z0-9_]+)\s*\(", text, re.MULTILINE))


def check(path, abi, system_libraries=SYSTEM_LIBRARIES, min_api=None):
    """Returns (errors, warnings, details) for the library at path, which is meant for the given ABI."""
    errors, warnings, details = [], [], []
    if min_api is None:
        min_api = project_min_api()
    machine, bits, cpu = ABIS[abi]
    with open(path, "rb") as handle:
        data = handle.read()
    try:
        elf = Elf(data)
    except ElfError as e:
        return ["%s: %s" % (path, e)], warnings, details

    details.append("%d bytes, %d-bit %s, %s" % (
        len(data), elf.bits, MACHINE_NAMES.get(elf.machine, "machine %d" % elf.machine),
        "shared object" if elf.type == ET_DYN else "not a shared object"))
    if elf.type != ET_DYN:
        errors.append("it is not a shared object")
    if elf.machine != machine or elf.bits != bits:
        errors.append("it is built for %d-bit %s, but a library for %s has to be %d-bit %s" % (
            elf.bits, MACHINE_NAMES.get(elf.machine, "machine %d" % elf.machine), abi, bits, cpu))

    needed = elf.needed()
    details.append("needs " + (", ".join(needed) if needed else "nothing"))
    for name in needed:
        if name in system_libraries:
            continue
        hint = ""
        if name in ("libc++_shared.so", "libstdc++.so"):
            hint = " (the C++ runtime has to be linked into the library: build with ANDROID_STL=c++_static)"
        errors.append("it needs %s, which the Unity player does not provide%s" % (name, hint))

    exported = set(elf.exported_symbols())
    expected = declared_functions()
    details.append("exports %d symbols" % len(exported))
    if not expected:
        errors.append("no BSE_API functions were found in " + HEADER)
    missing = sorted(expected - exported)
    extra = sorted(exported - expected)
    if missing:
        errors.append("it does not export " + ", ".join(missing))
    if extra:
        errors.append("it exports more than the bse_* functions: " + ", ".join(extra[:8]) + (" ..." if len(extra) > 8 else ""))

    if bits == 64:
        for segment in elf.segments:
            if segment["type"] != PT_LOAD:
                continue
            if segment["align"] % PAGE_SIZE != 0 or segment["align"] < PAGE_SIZE:
                errors.append("a LOAD segment is aligned to %d bytes; 64-bit libraries need %d so that devices with 16 KB pages can load them"
                              % (segment["align"], PAGE_SIZE))
                break
            if segment["offset"] % PAGE_SIZE != segment["vaddr"] % PAGE_SIZE:
                errors.append("a LOAD segment's file offset and address differ modulo %d" % PAGE_SIZE)
                break

    if elf.has_text_relocations():
        errors.append("it has text relocations, which Android does not load")
    if min_api < 29 and any(segment["type"] == PT_TLS for segment in elf.segments):
        errors.append("it has a thread-local storage segment, which Android before 10 (API 29) cannot load; the project supports API %d" % min_api)
    if min_api < 28 and elf.has_tag(DT_ANDROID_RELR):
        errors.append("it has packed relative relocations (DT_ANDROID_RELR), which need API 28; the project supports API %d" % min_api)
    if min_api < 30 and elf.has_tag(DT_RELR):
        errors.append("it has relative relocations (DT_RELR), which need API 30; the project supports API %d" % min_api)

    soname = elf.soname()
    if soname != "libbse_luau.so":
        warnings.append("its soname is %s, not libbse_luau.so" % (soname if soname else "missing"))
    if not elf.has_section(".note.android.ident"):
        warnings.append("it has no .note.android.ident, so it does not look like a build with the Android NDK")
    return errors, warnings, details


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("libraries", nargs="+", help="libbse_luau.so files")
    parser.add_argument("--abi", choices=sorted(ABIS), help="the ABI the libraries are for; the name of their folder when omitted")
    parser.add_argument("--min-api", type=int, help="the oldest Android API level to support; ProjectSettings' minimum when omitted")
    options = parser.parse_args()

    failed = False
    for path in options.libraries:
        abi = options.abi or os.path.basename(os.path.dirname(os.path.abspath(path)))
        if abi not in ABIS:
            print("%s: the folder '%s' is not an ABI (%s); use --abi" % (path, abi, ", ".join(sorted(ABIS))))
            failed = True
            continue
        errors, warnings, details = check(path, abi, min_api=options.min_api)
        print("%s (%s)" % (path, abi))
        for line in details:
            print("  " + line)
        for line in warnings:
            print("  warning: " + line)
        for line in errors:
            print("  ERROR: " + line)
        if not errors:
            print("  ok")
        failed = failed or bool(errors)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
