"""Regenerate (or check) the C# protocol bindings from the OyiProtocol submodule.

The generated files are committed so a plain `dotnet build` needs only the .NET SDK. Run this after
moving the submodule to a new tag; CI runs it with --check to catch drift.

The required flatc version is read from the submodule's scripts/verify.py (FLATC_VERSION), so the
protocol repository stays the single source of truth for it.
"""

from __future__ import annotations

import argparse
import filecmp
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PROTOCOL = ROOT / "protocol"
SCHEMA = PROTOCOL / "schema" / "oyi_protocol.fbs"
VERIFY_SCRIPT = PROTOCOL / "scripts" / "verify.py"
GENERATED = ROOT / "src" / "OyiEditor.Protocol.Schema" / "Generated"


def required_flatc_version() -> str:
    match = re.search(r'^FLATC_VERSION = "([^"]+)"', VERIFY_SCRIPT.read_text(encoding="utf-8"), re.MULTILINE)
    if match is None:
        sys.exit(f"error: no FLATC_VERSION in {VERIFY_SCRIPT}")
    return match.group(1)


def check_flatc(flatc: str, version: str) -> None:
    try:
        out = subprocess.run([flatc, "--version"], check=True, capture_output=True, text=True).stdout.strip()
    except (OSError, subprocess.CalledProcessError) as error:
        sys.exit(f"error: cannot run flatc ({flatc}): {error}")
    if out != f"flatc version {version}":
        sys.exit(f"error: expected flatc {version}, got '{out}' from {flatc}")


def generate(flatc: str, out: Path) -> None:
    subprocess.run([flatc, "--csharp", "-o", str(out), str(SCHEMA)], check=True)
    # flatc writes the platform's line endings; the repository stores LF.
    for path in out.rglob("*.cs"):
        data = path.read_bytes()
        path.write_bytes(data.replace(b"\r\n", b"\n"))


def same_tree(left: Path, right: Path) -> bool:
    comparison = filecmp.dircmp(left, right)
    if comparison.left_only or comparison.right_only or comparison.funny_files:
        return False
    _, mismatch, errors = filecmp.cmpfiles(left, right, comparison.common_files, shallow=False)
    if mismatch or errors:
        return False
    return all(same_tree(left / sub, right / sub) for sub in comparison.common_dirs)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--flatc", default=os.environ.get("FLATC") or shutil.which("flatc") or "flatc")
    parser.add_argument("--check", action="store_true", help="fail if the committed bindings are out of date")
    args = parser.parse_args()

    if not SCHEMA.is_file():
        sys.exit(f"error: {SCHEMA} is missing; run `git submodule update --init`")
    check_flatc(args.flatc, required_flatc_version())

    if args.check:
        with tempfile.TemporaryDirectory() as temp:
            fresh = Path(temp)
            generate(args.flatc, fresh)
            if not GENERATED.is_dir() or not same_tree(fresh, GENERATED):
                sys.exit("error: generated bindings are stale; run python scripts/generate_protocol.py and commit")
        print("ok: generated bindings match the schema")
        return

    if GENERATED.exists():
        shutil.rmtree(GENERATED)
    generate(args.flatc, GENERATED)
    print(f"generated {sum(1 for _ in GENERATED.rglob('*.cs'))} files in {GENERATED.relative_to(ROOT).as_posix()}")


if __name__ == "__main__":
    main()
