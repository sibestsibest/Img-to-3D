"""Entry point: python -m app [--host 127.0.0.1] [--port 5091]

Refuses to bind a non-loopback address unless SF3D_API_KEY is set, mirroring the .NET service.
"""

from __future__ import annotations

import argparse
import ipaddress
import sys

import uvicorn

from .config import Settings


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

    if not is_loopback(args.host) and not Settings().api_key:
        print("Refusing to bind a non-loopback address without SF3D_API_KEY.", file=sys.stderr)
        return 2

    uvicorn.run("app.main:app", host=args.host, port=args.port, log_level="info", workers=1)
    return 0


if __name__ == "__main__":
    sys.exit(main())
