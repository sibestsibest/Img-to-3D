"""Command line: python -m parametric <command>

  describe                                   templates, parameters, sheet thicknesses (JSON on stdout)
  generate --request FILE|- --output DIR     build and export; writes DIR/response.json and prints it

Exit codes: 0 succeeded, 2 refused (invalid request or parameters), 1 internal error.
Logs go to stderr, the JSON response to stdout.
"""

from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path

from .errors import INVALID_OUTPUT_DIRECTORY, INVALID_REQUEST, ParametricError
from .worker import describe, failure, generate

MAX_REQUEST_BYTES = 1_000_000
log = logging.getLogger("parametric")


def _read_request(source: str):
    raw = sys.stdin.buffer.read(MAX_REQUEST_BYTES + 1) if source == "-" else Path(source).read_bytes()[: MAX_REQUEST_BYTES + 1]
    if len(raw) > MAX_REQUEST_BYTES:
        raise ParametricError(INVALID_REQUEST, f"The request is larger than {MAX_REQUEST_BYTES} bytes.", None)
    try:
        return json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as e:
        raise ParametricError(INVALID_REQUEST, f"The request is not valid JSON: {e}", None) from None


def _print(obj: dict) -> None:
    sys.stdout.write(json.dumps(obj, indent=2) + "\n")


def main(argv: list[str] | None = None) -> int:
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(name)s %(message)s")
    logging.getLogger("ezdxf").setLevel(logging.WARNING)  # it logs every default dictionary it creates
    parser = argparse.ArgumentParser(prog="python -m parametric", description="ImageTo3D parametric geometry worker")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("describe")
    gen = sub.add_parser("generate")
    gen.add_argument("--request", required=True, help="request JSON file, or - for stdin")
    gen.add_argument("--output", required=True, help="new or empty output directory")
    args = parser.parse_args(argv)

    if args.command == "describe":
        _print(describe())
        return 0

    output = Path(args.output)
    own_dir = True
    try:
        response = generate(_read_request(args.request), output)
        code = 0
    except ParametricError as e:
        log.info("refused: %s (%s, field %s)", e.message, e.code, e.field)
        response, code = failure(e), 2
        own_dir = e.code != INVALID_OUTPUT_DIRECTORY  # never write into a directory that was refused
    except Exception as e:  # noqa: BLE001 - reported as internal_error, details in the log only
        log.exception("generation failed")
        response, code = failure(e), 1

    if own_dir and output.is_dir():
        (output / "response.json").write_text(json.dumps(response, indent=2) + "\n", encoding="utf-8")
    _print(response)
    return code


if __name__ == "__main__":
    sys.exit(main())
