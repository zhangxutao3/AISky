"""End-to-end smoke test for the AISky worker download and NetCDF pipeline."""

from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
import sys
import tempfile
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class ShareHandler(BaseHTTPRequestHandler):
    source: Path

    def do_GET(self) -> None:  # noqa: N802 - standard library callback name
        content = (
            '<html><form><input name="csrfmiddlewaretoken" value="smoke-token">'
            "</form></html>"
        ).encode()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self) -> None:  # noqa: N802 - standard library callback name
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        if b"password=1234" not in body:
            self.send_response(403)
            self.end_headers()
            return
        size = self.source.stat().st_size
        self.send_response(200)
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("Content-Length", str(size))
        self.end_headers()
        with self.source.open("rb") as stream:
            while chunk := stream.read(1024 * 1024):
                self.wfile.write(chunk)

    def log_message(self, format: str, *args: object) -> None:
        return


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    args = parser.parse_args()

    ShareHandler.source = args.source.resolve()
    server = ThreadingHTTPServer(("127.0.0.1", 0), ShareHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        with tempfile.TemporaryDirectory(prefix="aisky-worker-smoke-") as temporary:
            root = Path(temporary)
            database = root / "cache" / "aisky.db"
            command = [
                sys.executable,
                str(args.worker.resolve()),
                "--database",
                str(database),
                "--schema",
                str(args.schema.resolve()),
                "--command",
                "download-range",
                "--model",
                "AISky-SDS",
                "--start",
                "20260605_1930",
                "--end",
                "20260605_1930",
                "--password",
                "1234",
                "--max-lead-hours",
                "3",
                "--max-version",
                "1",
                "--base-url",
                f"http://127.0.0.1:{server.server_port}/forecast_data/",
                "--data-root",
                str(root / "data"),
                "--render-root",
                str(root / "render"),
            ]
            completed = subprocess.run(
                command,
                capture_output=True,
                timeout=120,
            )
            stdout = completed.stdout.decode("utf-8")
            stderr = completed.stderr.decode(errors="replace")
            if completed.returncode != 0:
                raise RuntimeError(
                    f"worker exited with {completed.returncode}\nstdout:\n{stdout}\nstderr:\n{stderr}"
                )
            events = [json.loads(line) for line in stdout.splitlines() if line]
            result = next(event for event in events if event.get("type") == "result")
            assert result["downloaded"] == 1, result
            assert result["failed"] == 0, result
            assert len(result["index"]["runs"]) == 1, result
            assert len(result["index"]["runs"][0]["layers"]) >= 5, result
            connection = sqlite3.connect(database)
            try:
                assert connection.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
                assert connection.execute(
                    "SELECT COUNT(*) FROM forecast_runs WHERE is_plot_ready=1"
                ).fetchone()[0] == 1
            finally:
                connection.close()
            print(
                json.dumps(
                    {
                        "status": "ok",
                        "events": len(events),
                        "layers": len(result["index"]["runs"][0]["layers"]),
                        "downloaded": result["downloaded"],
                    },
                    ensure_ascii=False,
                )
            )
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
