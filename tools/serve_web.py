#!/usr/bin/env python3
"""Static server for the published web build (web-dist/wwwroot) with the MIME types Blazor needs.

    python tools/serve_web.py [port]
"""
import http.server
import os
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "web-dist", "wwwroot")
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8080


class Handler(http.server.SimpleHTTPRequestHandler):
    extensions_map = {
        **http.server.SimpleHTTPRequestHandler.extensions_map,
        ".wasm": "application/wasm",
        ".js": "text/javascript",
        ".mjs": "text/javascript",
        ".json": "application/json",
        ".dat": "application/octet-stream",
        ".dll": "application/octet-stream",
        ".pdb": "application/octet-stream",
        ".br": "application/octet-stream",
    }

    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=ROOT, **kwargs)

    def end_headers(self):
        self.send_header("Cache-Control", "no-store")
        super().end_headers()

    def log_message(self, fmt, *args):
        pass


class Server(http.server.ThreadingHTTPServer):
    request_queue_size = 256   # Blazor fetches ~70 files at once; the default backlog of 5 resets connections
    daemon_threads = True


if __name__ == "__main__":
    print(f"serving {os.path.abspath(ROOT)} on http://localhost:{PORT}")
    Server(("127.0.0.1", PORT), Handler).serve_forever()
