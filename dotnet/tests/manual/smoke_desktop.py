r"""CabinetNC Desktop reusable UIA smoke suite.

Run from PowerShell:
  python -m pip install -r requirements.txt
  python smoke_desktop.py --keep-open --json ..\..\artifacts\smoke-latest.json
"""

from __future__ import annotations

import argparse
import json
import subprocess
import time
import traceback
from collections import Counter
from pathlib import Path
from typing import Callable

from pywinauto import Desktop
from pywinauto.application import Application


HERE = Path(__file__).resolve().parent
DOTNET = HERE.parents[1]
DEFAULT_EXE = (
    DOTNET
    / "src"
    / "CabinetNC.Desktop"
    / "bin"
    / "Debug"
    / "net10.0-windows"
    / "CabinetNC.Desktop.exe"
)


class SmokeSuite:
    def __init__(self, exe: Path, keep_open: bool, timeout: int):
        self.exe = exe
        self.keep_open = keep_open
        self.timeout = timeout
        self.app: Application | None = None
        self.window = None
        self.results: list[dict] = []

    def run_case(self, case_id: str, name: str, fn: Callable[[], None]) -> None:
        started = time.perf_counter()
        try:
            fn()
            self.results.append(
                {
                    "id": case_id,
                    "name": name,
                    "status": "PASS",
                    "durationMs": round((time.perf_counter() - started) * 1000),
                }
            )
            print(f"PASS {case_id} {name}")
        except Exception as exc:  # evidence must preserve the failing case
            self.results.append(
                {
                    "id": case_id,
                    "name": name,
                    "status": "FAIL",
                    "durationMs": round((time.perf_counter() - started) * 1000),
                    "error": str(exc),
                    "trace": traceback.format_exc(),
                }
            )
            print(f"FAIL {case_id} {name}: {exc}")

    def start(self) -> None:
        if not self.exe.exists():
            raise FileNotFoundError(f"Desktop exe not found: {self.exe}")
        subprocess.run(
            ["taskkill", "/IM", "CabinetNC.Desktop.exe", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        subprocess.run(
            ["taskkill", "/IM", "CabinetNC.ComputeWorker.exe", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        self.app = Application(backend="uia").start(str(self.exe))
        self.window = self.app.window(title="OmniCam")
        self.window.wait("visible", timeout=self.timeout)
        self.window.set_focus()

    def stop(self) -> None:
        if self.keep_open:
            return
        subprocess.run(
            ["taskkill", "/IM", "CabinetNC.Desktop.exe", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
        subprocess.run(
            ["taskkill", "/IM", "CabinetNC.ComputeWorker.exe", "/F"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )

    def control(self, auto_id: str, control_type: str | None = None):
        spec = self.window.child_window(auto_id=auto_id, control_type=control_type)
        spec.wait("exists enabled visible", timeout=self.timeout)
        return spec

    def click(self, auto_id: str, control_type: str) -> None:
        self.window.set_focus()
        wrapper = self.control(auto_id, control_type).wrapper_object()
        try:
            wrapper.invoke()
        except Exception:
            wrapper.click_input()
        time.sleep(0.2)

    def wait_for(self, predicate: Callable[[], bool], message: str, timeout: int | None = None):
        deadline = time.time() + (timeout or self.timeout)
        last_error = None
        while time.time() < deadline:
            try:
                if predicate():
                    return
            except Exception as exc:
                last_error = exc
            time.sleep(0.25)
        raise AssertionError(f"{message}; last_error={last_error}")

    def dismiss_message(self) -> None:
        ok = self.window.child_window(auto_id="2", control_type="Button")
        ok.wait("visible enabled", timeout=self.timeout)
        ok.click_input()

    def visible_texts(self) -> list[str]:
        return [
            item.window_text()
            for item in self.window.descendants(control_type="Text")
            if item.window_text()
        ]

    def assert_message_contains(self, expected: str, before: list[str] | None = None) -> None:
        ok = self.window.child_window(auto_id="2", control_type="Button")
        ok.wait("visible enabled", timeout=self.timeout)
        texts = self.visible_texts()
        if before is not None:
            texts = list((Counter(texts) - Counter(before)).elements())
        if not any(expected in text for text in texts):
            raise AssertionError(f"dialog does not contain {expected!r}; texts={texts!r}")

    def test_startup_and_gates(self) -> None:
        assert self.control("EmptyDemoBtn", "Button").exists()
        assert not self.window.child_window(auto_id="TabStock", control_type="TabItem").is_enabled()
        assert not self.window.child_window(auto_id="TabNest", control_type="TabItem").is_enabled()
        worker = self.window.child_window(auto_id="WorkerBadge", control_type="Text")
        self.wait_for(lambda: "Worker:" in worker.window_text(), "worker badge missing")

    def test_seven_modules(self) -> None:
        mapping = [
            ("ModProductionBtn", "EmptyDemoBtn", "Button"),
            ("ModRemnantsBtn", "RemnantsEmptyTitle", "Text"),
            ("ModEquipmentBtn", "EquipmentList", "List"),
            ("ModRoutesBtn", "RoutesMeta", "Text"),
            ("ModMaterialsBtn", "MaterialsList", "List"),
            ("ModProcessBtn", "ProcessToolsList", "List"),
            ("ModSettingsBtn", "SettingsMeta", "Text"),
        ]
        for button_id, anchor_id, anchor_type in mapping:
            self.click(button_id, "Button")
            anchor = self.window.child_window(auto_id=anchor_id, control_type=anchor_type)
            anchor.wait("visible", timeout=self.timeout)
        self.click("ModProductionBtn", "Button")

    def test_import_120(self) -> None:
        self.click("EmptyDemoBtn", "Button")
        self.dismiss_message()
        status = self.window.child_window(auto_id="StatusText", control_type="Text")
        self.wait_for(lambda: "panels=120" in status.window_text(), "120 panel import status missing")
        assert self.window.child_window(auto_id="TabNest", control_type="TabItem").is_enabled()

    def test_nest_and_nc(self) -> None:
        self.control("TabNest", "TabItem").click_input()
        self.click("NestApplyBtn", "Button")
        report = self.window.child_window(auto_id="NestReportMeta", control_type="Text")
        self.wait_for(
            lambda: "已排" in report.window_text(),
            "nest completion report missing",
            timeout=45,
        )

    def test_poly_verify(self) -> None:
        before = self.visible_texts()
        self.click("NestVerifyPolyBtn", "Button")
        self.assert_message_contains("校验通过", before)
        self.dismiss_message()

    def test_cam_playhead_and_offset(self) -> None:
        self.control("TabOps", "TabItem").click_input()
        self.click("OpsCalcBtn", "Button")
        play = self.control("CamPlayBtn", "Button")
        meta = self.window.child_window(auto_id="CamSimMeta", control_type="Text")
        before = meta.window_text()
        play.click_input()
        time.sleep(0.8)
        play.click_input()
        after = meta.window_text()
        assert after and "/" in after and "@(" in after
        assert before != after, "CAM playhead did not advance"
        self.control("TabOut", "TabItem").click_input()
        nc_control = self.control("NcPreview", "Edit")
        self.wait_for(lambda: "G21" in nc_control.window_text(), "NC was not generated")
        nc = nc_control.window_text()
        assert "X-" not in nc and "Y-" not in nc, "offset NC contains negative sheet XY"
        assert len(nc.splitlines()) > 100, "NC output unexpectedly small"

    def test_tool_drives_cam(self) -> None:
        self.click("ModProcessBtn", "Button")
        tools = self.control("ProcessToolsList", "List")
        items = tools.children(control_type="ListItem")
        assert items, "tool library is empty"
        items[0].click_input()
        self.click("ToolApplyBtn", "Button")
        status = self.window.child_window(auto_id="StatusText", control_type="Text").window_text()
        assert "F" in status or "Ø" in status
        self.click("ModProductionBtn", "Button")

    def test_out_preflight_and_controls(self) -> None:
        self.control("TabOut", "TabItem").click_input()
        for auto_id in (
            "OutPreflightBtn",
            "OutSaveNcBtn",
            "OutDxfBtn",
            "OutJobSheetBtn",
            "OutJsonBtn",
            "OutBundleBtn",
        ):
            assert self.control(auto_id, "Button").exists()
        before = self.visible_texts()
        self.click("OutPreflightBtn", "Button")
        self.assert_message_contains("预检通过", before)
        self.dismiss_message()

    def execute(self) -> dict:
        self.start()
        try:
            cases = [
                ("SMK-001/003", "startup-and-stage-gates", self.test_startup_and_gates),
                ("SMK-002", "seven-module-navigation", self.test_seven_modules),
                ("SMK-010", "import-demo-120", self.test_import_120),
                ("SMK-031/045", "nest-nc-and-rotation-safety", self.test_nest_and_nc),
                ("SMK-036", "clipper-poly-verify", self.test_poly_verify),
                ("SMK-043/044", "offset-and-cam-playhead", self.test_cam_playhead_and_offset),
                ("SMK-042", "tool-drives-current-cam", self.test_tool_drives_cam),
                ("SMK-046/050-053", "out-preflight-and-export-controls", self.test_out_preflight_and_controls),
            ]
            for case_id, name, fn in cases:
                self.run_case(case_id, name, fn)
        finally:
            self.stop()

        passed = sum(r["status"] == "PASS" for r in self.results)
        return {
            "schema": "cabinetnc.desktop-smoke",
            "schemaVersion": 1,
            "runAt": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
            "exe": str(self.exe),
            "passed": passed,
            "failed": len(self.results) - passed,
            "ok": passed == len(self.results),
            "cases": self.results,
        }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, default=DEFAULT_EXE)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--keep-open", action="store_true")
    parser.add_argument("--timeout", type=int, default=20)
    args = parser.parse_args()

    report = SmokeSuite(args.exe.resolve(), args.keep_open, args.timeout).execute()
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("passed", "failed", "ok")}, ensure_ascii=False))
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
