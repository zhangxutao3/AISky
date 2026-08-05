"""Stage 5 integration test for latest-run sync and retention cleanup."""

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

    def do_GET(self) -> None:  # noqa: N802
        content = b'<input name="csrfmiddlewaretoken" value="phase5-token">'
        self.send_response(200)
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self) -> None:  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        if b"password=1234" not in self.rfile.read(length):
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


def run_worker(common: list[str], arguments: list[str]) -> dict:
    completed = subprocess.run(
        [sys.executable, *common, *arguments],
        capture_output=True,
        timeout=180,
    )
    stdout = completed.stdout.decode("utf-8")
    if completed.returncode != 0:
        raise RuntimeError(
            f"worker exited with {completed.returncode}\n"
            f"stdout:\n{stdout}\nstderr:\n{completed.stderr.decode(errors='replace')}"
        )
    events = [json.loads(line) for line in stdout.splitlines() if line]
    return next(event for event in events if event.get("type") == "result")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    args = parser.parse_args()

    ShareHandler.source = args.source.resolve()
    server = ThreadingHTTPServer(("127.0.0.1", 0), ShareHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(
            prefix="aisky-phase5-",
            ignore_cleanup_errors=True,
        ) as temporary:
            root = Path(temporary)
            database = root / "cache" / "aisky.db"
            data_root = root / "data"
            render_root = root / "render"
            common = [
                str(args.worker.resolve()),
                "--database",
                str(database),
                "--schema",
                str(args.schema.resolve()),
            ]
            synced = run_worker(
                common,
                [
                    "--command",
                    "sync-latest",
                    "--model",
                    "AISky-SDS",
                    "--password",
                    "1234",
                    "--max-lead-hours",
                    "3",
                    "--max-version",
                    "1",
                    "--probe-days",
                    "1",
                    "--now",
                    "20260605_1930",
                    "--base-url",
                    f"http://127.0.0.1:{server.server_port}/forecast_data/",
                    "--data-root",
                    str(data_root),
                    "--render-root",
                    str(render_root),
                ],
            )
            assert synced["found"] is True, synced
            assert synced["downloaded"] == 1, synced
            assert len(synced["index"]["runs"]) == 1, synced
            with sqlite3.connect(database) as connection:
                row = connection.execute(
                    "SELECT source_path, manifest_path FROM forecast_runs"
                ).fetchone()
            assert row is not None
            source_path = Path(row[0])
            manifest_path = Path(row[1])
            assert source_path.is_file()
            assert manifest_path.is_file()

            cleaned = run_worker(
                common,
                [
                    "--command",
                    "cleanup",
                    "--retention-days",
                    "1",
                    "--now",
                    "20260610_1930",
                    "--data-root",
                    str(data_root),
                    "--render-root",
                    str(render_root),
                ],
            )
            assert cleaned["removedRuns"] == 1, cleaned
            assert cleaned["failed"] == 0, cleaned
            assert cleaned["reclaimedBytes"] > 0, cleaned
            assert not source_path.exists()
            assert not manifest_path.exists()
            with sqlite3.connect(database) as connection:
                assert connection.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
                assert connection.execute("SELECT COUNT(*) FROM forecast_runs").fetchone()[0] == 0
                assert connection.execute("SELECT COUNT(*) FROM cache_entries").fetchone()[0] == 0

            print(
                json.dumps(
                    {
                        "status": "ok",
                        "synced": synced["downloaded"],
                        "removed": cleaned["removedRuns"],
                        "reclaimedBytes": cleaned["reclaimedBytes"],
                    },
                    ensure_ascii=False,
                )
            )
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
