"""Stage 7 resilience tests for empty, offline, retry and corruption paths."""

from __future__ import annotations

import argparse
import json
import sqlite3
import subprocess
import sys
import tempfile
import threading
from contextlib import closing
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


class ResilienceHandler(BaseHTTPRequestHandler):
    source: Path
    retry_posts = 0

    def do_GET(self) -> None:  # noqa: N802
        if self.path.startswith("/missing/"):
            self.send_response(404)
            self.end_headers()
            return
        content = b'<input name="csrfmiddlewaretoken" value="phase7-token">'
        self.send_response(200)
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self) -> None:  # noqa: N802
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        if self.path.startswith("/wrong/") or b"password=test-access-code" not in body:
            self.send_response(403)
            self.end_headers()
            return
        if self.path.startswith("/retry/") and self.retry_posts == 0:
            type(self).retry_posts += 1
            self.connection.shutdown(2)
            self.connection.close()
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


def execute(common: list[str], arguments: list[str], expected_code: int = 0) -> list[dict]:
    completed = subprocess.run(
        [sys.executable, *common, *arguments],
        capture_output=True,
        timeout=180,
    )
    stdout = completed.stdout.decode("utf-8")
    events = [json.loads(line) for line in stdout.splitlines() if line]
    if completed.returncode != expected_code:
        raise RuntimeError(
            f"worker exited with {completed.returncode}, expected {expected_code}\n"
            f"stdout:\n{stdout}\nstderr:\n{completed.stderr.decode(errors='replace')}"
        )
    return events


def result(events: list[dict]) -> dict:
    return next(event for event in events if event.get("type") == "result")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--schema", type=Path, required=True)
    args = parser.parse_args()

    ResilienceHandler.source = args.source.resolve()
    server = ThreadingHTTPServer(("127.0.0.1", 0), ResilienceHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        with tempfile.TemporaryDirectory(
            prefix="aisky-phase7-",
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

            empty = result(execute(common, ["--command", "index"]))
            assert empty["runs"] == [], empty

            corrupt_source = root / "AISky-SDS_20260605_1930+20260605_1930_V01.nc"
            corrupt_source.write_bytes(b"not-netcdf" * 1024)
            corrupt_events = execute(
                common,
                [
                    "--command",
                    "import",
                    "--source",
                    str(corrupt_source),
                    "--render-root",
                    str(render_root),
                ],
                expected_code=2,
            )
            assert any(
                event.get("type") == "error"
                and "NetCDF" in event.get("message", "")
                for event in corrupt_events
            ), corrupt_events
            with closing(sqlite3.connect(database)) as connection:
                state = connection.execute(
                    "SELECT validation_state, parse_state FROM forecast_runs"
                ).fetchone()
            assert state == ("invalid", "error"), state

            missing = result(
                execute(
                    common,
                    [
                        "--command",
                        "sync-latest",
                        "--model",
                        "AISky-SDS",
                        "--password",
                        "test-access-code",
                        "--probe-days",
                        "1",
                        "--max-lead-hours",
                        "0",
                        "--max-version",
                        "1",
                        "--now",
                        "20260605_1930",
                        "--base-url",
                        f"http://127.0.0.1:{server.server_port}/missing/",
                        "--data-root",
                        str(data_root),
                        "--render-root",
                        str(render_root),
                    ],
                )
            )
            assert missing["found"] is False, missing

            wrong_password = execute(
                common,
                [
                    "--command",
                    "sync-latest",
                    "--model",
                    "AISky-SDS",
                    "--password",
                    "wrong-code",
                    "--probe-days",
                    "1",
                    "--max-lead-hours",
                    "0",
                    "--max-version",
                    "1",
                    "--now",
                    "20260605_1930",
                    "--base-url",
                    f"http://127.0.0.1:{server.server_port}/wrong/",
                    "--data-root",
                    str(data_root),
                    "--render-root",
                    str(render_root),
                ],
                expected_code=2,
            )
            assert any(
                "访问密码验证失败" in event.get("message", "")
                for event in wrong_password
            ), wrong_password

            retry_target = (
                data_root
                / "AISky-SDS"
                / "20260605_1930"
                / "AISky-SDS_20260605_1930+20260605_1930_V01.nc"
            )
            retry_target.parent.mkdir(parents=True, exist_ok=True)
            retry_target.write_bytes(b"damaged" * 1024)
            retry_events = execute(
                common,
                [
                    "--command",
                    "sync-latest",
                    "--model",
                    "AISky-SDS",
                    "--password",
                    "test-access-code",
                    "--probe-days",
                    "1",
                    "--max-lead-hours",
                    "0",
                    "--max-version",
                    "1",
                    "--now",
                    "20260605_1930",
                    "--base-url",
                    f"http://127.0.0.1:{server.server_port}/retry/",
                    "--data-root",
                    str(data_root),
                    "--render-root",
                    str(render_root),
                ],
            )
            retried = result(retry_events)
            assert retried["found"] is True, retried
            assert any("重试" in event.get("message", "") for event in retry_events)
            assert retry_target.read_bytes()[:3] in (b"CDF", b"\x89HD")
            assert not retry_target.with_suffix(".nc.part").exists()
            with closing(sqlite3.connect(database)) as connection:
                attempts = connection.execute(
                    "SELECT MAX(attempts) FROM download_jobs"
                ).fetchone()[0]
            assert attempts == 2, attempts

            database.unlink()
            database.write_bytes(b"this is not sqlite")
            recovered_events = execute(common, ["--command", "status"])
            recovered = result(recovered_events)
            assert recovered["integrity"] == "ok", recovered
            assert any(
                "数据库损坏" in event.get("message", "")
                for event in recovered_events
            ), recovered_events
            assert list(database.parent.glob("aisky.db.corrupt-*"))

            print(
                json.dumps(
                    {
                        "status": "ok",
                        "emptyIndex": True,
                        "corruptNetcdfRejected": True,
                        "missingDataHandled": True,
                        "wrongPasswordExplained": True,
                        "networkRetryAttempts": attempts,
                        "databaseRecovered": True,
                    },
                    ensure_ascii=False,
                )
            )
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
