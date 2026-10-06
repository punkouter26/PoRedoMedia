"""Zips a publish folder for App Service on Linux, keeping the execute bit on the bundled ffmpeg.

Usage: python scripts/package.py artifacts/publish artifacts/package.zip
Compress-Archive and Windows zip tools drop Unix modes, which is why this exists.
"""
import os
import sys
import zipfile

root, target = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    for folder, _, files in os.walk(root):
        for name in files:
            path = os.path.join(folder, name)
            entry = os.path.relpath(path, root).replace(os.sep, "/")
            info = zipfile.ZipInfo.from_file(path, entry)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3  # Unix, so the mode below is honoured
            info.external_attr = (0o755 if entry.startswith("ffmpeg/") else 0o644) << 16
            with open(path, "rb") as source:
                archive.writestr(info, source.read())
print(f"{target}: {os.path.getsize(target) // 1_000_000} MB")
