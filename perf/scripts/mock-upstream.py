#!/usr/bin/env python3
"""Minimal OpenAI-compatible mock for local k6 smoke (no Docker). Answers instantly, many at once."""
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        body = b'{"status":"ok"}'
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length", "0"))
        _ = self.rfile.read(length) if length else b""
        body = (
            b'{"id":"chatcmpl-mock","object":"chat.completion",'
            b'"model":"local-mock","choices":[{"message":{"content":"ok"}}]}'
        )
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: object) -> None:
        return


if __name__ == "__main__":
    port = 18080
    print(f"mock upstream listening on http://127.0.0.1:{port}", flush=True)
    # ThreadingHTTPServer, not HTTPServer: the single-threaded server accepted one connection at a
    # time, so any concurrency test through the gateway measured the mock's serialization and
    # reported it as the gateway's. For a slow, deliberately concurrent backend model see
    # concurrent-mock-upstream.py.
    ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
