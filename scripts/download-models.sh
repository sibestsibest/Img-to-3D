#!/usr/bin/env bash
# Linux/macOS/WSL equivalent of download-models.ps1 (same files and checksums).
# EMBEDDINGS=1 also downloads the optional CLIP model (~155 MB) used to rank reference images.
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

if [ "${EMBEDDINGS:-0}" = 1 ]; then
  CLIP=https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/d15189d7028b43f1d3e65039190477f6af591c2a
  fetch "$CLIP/onnx/vision_model_quantized.onnx" clip/vision_model_quantized.onnx 583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299
  fetch "$CLIP/onnx/text_model_quantized.onnx"   clip/text_model_quantized.onnx   73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a
  fetch "$CLIP/vocab.json"                      clip/vocab.json                  5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363
  fetch "$CLIP/merges.txt"                      clip/merges.txt                  9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a
fi

mkdir -p "$SAMPLE_DIR"
for name in chair.png robot.png; do
  [ -f "$SAMPLE_DIR/$name" ] || curl -fsSL -o "$SAMPLE_DIR/$name" "https://raw.githubusercontent.com/VAST-AI-Research/TripoSR/main/examples/$name"
done
