"""Loads a .safetensors checkpoint by reading tensors straight into the model's parameters.

safetensors 0.4.x maps the file with torch.UntypedStorage.from_file and slices it; with torch
2.5.1 on Windows that slicing crashes (access violation in torch_cpu.dll) for SPAR3D's 7.3 GB
checkpoint. The format is simple (8-byte header length, JSON header, raw little-endian tensor
bytes), so this reads it with plain file I/O into each destination tensor: no mmap, and no
second copy of the weights in memory.
"""

from __future__ import annotations

import json
import struct

_DTYPES = {
    "F64": "float64", "F32": "float32", "F16": "float16", "BF16": "bfloat16",
    "I64": "int64", "I32": "int32", "I16": "int16", "I8": "int8", "U8": "uint8", "BOOL": "bool",
}


def load_into(model, filename: str, strict: bool = False) -> tuple[list[str], list[str]]:
    """Same contract as safetensors.torch.load_model: returns (missing, unexpected) keys."""
    import torch

    targets = model.state_dict()
    seen: set[str] = set()
    unexpected: list[str] = []
    with open(filename, "rb") as f, torch.no_grad():
        (length,) = struct.unpack("<Q", f.read(8))
        header = json.loads(f.read(length))
        base = 8 + length
        for key, info in header.items():
            if key == "__metadata__":
                continue
            if key not in targets:
                unexpected.append(key)
                continue
            dtype = getattr(torch, _DTYPES[info["dtype"]])
            start, end = info["data_offsets"]
            dst = targets[key]
            if list(dst.shape) != info["shape"]:
                raise ValueError(f"{key}: checkpoint shape {info['shape']} != model shape {list(dst.shape)}")
            f.seek(base + start)
            if dst.dtype == dtype and dst.is_contiguous() and dst.device.type == "cpu":
                if dst.numel():
                    f.readinto(memoryview(dst.reshape(-1).view(torch.uint8).numpy()))
            else:
                data = bytearray(f.read(end - start))
                dst.copy_(torch.frombuffer(data, dtype=dtype).reshape(info["shape"]))
            seen.add(key)
    missing = [k for k in targets if k not in seen]
    if strict and (missing or unexpected):
        raise RuntimeError(f"Checkpoint mismatch: {len(missing)} missing, {len(unexpected)} unexpected keys")
    return missing, unexpected
