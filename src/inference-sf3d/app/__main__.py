"""Entry point: python -m app [--host 127.0.0.1] [--port 5091]

Refuses to bind a non-loopback address unless SF3D_API_KEY is set, mirroring the .NET service.
"""

from __future__ import annotations

import argparse
import ipaddress
import sys

import uvicorn

from .config import Settings
from .cpu import apply_cpu_limit


def is_loopback(host: str) -> bool:
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def main() -> int:
    parser = argparse.ArgumentParser(description="ImageTo3D Stable Fast 3D inference service")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5091)
    args = parser.parse_args()

    settings = Settings()
    if not is_loopback(args.host) and not settings.api_key:
        print("Refusing to bind a non-loopback address without SF3D_API_KEY.", file=sys.stderr)
        return 2
    # Before uvicorn imports the app (and with it numpy/torch), so the thread pools start capped.
    apply_cpu_limit(settings.cpu_limit)

    uvicorn.run("app.main:app", host=args.host, port=args.port, log_level="info", workers=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
