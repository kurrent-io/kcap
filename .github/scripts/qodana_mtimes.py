"""Keeps tracked files' timestamps stable across checkouts for Qodana's cache.

A checkout stamps every file with the current time, so the cached analysis sees every file as changed.
`record` writes each tracked file's blob and timestamp to the manifest; `restore` gives every file whose
blob still matches its recorded timestamp back. Any other file keeps the checkout's time, which is
always newer than anything the cache saw, whichever way the checkout moved.

Usage: qodana_mtimes.py record|restore <manifest>
"""
import os
import subprocess
import sys


def tracked_files():
    out = subprocess.run(["git", "ls-files", "-s", "-z"], check=True, capture_output=True).stdout
    for entry in out.decode().split("\0"):
        if not entry:
            continue
        meta, path = entry.split("\t", 1)
        mode, blob, _ = meta.split()
        # Symlinks and submodules carry no content of their own to analyse
        if mode in ("120000", "160000"):
            continue
        yield path, blob


def record(manifest):
    with open(manifest, "w") as f:
        for path, blob in tracked_files():
            f.write(f"{blob}\t{os.stat(path).st_mtime_ns}\t{path}\n")


def restore(manifest):
    if not os.path.exists(manifest):
        print("No manifest: every file keeps its checkout time")
        return
    recorded = {}
    with open(manifest) as f:
        for line in f:
            blob, mtime, path = line.rstrip("\n").split("\t", 2)
            recorded[path] = (blob, int(mtime))
    kept, changed = 0, 0
    for path, blob in tracked_files():
        entry = recorded.get(path)
        if entry and entry[0] == blob:
            os.utime(path, ns=(entry[1], entry[1]))
            kept += 1
        else:
            changed += 1
    print(f"Restored {kept} timestamps; {changed} files changed since the cache was saved")


if __name__ == "__main__":
    {"record": record, "restore": restore}[sys.argv[1]](sys.argv[2])
