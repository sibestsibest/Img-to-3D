"""Errors with a stable code and the offending field, so callers can show them next to the input."""

from __future__ import annotations


class ParametricError(ValueError):
    """A request the engine refuses: bad parameters, unknown template, impossible geometry."""

    def __init__(self, code: str, message: str, field: str | None = None):
        super().__init__(message)
        self.code = code
        self.message = message
        self.field = field

    def to_dict(self) -> dict:
        return {"code": self.code, "message": self.message, "field": self.field}


# Error codes (part of the worker contract).
INVALID_REQUEST = "invalid_request"
UNKNOWN_TEMPLATE = "unknown_template"
INVALID_PARAMETER = "invalid_parameter"
UNSUPPORTED_THICKNESS = "unsupported_thickness"
GEOMETRY_CONFLICT = "geometry_conflict"
LIMIT_EXCEEDED = "limit_exceeded"
INVALID_OUTPUT_DIRECTORY = "invalid_output_directory"
INTERNAL_ERROR = "internal_error"
