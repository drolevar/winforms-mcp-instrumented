#!/usr/bin/env python3
"""Drive the sample application through the real MCP sidecar on an interactive Windows desktop.

This is the executable form of the manual exercise in docs/07-implementation-status.md.
It launches the sidecar, which launches the instrumented demo, and performs the
observe/act/verify cycles for standard controls, the modal dialog, identity changes,
captures, an application grid adapter (read, select, edit, stale views, a context menu)
and the application restart. Every check is recorded; the script exits
non-zero if any check fails. It never touches processes it did not start.

    python eng/exercise-demo.py --captures artifacts/validation/captures -- \
        dotnet <server dll> --app <instrumented InstrumentedDemo.exe>

Requires Windows, an interactive desktop, and an instrumented (Debug or explicit
EnableWinFormsMcpInstrumentation=true) demo. Python standard library only.
"""

from __future__ import annotations

import argparse
import base64
import importlib.util
import json
import pathlib
import re
import struct
import sys
import time
from typing import Any

_spec = importlib.util.spec_from_file_location("smoke_mcp", pathlib.Path(__file__).with_name("smoke-mcp.py"))
smoke = importlib.util.module_from_spec(_spec)  # type: ignore[arg-type]
sys.modules["smoke_mcp"] = smoke
_spec.loader.exec_module(smoke)  # type: ignore[union-attr]


class Report:
    def __init__(self) -> None:
        self.results: list[dict[str, Any]] = []
        self.notes: dict[str, Any] = {}

    def check(self, name: str, condition: bool, detail: Any = None) -> bool:
        self.results.append({"check": name, "passed": bool(condition), "detail": detail if not condition else None})
        print(("PASS  " if condition else "FAIL  ") + name + ("" if condition else "  -> " + str(detail)[:600]))
        return bool(condition)

    @property
    def failed(self) -> list[dict[str, Any]]:
        return [r for r in self.results if not r["passed"]]


class Tools:
    """Thin typed wrapper over tools/call that parses the JSON text block."""

    def __init__(self, client: Any) -> None:
        self.client = client

    def call(self, name: str, **arguments: Any) -> tuple[bool, dict[str, Any], list[dict[str, Any]]]:
        result = self.client.call("tools/call", {"name": name, "arguments": arguments})
        texts = [b for b in result.get("content", []) if b.get("type") == "text"]
        images = [b for b in result.get("content", []) if b.get("type") == "image"]
        parsed = json.loads(texts[0]["text"]) if texts else {}
        return bool(result.get("isError")), parsed, images

    def result(self, name: str, **arguments: Any) -> dict[str, Any]:
        is_error, parsed, _ = self.call(name, **arguments)
        if is_error:
            raise smoke.SmokeFailure(name + " failed: " + json.dumps(parsed)[:500])
        return parsed.get("result", parsed)


def error_code(parsed: dict[str, Any]) -> str | None:
    # Strict on purpose: every tool error must use the documented camelCase "code" key.
    error = parsed.get("error") or {}
    return error.get("code")


class Desktop:
    """Win32 helpers used only to arrange the demo window; the product has no such tool."""

    def __init__(self) -> None:
        import ctypes
        from ctypes import wintypes
        self.ctypes, self.wintypes = ctypes, wintypes
        self.user32 = ctypes.windll.user32
        self.user32.SetThreadDpiAwarenessContext(ctypes.c_void_p(-4))  # PerMonitorV2: no coordinate virtualization
        self.user32.GetDpiForWindow.restype = ctypes.c_uint

    def virtual_screen(self) -> tuple[int, int, int, int]:
        m = self.user32.GetSystemMetrics
        left, top, width, height = m(76), m(77), m(78), m(79)
        return left, top, left + width, top + height

    def main_window(self, pid: int) -> int:
        ctypes, wintypes = self.ctypes, self.wintypes
        found: list[int] = []

        @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
        def visit(hwnd: int, _: int) -> bool:
            owner = wintypes.DWORD()
            self.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
            if owner.value == pid and self.user32.IsWindowVisible(hwnd) and not self.user32.GetWindow(hwnd, 4):
                found.append(hwnd)
            return True

        self.user32.EnumWindows(visit, 0)
        if not found:
            raise smoke.SmokeFailure("No visible top-level window for process " + str(pid))
        return found[0]

    def rect(self, hwnd: int) -> tuple[int, int, int, int]:
        r = self.wintypes.RECT()
        self.user32.GetWindowRect(hwnd, self.ctypes.byref(r))
        return r.left, r.top, r.right, r.bottom

    def move(self, hwnd: int, x: int, y: int) -> None:
        # SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        self.user32.SetWindowPos(hwnd, 0, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010)

    def dpi(self, hwnd: int) -> int:
        return int(self.user32.GetDpiForWindow(hwnd))


def png_size(data: bytes) -> tuple[int, int]:
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise smoke.SmokeFailure("Image block is not a PNG.")
    return struct.unpack(">II", data[16:24])


class Session:
    def __init__(self, tools: Tools, report: Report) -> None:
        self.tools = tools
        self.report = report
        self.generation = ""
        self.nodes: dict[str, dict[str, Any]] = {}

    def refresh(self, **arguments: Any) -> dict[str, dict[str, Any]]:
        tree = self.tools.result("control_tree", **arguments)
        self.generation = tree["generation"]
        self.nodes = {}
        for node in tree["nodes"]:
            if node["name"]:
                self.nodes.setdefault(node["name"], node)
        self.truncated = tree["truncated"]
        return self.nodes

    def node(self, name: str) -> dict[str, Any]:
        self.refresh()
        if name not in self.nodes:
            raise smoke.SmokeFailure("Control not found in tree: " + name)
        return self.nodes[name]

    def inspect(self, name: str, **extra: Any) -> dict[str, Any]:
        node = self.node(name)
        return self.tools.result("inspect_control", generation=node["generation"], id=node["id"], **extra)

    def act(self, tool: str, name: str, **arguments: Any) -> dict[str, Any]:
        node = self.node(name)
        receipt = self.tools.result(tool, generation=node["generation"], id=node["id"], **arguments)
        return self.wait(receipt)

    def wait(self, receipt: dict[str, Any], accept: tuple[str, ...] = ("completed", "failed"), timeout: float = 8.0) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        while True:
            status = self.tools.result("action_status", generation=receipt["generation"], actionId=receipt["actionId"])
            if status["state"] in accept:
                return status
            if time.monotonic() > deadline:
                raise smoke.SmokeFailure("Action did not reach " + "/".join(accept) + ": " + json.dumps(status))
            time.sleep(0.05)

    def wait_for_node(self, name: str, timeout: float = 5.0) -> dict[str, Any]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            self.refresh()
            if name in self.nodes:
                return self.nodes[name]
            time.sleep(0.05)
        raise smoke.SmokeFailure("Control did not appear: " + name)


def exercise(client: Any, report: Report, captures: pathlib.Path) -> None:
    tools = Tools(client)
    client.call("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                               "clientInfo": {"name": "winforms-mcp-instrumented-exercise", "version": "0.1.0"}})
    client.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
    s = Session(tools, report)

    # --- lifecycle: offline, launch ---------------------------------------
    status = tools.result("app_status")
    report.check("app_status works before launch", status.get("bridgeAvailable") is False, status)
    launched = tools.result("app_launch")
    status = tools.result("app_status")
    report.check("app_launch connects the bridge", status.get("bridgeAvailable") is True, [launched, status])
    first_generation = status.get("generation")

    windows = tools.result("list_windows")
    report.check("list_windows reports the main form", any(w["name"] == "MainForm" for w in windows["windows"]), windows)
    s.refresh()
    report.check("tree is not truncated at default limits", not s.truncated)
    report.check("tree has the standard controls", all(n in s.nodes for n in
                 ("txtName", "btnGreet", "chkEnabled", "numericValue", "dateValue", "listChoice", "dataGrid", "treeItems")))

    # --- standard controls -------------------------------------------------
    password = s.nodes["txtPassword"]
    report.check("password text is redacted in the tree", password["text"] is None and password["textRedacted"] is True, password)

    r = s.act("set_text", "txtName", text="Ada")
    report.check("set_text completes", r["state"] == "completed", r)
    report.check("set_text changed the control", s.inspect("txtName")["text"] == "Ada")
    r = s.act("invoke_control", "btnGreet")
    report.check("invoke_control click completes", r["state"] == "completed", r)
    report.check("button handler changed application state", s.node("lblStatus")["text"] == "Hello, Ada!", s.nodes["lblStatus"])

    r = s.act("set_checked", "chkEnabled", value=True)
    report.check("set_checked check box", r["state"] == "completed" and s.inspect("chkEnabled")["checked"] is True, r)
    r = s.act("set_checked", "radioChoice", value=True)
    report.check("set_checked radio button", r["state"] == "completed" and s.inspect("radioChoice")["checked"] is True, r)
    r = s.act("set_value", "numericValue", value=42.5)
    report.check("set_value numeric", r["state"] == "completed" and float(s.inspect("numericValue")["value"]) == 42.5, r)
    r = s.act("set_value", "numericValue", value=1000)
    report.check("set_value rejects out-of-range numeric", r["state"] == "failed" and r["error"]["code"] == "invalid_argument", r)
    r = s.act("set_value", "trackValue", value=70)
    report.check("set_value trackbar", r["state"] == "completed" and s.inspect("trackValue")["value"] == 70, r)
    r = s.act("set_value", "dateValue", value="2026-06-15")
    report.check("set_value date", r["state"] == "completed" and s.inspect("dateValue")["value"].startswith("2026-06-15"), r)
    r = s.act("select_control", "comboChoice", index=2)
    report.check("select combo item", r["state"] == "completed" and s.inspect("comboChoice")["selectedIndex"] == 2, r)
    r = s.act("select_control", "listChoice", index=1)
    items = s.inspect("listChoice")
    report.check("select list item", r["state"] == "completed" and items["selectedIndex"] == 1 and len(items["items"]) == 4, items)
    r = s.act("set_text", "txtPassword", text="x")
    report.check("set_text on a password box is accepted but value stays redacted",
                 r["state"] == "completed" and s.inspect("txtPassword")["text"] is None, r)

    # --- menus and context menus (element nodes) ---------------------------
    def subtree(root: dict[str, Any]) -> dict[str, dict[str, Any]]:
        # Elements inside a closed dropdown are omitted from window-rooted trees; a tree rooted at the
        # element expands its subtree without opening anything.
        tree = tools.result("control_tree", generation=root["generation"], id=root["id"])
        return {n["name"]: n for n in tree["nodes"] if n["name"]}

    def act_on(node: dict[str, Any], **arguments: Any) -> dict[str, Any]:
        return s.wait(tools.result("invoke_control", generation=node["generation"], id=node["id"], **arguments))

    menu_bar = s.node("mainMenu")
    file_item = s.nodes.get("mnuFile", {})
    report.check("the menu strip is a control node and its File item an element node under it",
                 menu_bar.get("kind") == "control" and file_item.get("kind") == "item"
                 and file_item.get("parentId") == menu_bar["id"] and file_item.get("text") == "File"
                 and file_item.get("role") == "menu_item", [menu_bar, file_item])
    report.check("a closed dropdown reports its child count and omits its children from the window tree",
                 file_item.get("opened") is False and file_item.get("childCount") == 2
                 and file_item.get("childrenOmitted") is True and "mnuExport" not in s.nodes, file_item)
    file_tree = subtree(file_item)
    export_item, csv_item, disabled_item = (file_tree.get(n, {}) for n in ("mnuExport", "mnuCsv", "mnuDisabled"))
    report.check("a tree rooted at mnuFile lists File > Export > CSV as element nodes with the parent chain",
                 export_item.get("kind") == "item" and csv_item.get("kind") == "item"
                 and export_item.get("parentId") == file_item["id"] and csv_item.get("parentId") == export_item.get("id")
                 and disabled_item.get("parentId") == file_item["id"], file_tree)
    report.check("element nodes carry the item state and capabilities",
                 csv_item.get("text") == "CSV" and csv_item.get("enabled") is True and "click" in csv_item.get("capabilities", [])
                 and disabled_item.get("enabled") is False and "open" in export_item.get("capabilities", []), file_tree)
    report.check("reading the subitems did not open the menu",
                 s.inspect("mnuFile")["opened"] is False and s.nodes["mnuFile"]["opened"] is False)

    r = act_on(csv_item)
    report.check("invoke_control click on an element completes with a handler receipt",
                 r["state"] == "completed" and r["result"].get("action") == "click" and r["result"].get("handlerReturned") is True, r)
    report.check("the menu item's click handler changed application state", s.node("lblStatus")["text"] == "CSV exported", s.nodes["lblStatus"])

    r = act_on(s.node("btnGreet"))
    report.check("the status label was reset by an unrelated handler", s.node("lblStatus")["text"] == "Hello, Ada!", s.nodes["lblStatus"])
    r = act_on(disabled_item)
    report.check("click on a disabled menu item fails with disabled_control and runs no handler",
                 r["state"] == "failed" and r["error"]["code"] == "disabled_control"
                 and s.node("lblStatus")["text"] == "Hello, Ada!", r)

    # open is best-effort: in the plain-WinForms spike a dropdown did not remain visible while its form was
    # not the active window (docs/09-element-adapters.md), so only the observed state is asserted.
    r = act_on(file_item, action="open")
    opened_now = s.inspect("mnuFile")["opened"]
    report.notes["menuOpenObservedOpened"] = r["result"].get("opened")
    report.check("open completes and its receipt reports the observed state, equal to a fresh inspect",
                 r["state"] == "completed" and isinstance(r["result"].get("opened"), bool)
                 and r["result"]["opened"] == opened_now, [r, opened_now])
    after_open = subtree(s.node("mnuFile"))
    report.check("subitems stay readable after open, whichever state was observed",
                 all(n in after_open for n in ("mnuExport", "mnuCsv", "mnuDisabled")), after_open)
    r = act_on(s.node("mnuFile"), action="close")
    report.check("close completes and the dropdown is observed closed",
                 r["state"] == "completed" and r["result"].get("opened") is False and s.inspect("mnuFile")["opened"] is False, r)

    panel_tree = tools.result("control_tree", generation=s.nodes["standardPanel"]["generation"], id=s.nodes["standardPanel"]["id"])
    panel_nodes = {n["name"]: n for n in panel_tree["nodes"] if n["name"]}
    context = panel_nodes.get("ctxDemo", {})
    report.check("the panel's ContextMenuStrip is reached through the panel, closed, with its child count",
                 context.get("kind") == "item" and context.get("role") == "context_menu"
                 and context.get("parentId") == s.nodes["standardPanel"]["id"]
                 and context.get("opened") is False and context.get("childCount") == 1 and context.get("childrenOmitted") is True,
                 [{k: n.get(k) for k in ("name", "kind", "role", "parentId", "id", "opened", "childCount", "childrenOmitted")}
                  for n in panel_tree["nodes"] if n["kind"] == "item"])
    about = subtree(context).get("ctxAbout", {})
    report.check("a tree rooted at the context menu lists its item beneath it",
                 about.get("kind") == "item" and about.get("parentId") == context.get("id") and about.get("text") == "About this panel", about)
    r = act_on(about)
    report.check("clicking the context menu item ran its handler", r["state"] == "completed"
                 and s.node("lblStatus")["text"] == "About clicked", [r, s.nodes["lblStatus"]])

    # --- tabs, grid, tree, scrolling --------------------------------------
    r = s.act("select_control", "mainTabs", index=2)
    report.check("select tab page", r["state"] == "completed" and s.inspect("mainTabs")["selectedIndex"] == 2, r)
    grid_node = s.node("dataGrid")
    grid = tools.result("read_grid", generation=grid_node["generation"], id=grid_node["id"], startRow=5, rowCount=3)
    report.check("read_grid returns the requested page", [row["index"] for row in grid["rows"]] == [5, 6, 7]
                 and grid["rows"][0]["cells"][1] == "Item 5", grid)
    r = s.act("select_control", "dataGrid", row=10, column=1)
    report.check("select grid cell", r["state"] == "completed", r)
    r = s.act("scroll_control", "dataGrid", row=100)
    report.check("scroll grid to a row", r["state"] == "completed", r)
    r = s.act("select_control", "treeItems", path=[1, 2])
    tree_items = s.inspect("treeItems")
    selected = [i for i in tree_items["items"] if i["selected"]]
    report.check("select tree path", r["state"] == "completed" and selected and selected[0]["path"] == [1, 2], tree_items["items"][:12])

    r = s.act("ensure_visible", "scrollText40")
    report.check("ensure_visible selects the ancestor tab and scrolls", r["state"] == "completed", r)
    report.check("ensure_visible made the scrolling tab current", s.inspect("mainTabs")["selectedIndex"] == 3)
    r = s.act("scroll_control", "scrollPanel", y=400)
    panel_bounds = s.node("scrollPanel")["bounds"]
    first_row = s.node("scrollText0")["bounds"]
    # scroll_control takes an absolute offset: row 0 sits 20 px below the content origin.
    report.check("scroll_control sets the absolute offset",
                 r["state"] == "completed" and first_row["y"] - panel_bounds["y"] == 20 - 400 + 2,
                 [r, panel_bounds, first_row])

    captures.mkdir(parents=True, exist_ok=True)

    def capture(label: str, **arguments: Any) -> tuple[bool, dict[str, Any], list[tuple[int, int]]]:
        is_error, parsed, images = tools.call("capture", **arguments)
        sizes = []
        for index, image in enumerate(images):
            data = base64.b64decode(image["data"])
            sizes.append(png_size(data))
            (captures / f"{label}-{index}.png").write_bytes(data)
        return is_error, parsed, sizes

    panel = s.node("scrollPanel")
    err, meta, sizes = capture("control-scrollPanel", scope="control", generation=panel["generation"], id=panel["id"])
    report.check("capture control", not err and len(sizes) == 1, meta)
    if sizes:
        img = meta["images"][0]
        report.check("capture size matches reported pixels", sizes[0] == (img["width"], img["height"]) if "width" in img else True, [sizes, img])
    # Fully scrolled out of its viewport: the bridge must refuse rather than return unrelated pixels.
    child = s.node("scrollText40")
    err, meta, _ = capture("scrolled-out-child", scope="control", generation=child["generation"], id=child["id"])
    report.check("a control scrolled out of its container is refused as capture_unavailable",
                 err and error_code(meta) == "capture_unavailable", meta)
    # Straddle the viewport's bottom edge: 10 px of the 21 px text box remain visible.
    viewport = s.node("scrollPanel")["bounds"]["height"]
    s.act("scroll_control", "scrollPanel", y=1700 - (viewport - 10))
    child = s.node("scrollText40")
    err, meta, sizes = capture("clipped-child", scope="control", generation=child["generation"], id=child["id"])
    image = meta["images"][0] if not err else {}
    # Bounds are client rectangles (the text box's 2 px border is excluded), so derive the
    # expected visible height from the reported geometry instead of assuming a number.
    visible_bottom = s.node("scrollPanel")["bounds"]["y"] + s.nodes["scrollPanel"]["bounds"]["height"]
    expected_height = visible_bottom - child["bounds"]["y"]
    report.check("a partially clipped child is captured and reported as clipped",
                 not err and 0 < expected_height < child["bounds"]["height"]
                 and image.get("clipped") is True and image.get("height") == expected_height
                 and image.get("requestedBounds", {}).get("height") == child["bounds"]["height"], [expected_height, meta])
    report.check("clipped capture PNG matches reported pixels", bool(sizes) and sizes[0] == (image.get("width"), image.get("height")), [sizes, image])
    err, meta, sizes = capture("region", scope="control", generation=panel["generation"], id=panel["id"],
                               region={"x": 10, "y": 10, "width": 200, "height": 80})
    report.check("capture region", not err and sizes == [(200, 80)], [err, meta, sizes])
    err, meta, sizes = capture("window", scope="window", generation=panel["generation"], id=panel["id"], includeFrame=True)
    report.check("capture window with frame", not err and len(sizes) == 1, meta)
    err, meta, sizes = capture("application", scope="application")
    report.check("capture application", not err and len(sizes) >= 1, meta)
    report.check("capture metadata declares its method and that occlusion is possible",
                 not err and all(i.get("method") == "screen_copy" and i.get("occlusionPossible") is True for i in meta["images"]), meta)
    err, meta, _ = capture("bad-region", scope="control", generation=panel["generation"], id=panel["id"],
                           region={"x": 0, "y": 0, "width": 0, "height": 10})
    report.check("zero-size region is rejected", err, meta)

    # --- desktop boundary and DPI ------------------------------------------
    desktop = Desktop()
    hwnd = desktop.main_window(status["processId"])
    report.notes["windowDpi"] = desktop.dpi(hwnd)
    report.notes["virtualScreen"] = desktop.virtual_screen()
    left, top, right, bottom = desktop.virtual_screen()
    original = desktop.rect(hwnd)
    desktop.move(hwnd, right - 600, top + 80)
    time.sleep(0.4)
    err, meta, sizes = capture("window-off-right-edge", scope="window", generation=panel["generation"], id=panel["id"])
    image = meta["images"][0] if not err else {}
    report.check("a window partly beyond the desktop is clipped to the desktop and says so",
                 not err and image.get("clipped") is True and image["bounds"]["x"] + image["bounds"]["width"] <= right
                 and image["requestedBounds"]["width"] > image["width"], meta)
    desktop.move(hwnd, left - 400, bottom - 300)
    time.sleep(0.4)
    err, meta, sizes = capture("window-off-bottom-left", scope="window", generation=panel["generation"], id=panel["id"])
    image = meta["images"][0] if not err else {}
    report.check("a window beyond the left and bottom edges is clipped on both axes",
                 not err and image.get("clipped") is True and image["bounds"]["x"] >= left
                 and image["bounds"]["y"] + image["bounds"]["height"] <= bottom, meta)
    desktop.move(hwnd, original[0], original[1])
    time.sleep(0.4)

    # --- modal dialog -----------------------------------------------------
    s.act("select_control", "mainTabs", index=0)
    opening = tools.result("invoke_control", generation=s.node("btnOpenDialog")["generation"], id=s.nodes["btnOpenDialog"]["id"])
    report.check("modal opener returns a receipt immediately", opening["state"] == "queued", opening)
    close = s.wait_for_node("btnCloseDialog")
    status = tools.result("action_status", generation=opening["generation"], actionId=opening["actionId"])
    report.check("opening action stays 'started' while the dialog is open", status["state"] == "started", status)
    windows = tools.result("list_windows")
    report.check("modal dialog is listed as a window", any(w["name"] == "DetailsDialog" for w in windows["windows"]), windows)
    err, meta, sizes = capture("modal-application", scope="application")
    report.check("application capture includes the modal dialog window", not err and len(sizes) == 2, [err, sizes])
    closing = tools.result("invoke_control", generation=close["generation"], id=close["id"])
    closing_status = s.wait(closing)
    opening_status = s.wait(opening)
    report.check("closing the dialog completes both actions",
                 closing_status["state"] == "completed" and opening_status["state"] == "completed", [closing_status, opening_status])
    s.refresh()
    report.check("dialog controls leave the tree", "btnCloseDialog" not in s.nodes)
    report.check("handler continuation ran after the dialog closed", s.nodes["lblStatus"]["text"].startswith("Modal dialog closed"), s.nodes["lblStatus"])

    # --- identity ----------------------------------------------------------
    s.act("select_control", "mainTabs", index=1)
    editor = s.node("txtDynamic")
    old_id, old_parent = editor["id"], editor["parentId"]
    s.act("invoke_control", "btnRecreateHandle")
    after = s.node("txtDynamic")
    report.check("HWND recreation preserves the ID", after["id"] == old_id, [old_id, after["id"]])
    s.act("invoke_control", "btnMoveEditor")
    after = s.node("txtDynamic")
    report.check("reparenting preserves the ID and changes the parent", after["id"] == old_id and after["parentId"] != old_parent, [after, old_parent])
    s.act("invoke_control", "btnReplaceEditor")
    replacement = s.node("txtDynamic")
    report.check("replacement gets a new ID", replacement["id"] != old_id, [old_id, replacement["id"]])
    is_error, parsed, _ = tools.call("inspect_control", generation=editor["generation"], id=old_id)
    report.check("the old ID fails as stale_target", is_error and error_code(parsed) == "stale_target", parsed)
    is_error, parsed, _ = tools.call("invoke_control", generation=editor["generation"], id=old_id)
    report.check("a mutation of a stale ID fails explicitly", is_error is False or error_code(parsed) == "stale_target", parsed)
    old_receipt = parsed.get("result") or {}
    if old_receipt.get("actionId"):
        report.check("stale-ID mutation receipt ends failed with stale_target",
                     s.wait(old_receipt)["error"]["code"] == "stale_target")

    # --- a custom grid through an application grid adapter ------------------
    # demoGrid is an owner-drawn control; samples/InstrumentedDemo/DemoGridAdapter.cs teaches the bridge to read,
    # select and edit it. Its editor needs a realized grid, so its tab is selected first.
    r = s.act("select_control", "mainTabs", index=4)
    report.check("select the custom grid tab", r["state"] == "completed" and s.inspect("mainTabs")["selectedIndex"] == 4, r)
    demo = s.node("demoGrid")
    report.check("the demo grid advertises the adapter's capabilities and the context menu action",
                 demo.get("gridAdapter") == "demo-grid"
                 and all(c in demo.get("capabilities", []) for c in ("read_grid", "select", "scroll", "set_cell", "show_context_menu")), demo)

    def read_demo(**extra: Any) -> dict[str, Any]:
        node = s.node("demoGrid")
        return tools.result("read_grid", generation=node["generation"], id=node["id"], **extra)

    def cells(page: dict[str, Any], row: int) -> list[str]:
        return page["rows"][row]["cells"]

    def details_of(status: dict[str, Any]) -> dict[str, Any]:
        return (status.get("error") or {}).get("details") or {}

    page = read_demo()
    token = page.get("viewToken")
    columns = page.get("columns", [])
    report.check("read_grid on demoGrid returns the adapter name, a viewToken and the row and column counts",
                 page.get("gridAdapter") == "demo-grid" and isinstance(token, str) and re.fullmatch(r"[0-9a-f]{16}\.\d+", token) is not None
                 and page.get("totalRows") == 12 and page.get("totalColumns") == 3 and page.get("focusedRow") is None, page)
    report.check("read_grid columns carry editable flags and value kinds",
                 [c.get("editable") for c in columns] == [False, True, False]
                 and [c.get("valueKind") for c in columns] == ["text", "number", "number"], columns)
    report.check("read_grid columns carry the band path of the banded header",
                 [c.get("bandPath") for c in columns] == [None, ["Stock", "On hand"], ["Stock", "Held"]], columns)
    report.check("read_grid returns the display text of the rows", cells(page, 0) == ["Anvil", "30", "5"] and cells(page, 1) == ["Bolt", "10", "0"], page["rows"][:2])

    # The grid's ContextMenuStrip is reached through the grid. Its Delete item is enabled by the strip's Opening
    # handler, which runs only when the strip is shown: the show_context_menu action does that. Whether the strip then
    # stays visible depends on the window being active, so the observed state is reported, not required.
    grid_tree = tools.result("control_tree", generation=demo["generation"], id=demo["id"])
    strip = next((n for n in grid_tree["nodes"] if n.get("name") == "ctxGrid"), {})
    report.check("the grid's ContextMenuStrip is reached through the grid as a closed context_menu element",
                 strip.get("kind") == "item" and strip.get("role") == "context_menu"
                 and strip.get("parentId") == demo["id"] and strip.get("opened") is False, strip)
    items = subtree(strip)
    report.check("before the menu was ever shown, ctxGridDelete is disabled and ctxGridInfo is enabled",
                 items.get("ctxGridDelete", {}).get("enabled") is False and items.get("ctxGridInfo", {}).get("enabled") is True, items)
    r = act_on(s.node("demoGrid"), action="show_context_menu")
    observed = tools.result("inspect_control", generation=strip["generation"], id=strip["id"])
    items = subtree(strip)
    report.notes["gridContextMenuOpenedWithoutSelection"] = observed.get("opened")
    report.check("show_context_menu with no row selected completes, Delete stays disabled and the observed opened state is reported",
                 r["state"] == "completed" and isinstance(observed.get("opened"), bool)
                 and items.get("ctxGridDelete", {}).get("enabled") is False and items.get("ctxGridInfo", {}).get("enabled") is True,
                 [r, observed, items])
    if observed.get("opened") is True:
        act_on(strip, action="close")

    r = s.act("select_control", "demoGrid", row=3, column=1, viewToken=token)
    page = read_demo()
    report.check("select_control row and column completes and read_grid shows the focus and the one selected row",
                 r["state"] == "completed" and page.get("focusedRow") == 3 and page.get("focusedColumn") == 1
                 and [row.get("selected") for row in page["rows"]] == [i == 3 for i in range(12)] and page.get("viewToken") == token,
                 [r, page.get("focusedRow"), page.get("focusedColumn"), page.get("viewToken")])

    delete_before = subtree(strip).get("ctxGridDelete", {}).get("enabled")
    r = act_on(s.node("demoGrid"), action="show_context_menu")
    observed = tools.result("inspect_control", generation=strip["generation"], id=strip["id"])
    items = subtree(strip)
    report.notes["gridContextMenuOpenedWithSelection"] = observed.get("opened")
    report.check("after a row was selected, show_context_menu runs Opening: ctxGridDelete is enabled and ctxGridInfo still is",
                 delete_before is False and r["state"] == "completed" and isinstance(observed.get("opened"), bool)
                 and items.get("ctxGridDelete", {}).get("enabled") is True and items.get("ctxGridInfo", {}).get("enabled") is True,
                 [delete_before, r, observed, items])
    if observed.get("opened") is True:
        act_on(strip, action="close")

    r = s.act("set_grid_cell", "demoGrid", row=1, column=1, text="11", viewToken=token)
    page = read_demo()
    committed = r.get("result") or {}
    report.check("set_grid_cell on the editable column completes as committed; read_grid shows the new text and a new token",
                 r["state"] == "completed" and committed.get("committed") is True and committed.get("displayText") == "11"
                 and committed.get("rowAfter") == 1 and cells(page, 1)[1] == "11"
                 and page.get("viewToken") != token and page.get("viewToken") == committed.get("viewToken"), [r, page.get("viewToken"), token])
    token = page["viewToken"]

    refusals = [s.act("set_grid_cell", "demoGrid", row=1, column=column, text="x", viewToken=token) for column in (0, 2)]
    page = read_demo()
    report.check("the read-only columns give read_only and change nothing",
                 all(x["state"] == "failed" and x["error"]["code"] == "read_only" for x in refusals)
                 and cells(page, 1) == ["Bolt", "11", "0"] and page.get("viewToken") == token, [refusals, page["rows"][1]])

    r = s.act("set_grid_cell", "demoGrid", row=1, column=1, text="-5", viewToken=token)
    report.check("a negative quantity gives edit_rejected with the reason, the application's error text and the text still shown",
                 r["state"] == "failed" and r["error"]["code"] == "edit_rejected"
                 and details_of(r).get("reason") == "validation_failed"
                 and details_of(r).get("errorText") == "Quantity must not be negative." and details_of(r).get("displayText") == "11", r)
    r = s.act("set_grid_cell", "demoGrid", row=1, column=1, text="abc", viewToken=token)
    report.check("text in the number column gives edit_rejected with reason invalid_input",
                 r["state"] == "failed" and r["error"]["code"] == "edit_rejected" and details_of(r).get("reason") == "invalid_input"
                 and details_of(r).get("errorText") == "Quantity must be a whole number.", r)
    names = [w["name"] for w in tools.result("list_windows")["windows"]]
    page = read_demo()
    report.check("refused edits left no dialog open and no change (they were failed receipts, not message boxes)",
                 names == ["MainForm"] and cells(page, 1)[1] == "11" and page.get("viewToken") == token, [names, page["rows"][1]])

    target = s.node("demoGrid")
    receipt = tools.result("set_grid_cell", generation=target["generation"], id=target["id"], row=2, column=1, text="1500", viewToken=token)
    keep = s.wait_for_node("btnConfirmKeep")
    status = tools.result("action_status", generation=receipt["generation"], actionId=receipt["actionId"])
    names = [w["name"] for w in tools.result("list_windows")["windows"]]
    held = read_demo()
    report.check("a large quantity opens the confirm dialog: the receipt stays started, the dialog is listed and the value is held",
                 status["state"] == "started" and "dlgConfirmQuantity" in names and cells(held, 2)[1] == "20", [status, names, held["rows"][2]])
    s.wait(tools.result("invoke_control", generation=keep["generation"], id=keep["id"]))
    final = s.wait(receipt)
    page = read_demo()
    report.check("keeping the change through the bridge completes the receipt as committed; read_grid shows it with a new token",
                 final["state"] == "completed" and (final.get("result") or {}).get("committed") is True
                 and (final.get("result") or {}).get("displayText") == "1500" and cells(page, 2)[1] == "1500" and page.get("viewToken") != token,
                 [final, page["rows"][2]])
    token = page["viewToken"]

    target = s.node("demoGrid")
    receipt = tools.result("set_grid_cell", generation=target["generation"], id=target["id"], row=4, column=1, text="2000", viewToken=token)
    discard = s.wait_for_node("btnConfirmDiscard")
    s.wait(tools.result("invoke_control", generation=discard["generation"], id=discard["id"]))
    final = s.wait(receipt)
    page = read_demo()
    report.check("discarding the confirm dialog fails the receipt as edit_rejected and changes nothing",
                 final["state"] == "failed" and final["error"]["code"] == "edit_rejected"
                 and details_of(final).get("reason") == "validation_failed" and details_of(final).get("errorText") == "The change was not confirmed."
                 and details_of(final).get("displayText") == "45" and cells(page, 4)[1] == "45" and page.get("viewToken") == token,
                 [final, page["rows"][4]])

    stale_token = token
    r = s.act("invoke_control", "btnSortGrid")
    page = read_demo()
    report.check("sorting through the demo's button re-orders the rows and gives a new viewToken",
                 r["state"] == "completed" and cells(page, 0)[0] == "Lathe" and page.get("viewToken") != stale_token, [r, page["rows"][0], page.get("viewToken")])
    r = s.act("select_control", "demoGrid", row=0, column=1, viewToken=stale_token)
    report.check("a select with the token read before the sort fails as stale_view",
                 r["state"] == "failed" and r["error"]["code"] == "stale_view" and details_of(r).get("reason") == "view_changed", r)
    r = s.act("set_grid_cell", "demoGrid", row=0, column=1, text="9", viewToken=stale_token)
    report.check("an edit with the token read before the sort fails as stale_view and writes nothing",
                 r["state"] == "failed" and r["error"]["code"] == "stale_view" and cells(read_demo(), 0)[1] == "2", r)
    r = s.act("select_control", "demoGrid", row=0, column=1, viewToken=page["viewToken"])
    report.check("after read_grid again, the fresh token selects the first row of the new order",
                 r["state"] == "completed" and read_demo().get("focusedRow") == 0, r)

    # --- restart while MCP stays connected --------------------------------
    stale = replacement
    tools.result("app_stop")
    status = tools.result("app_status")
    report.check("app_stop closes the app and MCP stays up", status.get("bridgeAvailable") is False, status)
    is_error, parsed, _ = tools.call("list_windows")
    report.check("tools report app_unavailable after the app closed", is_error and error_code(parsed) == "app_unavailable", parsed)
    tools.result("app_launch")
    status = tools.result("app_status")
    report.check("relaunch produces a new generation", status.get("bridgeAvailable") and status.get("generation") != first_generation, status)
    is_error, parsed, _ = tools.call("inspect_control", generation=stale["generation"], id=stale["id"])
    report.check("an ID from the previous generation fails as stale_generation", is_error and error_code(parsed) == "stale_generation", parsed)
    tools.result("app_restart")
    s.refresh()
    report.check("app_restart yields a live tree again", "txtName" in s.nodes and s.generation != first_generation, s.generation)
    tools.result("app_stop")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=float, default=30.0)
    parser.add_argument("--captures", type=pathlib.Path, default=pathlib.Path("artifacts/validation/captures"))
    parser.add_argument("--report", type=pathlib.Path, default=pathlib.Path("artifacts/validation/exercise-report.json"))
    parser.add_argument("command", nargs=argparse.REMAINDER)
    arguments = parser.parse_args()
    command = arguments.command[1:] if arguments.command and arguments.command[0] == "--" else arguments.command
    if not command:
        parser.error("Supply the server command after --.")
    report = Report()
    client = smoke.Client(command, arguments.timeout)
    try:
        exercise(client, report, arguments.captures)
    except (smoke.SmokeFailure, OSError, ValueError, KeyError) as error:
        report.check("exercise ran to completion", False, repr(error))
        for line in list(client.stderr)[-20:]:
            print("stderr: " + line, file=sys.stderr)
    finally:
        # The sidecar deliberately does not kill its app when MCP closes. Stop the one this
        # script launched (a no-op when it is already stopped) so a failed run leaves nothing behind.
        try:
            Tools(client).call("app_stop", force=True)
        except Exception:
            pass
        client.abort()
    arguments.report.parent.mkdir(parents=True, exist_ok=True)
    arguments.report.write_text(json.dumps({"notes": report.notes, "checks": report.results}, indent=2))
    print(f"\n{len(report.results) - len(report.failed)} passed, {len(report.failed)} failed")
    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
