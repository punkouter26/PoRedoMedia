#!/usr/bin/env bash
# scripts/package.sh [package.zip]
# Builds the App Service package: publish, a static Linux ffmpeg, the bundled font, a smoke check
# of both, then the zip. deploy.yml runs exactly this; locally it needs Linux or WSL (the ffmpeg
# and font are Linux ones). The publish folder is left at ./publish for inspection.
set -euo pipefail

TARGET="${1:-package.zip}"
PUBLISH=publish

# Statically linked ffmpeg, bundled into the publish output so the app ships as code on the F1
# plan rather than as a container.
#
# Must be the **gpl** variant: the render pipeline encodes with libx264, which the lgpl builds
# omit. Must be the non-`shared` archive: only the static build's binaries can be lifted out of
# the tarball on their own.
#
# `latest` + `n8.1-latest` are BtbN's rolling names and the only ones safe to reference: dated
# `autobuild-*` tags are pruned after about two weeks. A checksum pin is therefore impossible;
# the verification below is behavioural instead.
FFMPEG_TAG="${FFMPEG_TAG:-latest}"
FFMPEG_ARCHIVE="${FFMPEG_ARCHIVE:-ffmpeg-n8.1-latest-linux64-gpl-8.1}"
FONT=/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf

# Framework-dependent on purpose: App Service supplies the DOTNETCORE|10.0 runtime.
rm -rf "$PUBLISH"
dotnet publish src/PoRedoMedia.Api -c Release -o "$PUBLISH"

curl -fsSL -o ffmpeg.tar.xz \
  "https://github.com/BtbN/FFmpeg-Builds/releases/download/${FFMPEG_TAG}/${FFMPEG_ARCHIVE}.tar.xz"
sha256sum ffmpeg.tar.xz   # recorded in the log so a bad build can be identified afterwards

# FFmpegProcess looks for exactly this directory next to the app assembly.
mkdir -p "$PUBLISH/ffmpeg"
tar -xJf ffmpeg.tar.xz --strip-components=2 -C "$PUBLISH/ffmpeg" \
  "${FFMPEG_ARCHIVE}/bin/ffmpeg" "${FFMPEG_ARCHIVE}/bin/ffprobe"
chmod +x "$PUBLISH/ffmpeg/ffmpeg" "$PUBLISH/ffmpeg/ffprobe"
rm -f ffmpeg.tar.xz

"$PUBLISH/ffmpeg/ffmpeg" -version | head -1
"$PUBLISH/ffmpeg/ffprobe" -version | head -1
"$PUBLISH/ffmpeg/ffmpeg" -hide_banner -encoders | grep -q libx264 \
  || { echo "::error::bundled ffmpeg lacks libx264 (wrong build variant)"; exit 1; }

# The App Service .NET image is not known to carry any font, and both the meme renderer and
# ffmpeg's drawtext need one. FFmpegProcess.BundledFontPath is this file.
if [ ! -f "$FONT" ]; then
  sudo apt-get update && sudo apt-get install -y --no-install-recommends fonts-dejavu-core
fi
mkdir -p "$PUBLISH/fonts"
cp "$FONT" "$PUBLISH/fonts/"

# An encoder listed is not an encoder that works, and a font present is not a font drawtext
# can open. Encode one captioned frame with the bundled binaries and the bundled font, so a bad
# archive fails here rather than on the first render in production.
"$PUBLISH/ffmpeg/ffmpeg" -hide_banner -loglevel error \
  -f lavfi -i testsrc=size=64x64:rate=1 \
  -vf "drawtext=text='OK':fontfile='$PUBLISH/fonts/DejaVuSans-Bold.ttf':fontsize=12" \
  -frames:v 1 -c:v libx264 -y smoke.mp4
[ "$("$PUBLISH/ffmpeg/ffprobe" -v error -select_streams v:0 \
       -show_entries stream=codec_name -of csv=p=0 smoke.mp4)" = "h264" ] \
  || { echo "::error::ffprobe did not read back an h264 stream"; exit 1; }
rm -f smoke.mp4
du -sh "$PUBLISH"

# The zip, written by Python so the bundled ffmpeg keeps its execute bit: Compress-Archive and
# Windows zip tools drop Unix modes.
python3 - "$PUBLISH" "$TARGET" <<'PY'
import os, sys, zipfile

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
PY
