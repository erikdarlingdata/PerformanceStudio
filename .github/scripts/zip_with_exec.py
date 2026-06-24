#!/usr/bin/env python3
"""
Create a .zip whose entries carry Unix permission bits.

Why this exists
---------------
The release/ nightly workflows run on windows-latest, where PowerShell's
Compress-Archive cannot store Unix file modes. The .NET ZipArchive API can set
external attributes but hardcodes the archive's "host system" byte to Windows
(0), so unzip / macOS / Linux ignore the mode and the self-contained .NET
apphost (PlanViewer.App) extracts WITHOUT its execute bit -- the app then fails
to launch on Linux/macOS ("permission denied").

Python's zipfile lets us set create_system = 3 (Unix) explicitly, so extractors
honor the mode. Files default to 0644, directories to 0755, and any path passed
via --exec / --exec-optional is marked 0755 (rwxr-xr-x).

Usage
-----
  python zip_with_exec.py SOURCE_DIR DEST_ZIP
      [--exec RELPATH ...] [--exec-optional RELPATH ...]

  SOURCE_DIR        directory whose *contents* are zipped (the directory itself
                    is not stored as a top-level entry -- matches the behavior of
                    `Compress-Archive -Path SOURCE_DIR/*`)
  DEST_ZIP          output .zip path (overwritten if present)
  --exec            forward-slash relpath that MUST exist; marked executable.
                    A missing --exec path is a hard error so a renamed/absent
                    apphost fails the release loudly instead of shipping a zip
                    that won't launch.
  --exec-optional   relpath marked executable if present, skipped if absent
                    (e.g. createdump, which only some runtime IDs include).
"""
import argparse
import os
import shutil
import stat
import sys
import zipfile

UNIX = 3  # ZIP "version made by" host system: 3 = Unix (so the high word of
          # external_attr is read as st_mode by unzip / macOS / Linux).


def main():
    ap = argparse.ArgumentParser(description="Zip a directory preserving Unix exec bits.")
    ap.add_argument("source_dir")
    ap.add_argument("dest_zip")
    ap.add_argument("--exec", action="append", default=[], dest="execs",
                    metavar="RELPATH", help="path that must exist; marked 0755")
    ap.add_argument("--exec-optional", action="append", default=[], dest="execs_optional",
                    metavar="RELPATH", help="path marked 0755 if present")
    args = ap.parse_args()

    source = os.path.abspath(args.source_dir)
    if not os.path.isdir(source):
        sys.exit(f"error: source dir not found: {source}")

    required = {p.replace("\\", "/").strip("/") for p in args.execs}
    optional = {p.replace("\\", "/").strip("/") for p in args.execs_optional}

    # Walk first so we can validate that every required exec path is present
    # before we start writing the archive.
    dir_entries = []   # arcname with trailing slash
    file_entries = []  # (abs_path, arcname)
    present = set()
    for root, dirs, files in os.walk(source):
        dirs.sort()
        files.sort()
        rel_root = os.path.relpath(root, source)
        if rel_root != ".":
            dir_entries.append(rel_root.replace("\\", "/") + "/")
        for fn in files:
            abs_path = os.path.join(root, fn)
            arc = os.path.relpath(abs_path, source).replace("\\", "/")
            file_entries.append((abs_path, arc))
            present.add(arc)

    missing = sorted(required - present)
    if missing:
        sys.exit("error: required --exec path(s) not found under {}: {}".format(
            source, ", ".join(missing)))

    exec_paths = set(required) | (set(optional) & present)

    dest = os.path.abspath(args.dest_zip)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    if os.path.exists(dest):
        os.remove(dest)

    marked = []
    with zipfile.ZipFile(dest, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for dir_arc in dir_entries:
            zi = zipfile.ZipInfo(dir_arc)
            zi.create_system = UNIX
            # S_IFDIR | 0755 in the Unix high word; 0x10 = FILE_ATTRIBUTE_DIRECTORY
            # in the DOS low word so Windows tools also see it as a directory.
            zi.external_attr = ((stat.S_IFDIR | 0o755) << 16) | 0x10
            zf.writestr(zi, b"")

        for abs_path, arc in file_entries:
            zi = zipfile.ZipInfo.from_file(abs_path, arc)  # carries file mtime
            zi.create_system = UNIX
            zi.compress_type = zipfile.ZIP_DEFLATED
            if arc in exec_paths:
                zi.external_attr = (stat.S_IFREG | 0o755) << 16
                marked.append(arc)
            else:
                zi.external_attr = (stat.S_IFREG | 0o644) << 16
            with open(abs_path, "rb") as src, zf.open(zi, "w") as dst:
                shutil.copyfileobj(src, dst)

    print(f"Created {dest}")
    print(f"  {len(file_entries)} file(s), {len(dir_entries)} dir(s)")
    print(f"  executable: {', '.join(sorted(marked)) if marked else '(none)'}")


if __name__ == "__main__":
    main()
