#!/usr/bin/env python3
"""Exercise the real MCP stdio server while its configured app is unavailable.

Build the server first, then run from the repository root, for example:

    python eng/smoke-mcp.py -- dotnet \
      src/WinFormsMcp.Instrumented.Server/bin/Debug/net10.0/WinFormsMcp.Instrumented.Server.dll \
      --app /path/to/not-started/InstrumentedDemo.exe

The script never launches or stops the configured application. It uses only
Python's standard library and bounds each exchange and process shutdown.
"""

from __future__ import annotations

import argparse
from collections import deque
import json
import queue
import subprocess
import sys
import threading
import time
from typing import Any


EXPECTED_TOOLS = {
    "app_status", "app_launch", "app_stop", "app_restart", "app_logs",
    "list_windows", "control_tree", "inspect_control", "invoke_control",
    "set_text", "set_value", "set_checked", "select_control", "scroll_control",
    "ensure_visible", "read_grid", "capture", "action_status", "bridge_diagnostics",
}


class SmokeFailure(RuntimeError):
    pass


def check(condition: bool, message: str) -> None:
    if not condition:
        raise SmokeFailure(message)


class Client:
    def __init__(self, command: list[str], timeout: float):
        self.timeout = timeout
        self.next_id = 0
        self.messages: queue.Queue[tuple[str, Any]] = queue.Queue()
        self.stderr: deque[str] = deque(maxlen=100)
        self.process = subprocess.Popen(
            command,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="strict",
            bufsize=1,
        )
        self.stdout_thread = threading.Thread(target=self._stdout, daemon=True)
        self.stderr_thread = threading.Thread(target=self._stderr, daemon=True)
        self.stdout_thread.start()
        self.stderr_thread.start()

    def _stdout(self) -> None:
        assert self.process.stdout is not None
        try:
            while True:
                line = self.process.stdout.readline(16 * 1024 * 1024 + 1)
                if not line:
                    break
                if len(line) > 16 * 1024 * 1024:
                    raise SmokeFailure("MCP stdout emitted an oversized line.")
                try:
                    value = json.loads(line)
                except json.JSONDecodeError as error:
                    raise SmokeFailure("Non-JSON output appeared on MCP stdout: " + line[:200]) from error
                if not isinstance(value, dict) or value.get("jsonrpc") != "2.0":
                    raise SmokeFailure("MCP stdout contained a non-JSON-RPC message.")
                self.messages.put(("message", value))
        except Exception as error:
            self.messages.put(("error", error))
        finally:
            self.messages.put(("eof", None))

    def _stderr(self) -> None:
        assert self.process.stderr is not None
        try:
            for line in self.process.stderr:
                self.stderr.append(line.rstrip()[:4096])
        except (OSError, UnicodeError) as error:
            self.stderr.append("stderr reader: " + str(error))

    def send(self, message: dict[str, Any]) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n")
        self.process.stdin.flush()

    def call(self, method: str, parameters: dict[str, Any]) -> dict[str, Any]:
        self.next_id += 1
        request_id = self.next_id
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": parameters})
        deadline = time.monotonic() + self.timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise SmokeFailure("Timed out waiting for " + method)
            try:
                kind, value = self.messages.get(timeout=remaining)
            except queue.Empty as error:
                raise SmokeFailure("Timed out waiting for " + method) from error
            if kind == "error":
                raise value
            if kind == "eof":
                raise SmokeFailure("Server stdout closed while waiting for " + method)
            if "id" not in value:
                continue  # Valid notifications may interleave with responses.
            if value.get("method") == "ping":
                self.send({"jsonrpc": "2.0", "id": value["id"], "result": {}})
                continue
            check(value.get("id") == request_id, "Received an unexpected response ID.")
            check("error" not in value, method + " failed at the JSON-RPC layer: " + str(value.get("error")))
            result = value.get("result")
            check(isinstance(result, dict), method + " did not return an object result.")
            return result

    def tool(self, name: str) -> dict[str, Any]:
        return self.call("tools/call", {"name": name, "arguments": {}})

    def close_cleanly(self) -> None:
        assert self.process.stdin is not None
        self.process.stdin.close()
        try:
            code = self.process.wait(timeout=self.timeout)
        except subprocess.TimeoutExpired as error:
            raise SmokeFailure("The server did not exit after its MCP stdin closed.") from error
        self.stdout_thread.join(timeout=2)
        self.stderr_thread.join(timeout=2)
        check(code == 0, "The server exited with code " + str(code))
        while not self.messages.empty():
            kind, value = self.messages.get_nowait()
            if kind == "error":
                raise value

    def abort(self) -> None:
        if self.process.poll() is None:
            self.process.terminate()
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)


def text_json(result: dict[str, Any]) -> dict[str, Any]:
    blocks = [block for block in result.get("content", []) if block.get("type") == "text"]
    check(len(blocks) == 1, "Expected one text block containing structured JSON.")
    value = json.loads(blocks[0]["text"])
    check(isinstance(value, dict), "Tool text did not contain an object.")
    return value


def exercise(client: Client) -> dict[str, Any]:
    initialized = client.call("initialize", {
        "protocolVersion": "2025-11-25",
        "capabilities": {},
        "clientInfo": {"name": "winforms-mcp-instrumented-smoke", "version": "0.1.0"},
    })
    check(initialized.get("protocolVersion") == "2025-11-25", "Unexpected negotiated MCP protocol version.")
    check("tools" in initialized.get("capabilities", {}), "Server did not advertise tools.")
    client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    listed = client.call("tools/list", {})
    tools = {tool["name"]: tool for tool in listed.get("tools", [])}
    check(EXPECTED_TOOLS.issubset(tools), "Missing tools: " + ", ".join(sorted(EXPECTED_TOOLS - tools.keys())))
    capture_properties = tools["capture"].get("inputSchema", {}).get("properties", {})
    check({"scope", "generation", "id", "includeFrame", "region"}.issubset(capture_properties),
          "Capture schema is missing scope, target, or region parameters.")
    invoke_required = set(tools["invoke_control"].get("inputSchema", {}).get("required", []))
    check({"generation", "id"}.issubset(invoke_required), "Invocation schema must require a concrete instance target.")

    status_result = client.tool("app_status")
    check(not status_result.get("isError", False), "Offline app_status should still be a successful observation.")
    status = text_json(status_result)
    check(status.get("bridgeAvailable") is False, "This smoke test requires an unavailable app bridge.")

    windows_result = client.tool("list_windows")
    check(windows_result.get("isError") is True, "list_windows should fail explicitly while the bridge is unavailable.")
    offline_error = text_json(windows_result).get("error", {})
    error_code = offline_error.get("code", offline_error.get("Code"))
    check(error_code == "app_unavailable", "Unexpected offline error: " + str(error_code))

    # A failed UI tool must not disconnect the MCP session.
    check(not client.tool("app_logs").get("isError", False), "Server did not remain usable after an offline UI call.")
    client.close_cleanly()
    return {
        "status": "passed",
        "protocolVersion": initialized["protocolVersion"],
        "tools": len(tools),
        "checks": ["initialize", "tool catalog and schemas", "offline status", "structured UI error", "session survives error", "clean EOF shutdown"],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=float, default=15.0, help="Seconds allowed per MCP exchange and shutdown.")
    parser.add_argument("command", nargs=argparse.REMAINDER, help="Server command, normally after --.")
    arguments = parser.parse_args()
    command = arguments.command
    if command and command[0] == "--":
        command = command[1:]
    if not command or arguments.timeout <= 0:
        parser.error("Supply a server command after -- and a positive timeout.")
    client = Client(command, arguments.timeout)
    try:
        report = exercise(client)
        print(json.dumps(report, indent=2))
        return 0
    except (SmokeFailure, OSError, ValueError, KeyError) as error:
        print("MCP smoke test failed: " + str(error), file=sys.stderr)
        for line in list(client.stderr)[-20:]:
            print(line, file=sys.stderr)
        return 1
    finally:
        client.abort()


if __name__ == "__main__":
    sys.exit(main())
