"""Caps how many CPU threads the numeric libraries use, so a generation leaves headroom on the host."""

from __future__ import annotations

import logging
import math
import os
import sys

log = logging.getLogger("sf3d.cpu")

# Read when each library starts its thread pool; rembg also sizes its ONNX Runtime session from
# OMP_NUM_THREADS. Values the user already set are kept.
_THREAD_ENV = ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS", "NUMEXPR_NUM_THREADS", "NUMBA_NUM_THREADS")


def thread_budget(fraction: float, cpu_count: int | None = None) -> int:
    cores = cpu_count or os.cpu_count() or 1
    return max(1, min(cores, math.floor(cores * fraction)))


def apply_cpu_limit(fraction: float) -> int:
    """Call before the libraries spin up (startup); calling again later is harmless."""
    threads = thread_budget(fraction)
    for name in _THREAD_ENV:
        os.environ.setdefault(name, str(threads))
    threads = int(os.environ["OMP_NUM_THREADS"])
    torch = sys.modules.get("torch")
    if torch is not None:
        torch.set_num_threads(threads)
    return threads
