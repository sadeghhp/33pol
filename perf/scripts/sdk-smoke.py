#!/usr/bin/env python3
"""Manual GA smoke: OpenAI Python SDK against a running 33pol gateway.

Usage:
  export OPENAI_BASE_URL=http://localhost:8080/v1
  export OPENAI_API_KEY=sk-your-key   # optional when gateway auth is off
  python3 perf/scripts/sdk-smoke.py

Exit 0 on success; non-zero on failure.
"""
from __future__ import annotations

import os
import sys


def main() -> int:
    try:
        from openai import OpenAI
    except ImportError:
        print("Install: pip install openai", file=sys.stderr)
        return 2

    base_url = os.environ.get("OPENAI_BASE_URL", "http://localhost:8080/v1")
    api_key = os.environ.get("OPENAI_API_KEY", "sk-local-smoke")
    model = os.environ.get("MODEL", "gpt-local")

    client = OpenAI(base_url=base_url, api_key=api_key)

    print("1. GET /v1/models")
    models = client.models.list()
    assert len(models.data) > 0, "expected at least one model"
    print(f"   models: {[m.id for m in models.data[:5]]}")

    print("2. POST /v1/chat/completions (non-stream)")
    chat = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "sdk smoke ping"}],
        stream=False,
    )
    assert chat.choices[0].message.content, "empty completion"
    print(f"   reply: {chat.choices[0].message.content[:80]!r}")

    print("3. POST /v1/chat/completions (stream)")
    stream = client.chat.completions.create(
        model=model,
        messages=[{"role": "user", "content": "stream ping"}],
        stream=True,
    )
    chunks = 0
    for _ in stream:
        chunks += 1
    assert chunks > 0, "expected SSE chunks"
    print(f"   chunks: {chunks}")

    print("SDK smoke passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
