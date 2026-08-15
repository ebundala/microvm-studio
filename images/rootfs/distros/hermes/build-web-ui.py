"""Build hermes-agent's dashboard web frontend once, at image-build time.

Run with cwd=/usr/local/lib/hermes-agent (the installed repo root) via the
project's own venv python, with Node.js on PATH — see the Dockerfile's
comment for why this can't just happen lazily at `hermes serve` startup
like the stock install does.
"""
import sys
from pathlib import Path

from hermes_cli.main import _build_web_ui

ok = _build_web_ui(Path("web"), fatal=True)
sys.exit(0 if ok else 1)
