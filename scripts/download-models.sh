#!/usr/bin/env bash
# Linux/macOS/WSL equivalent of download-models.ps1 (same files and checksums).
set -euo pipefail

MODEL_DIR="${1:-$(dirname "$0")/../models}"
SAMPLE_DIR="${2:-$(dirname "$0")/../samples}"
TRIPOSR=https://huggingface.co/brodatech/triposr-onnx/resolve/main

fetch() { # url path sha256
  local target="$MODEL_DIR/$2"
  mkdir -p "$(dirname "$target")"
  if [ -f "$target" ] && echo "$3  $target" | sha256sum -c --status; then echo "OK (cached)  $2"; return; fi
  echo "Downloading  $2 ..."
  curl -fL --retry 3 -o "$target.download" "$1"
  echo "$3  $target.download" | sha256sum -c --status || { rm -f "$target.download"; echo "Checksum mismatch for $2" >&2; exit 1; }
  mv "$target.download" "$target"
  echo "OK           $2"
}

fetch "$TRIPOSR/triplane.onnx"      triposr/triplane.onnx      70740d13d9c240df1f97e55c08d588fe3c930c5adbab64c2bea3d023fb07494f
fetch "$TRIPOSR/triplane.onnx_data" triposr/triplane.onnx_data 3c9725a9279127ea7828a4aa025eb58fac9c841cd77f9d5241367613b3286cae
fetch "$TRIPOSR/nerf.onnx"          triposr/nerf.onnx          3800e60cff68c45d6a299154d5daa50831ca3204ee245407da500fc2a01fbe7b
fetch "$TRIPOSR/config.yaml"        triposr/config.yaml        74ca708ce086bf68e97709ea6b3d91f14717921c04691e84043f0eb8fcc68e62
fetch "$TRIPOSR/LICENSE.txt"        triposr/LICENSE.txt        b5ed400bf58c5d7ebc253530c96cfb9a3506261809ed1d4438c22d23d5f6fc41
fetch https://github.com/danielgatis/rembg/releases/download/v0.0.0/u2net.onnx u2net/u2net.onnx 8d10d2f3bb75ae3b6d527c77944fc5e7dcd94b29809d47a739a7a728a912b491

mkdir -p "$SAMPLE_DIR"
for name in chair.png robot.png; do
  [ -f "$SAMPLE_DIR/$name" ] || curl -fsSL -o "$SAMPLE_DIR/$name" "https://raw.githubusercontent.com/VAST-AI-Research/TripoSR/main/examples/$name"
done
