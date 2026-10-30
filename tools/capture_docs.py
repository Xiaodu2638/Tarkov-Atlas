# -*- coding: utf-8 -*-
r"""生成 docs/ 界面截图（main / quests / settings / overlay）。

用法:
    python tools/capture_docs.py             # 全部四张
    python tools/capture_docs.py main        # 只重拍一张

前置：先跑 build.ps1，脚本会截 dist\TarkovAtlas-win-x64 里的产物。

踩过的坑（都体现在实现里）：
* 进程 PerMonitorV2 DPI aware —— 窗口坐标是物理像素，截图必须按窗口矩形裁剪；
* 无边框窗口的 GetWindowRect 含 DWM 投影，要用 DWMWA_EXTENDED_FRAME_BOUNDS；
* 鼠标停在地图上会显示坐标浮层，截图前必须把光标挪到屏幕角落；
* 抽屉展开、地图复位都是异步布局，等待不足会截到半帧；
* 按钮用 UIA InvokePattern 触发（按 AutomationId），比硬编码坐标可靠；
* 悬浮窗 Topmost 会盖住主界面，所以先拍它再收起；
* 编辑器/终端会抢前台，每次截图前重新激活目标窗口。
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import json
import os
import pathlib
import subprocess
import sys
import time

# 必须在任何 UIA 调用前设置，否则拿到的矩形是逻辑像素
ctypes.windll.shcore.SetProcessDpiAwareness(2)

ROOT = pathlib.Path(__file__).resolve().parents[1]
for cand in (ROOT / "tools" / "pyext", ROOT / "pyext"):
    if cand.is_dir():
        sys.path.insert(0, str(cand))
from PIL import ImageGrab  # noqa: E402

# 默认截 dist 产物。发布版内嵌 requireAdministrator，非交互 shell 里会被 UAC
# 挡住（WinError 740）；要脚本化验证就用非提权构建：
#   dotnet publish ... -p:ApplicationManifest= --no-incremental -o .tmp/verify/app
#   TARKOVATLAS_EXE_DIR=.tmp/verify/app python tools/capture_docs.py
EXE_DIR = pathlib.Path(os.environ.get("TARKOVATLAS_EXE_DIR")
                       or ROOT / "dist" / "TarkovAtlas-win-x64")
EXE = EXE_DIR / "TarkovAtlas.exe"
DOCS = ROOT / "docs"

user32 = ctypes.windll.user32
kernel32 = ctypes.windll.kernel32
dwmapi = ctypes.windll.dwmapi

MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000
VK_HOME = 0x24
VK_CTRL, VK_ALT, VK_M = 0x11, 0x12, 0x4D
SW_RESTORE = 9
DWMWA_EXTENDED_FRAME_BOUNDS = 9

# 截主界面时用这张地图：信息密度适中（撤离点 / Boss / 物资都画得出来）
MAIN_MAP = "customs"

PS_HEAD = (
    "Add-Type -AssemblyName UIAutomationClient\n"
    "Add-Type -AssemblyName UIAutomationTypes\n"
    "$p = Get-Process TarkovAtlas\n"
    "$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)\n"
    "$prop = [System.Windows.Automation.AutomationElement]::AutomationIdProperty\n"
)


# --------------------------------------------------------------------------- input


def move_abs(x: int, y: int) -> None:
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)
    user32.mouse_event(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                       int(x * 65535 / max(1, sw - 1)),
                       int(y * 65535 / max(1, sh - 1)), 0, 0)


def park_mouse() -> None:
    """地图上的坐标浮层跟随鼠标，截图前把它挪出窗口。"""
    move_abs(2, 2)


def key(vk: int) -> None:
    user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.06)
    user32.keybd_event(vk, 0, 2, 0)
    time.sleep(0.2)


def hotkey(*vks: int) -> None:
    for vk in vks:
        user32.keybd_event(vk, 0, 0, 0)
        time.sleep(0.06)
    for vk in reversed(vks):
        user32.keybd_event(vk, 0, 2, 0)
        time.sleep(0.06)
    time.sleep(0.8)


# --------------------------------------------------------------------------- UIA


def ps(body: str) -> str:
    r = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
         "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n" + PS_HEAD + body],
        capture_output=True, text=True, encoding="utf-8", errors="replace")
    return (r.stdout or "").strip()


def invoke(id_: str) -> bool:
    out = ps(
        '$c = New-Object System.Windows.Automation.PropertyCondition($prop, "%s")\n'
        "$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)\n"
        'if ($el -eq $null) { Write-Output "MISSING"; exit }\n'
        "try {\n"
        "  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()\n"
        '  Write-Output "OK"\n'
        '} catch { Write-Output ("ERR " + $_.Exception.Message) }\n' % id_)
    print(f"  invoke {id_}: {out}")
    return out == "OK"


# --------------------------------------------------------------------------- windows


def force_front(h: int) -> None:
    fg = user32.GetForegroundWindow()
    tid_fg = user32.GetWindowThreadProcessId(fg, None)
    tid_me = kernel32.GetCurrentThreadId()
    attached = bool(fg) and tid_fg != tid_me
    if attached:
        user32.AttachThreadInput(tid_fg, tid_me, True)
    if user32.IsIconic(h):
        user32.ShowWindow(h, SW_RESTORE)
    user32.BringWindowToTop(h)
    user32.SetForegroundWindow(h)
    if attached:
        user32.AttachThreadInput(tid_fg, tid_me, False)
    time.sleep(0.8)


def frame_box(h: int) -> tuple[int, int, int, int]:
    """不含 DWM 投影的窗口矩形。"""
    r = wt.RECT()
    if dwmapi.DwmGetWindowAttribute(wt.HWND(h), ctypes.c_uint(DWMWA_EXTENDED_FRAME_BOUNDS),
                                    ctypes.byref(r), ctypes.sizeof(r)) != 0:
        user32.GetWindowRect(h, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def list_windows(fragment: str) -> list[tuple[int, str]]:
    found: list[tuple[int, str]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)
    def cb(hwnd, lp):
        if not user32.IsWindowVisible(hwnd):
            return True
        n = user32.GetWindowTextLengthW(hwnd)
        if not n:
            return True
        b = ctypes.create_unicode_buffer(n + 1)
        user32.GetWindowTextW(hwnd, b, n + 1)
        if fragment in b.value:
            found.append((hwnd, b.value))
        return True

    user32.EnumWindows(cb, 0)
    return found


def find_win(title: str) -> int:
    return user32.FindWindowW(None, title)


def wait_win(title: str, timeout: float = 25.0) -> int:
    end = time.time() + timeout
    while time.time() < end:
        h = find_win(title)
        if h:
            return h
        time.sleep(0.3)
    return 0


def wait_win_exact(title: str, timeout: float = 15.0) -> int:
    end = time.time() + timeout
    while time.time() < end:
        for hwnd, text in list_windows(title):
            if text == title:
                return hwnd
        time.sleep(0.3)
    return 0


def wait_dialog(fragment: str, timeout: float = 12.0) -> int:
    """对话框标题带应用名前缀（「Tarkov Atlas 设置」），按片段匹配。"""
    end = time.time() + timeout
    while time.time() < end:
        for hwnd, text in list_windows(fragment):
            if text != "Tarkov Atlas":
                return hwnd
        time.sleep(0.3)
    return 0


def client_box(h: int) -> tuple[int, int, int, int]:
    """客户区矩形。无边框窗口的 frame_box 含透明投影边，会透出后面的窗口。"""
    cr = wt.RECT()
    user32.GetClientRect(h, ctypes.byref(cr))
    p = wt.POINT(0, 0)
    user32.ClientToScreen(h, ctypes.byref(p))
    return p.x, p.y, p.x + cr.right, p.y + cr.bottom


def grab(h: int, out: pathlib.Path, settle: float = 2.2,
         box_fn=frame_box) -> None:
    force_front(h)
    park_mouse()
    time.sleep(settle)
    l, t, r, b = box_fn(h)
    out.parent.mkdir(parents=True, exist_ok=True)
    ImageGrab.grab(bbox=(l, t, r, b), all_screens=True).save(out)
    try:
        shown = out.relative_to(ROOT)
    except ValueError:
        shown = out
    print(f"  {shown}  {r - l}x{b - t}")


# --------------------------------------------------------------------------- driver


def seed_settings() -> None:
    """把应用状态调成适合截图的样子，并清掉一次性的截图目录。"""
    path = pathlib.Path(os.environ.get("APPDATA", "")) / "TarkovAtlas" / "settings.json"
    data = {}
    if path.is_file():
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            data = {}
    data["selectedMap"] = MAIN_MAP
    data["overlayVisibleOnStartup"] = True
    data["deleteScreenshotsAfterScan"] = False
    data["scanOnStartup"] = False
    data["autoSwitchMap"] = False    # 否则游戏日志会把当前地图自动切走
    data["autoDetect"] = False
    # 悬浮窗是透明窗，方形窗口里宽幅地图上下会留透明带。把它摆到主窗口
    # 地图画布的黑色区域（而不是标题栏/桌面上），透明带就隐入黑底，截图干净。
    data["overlayScreenX"] = 1450
    data["overlayScreenY"] = 850
    data.pop("overlayScreenX", None)
    data.pop("overlayScreenY", None)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def restart() -> int:
    if not EXE.is_file():
        raise SystemExit(f"未找到 {EXE}，先跑 build.ps1")
    # 先杀掉残留实例：它会把当前地图周期性写回 settings.json，盖掉 seed 的值
    subprocess.run(["taskkill", "/IM", "TarkovAtlas.exe", "/F"], capture_output=True)
    time.sleep(1.5)
    seed_settings()
    subprocess.Popen([str(EXE)], cwd=str(EXE_DIR), env=os.environ.copy())
    h = wait_win("Tarkov Atlas")
    if not h:
        raise SystemExit("主窗口未出现")
    time.sleep(6.0)          # 首帧：底图解码 + 标记生成
    force_front(h)
    return h


def invoke_by_name(name: str) -> bool:
    """按显示文字触发按钮（复位等按钮没有 AutomationId）。

    注意：不要用键盘 HOME 复位 —— 刚点完地图列表时焦点在 ListBox 上，
    HOME 会把选中项挪到第一项，等于换了一张地图。
    """
    # 用拼接而不是 str.format：脚本文本里全是 PowerShell 的 {} 花括号
    out = ps(
        "$c = New-Object System.Windows.Automation.PropertyCondition(\n"
        "  [System.Windows.Automation.AutomationElement]::NameProperty, \"" + name + "\")\n"
        "$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)\n"
        "if ($els.Count -eq 0) { Write-Output 'MISSING'; exit }\n"
        "foreach ($el in $els) {\n"
        "  try {\n"
        "    $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()\n"
        "    Write-Output 'OK'; exit\n"
        "  } catch { }\n"
        "}\n"
        "Write-Output 'NOPATTERN'\n")
    print(f"  invoke 「{name}」: {out}")
    return out == "OK"


def select_map(display: str) -> bool:
    """在地图列表里点选指定地图。

    不能用 UIA 的 SelectionItemPattern.Select()：列表是虚拟化的，它对已实现行
    有 off-by-one（点「海关」实际选中了「中心区」）。改为取行的屏幕矩形，再发
    一次真实鼠标点击。
    """
    rect = ps(
        "$c = New-Object System.Windows.Automation.PropertyCondition($prop, \"MapList\")\n"
        "$list = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)\n"
        "if ($list -eq $null) { Write-Output 'NOLIST'; exit }\n"
        "$li = New-Object System.Windows.Automation.PropertyCondition(\n"
        "  [System.Windows.Automation.AutomationElement]::ControlTypeProperty,\n"
        "  [System.Windows.Automation.ControlType]::ListItem)\n"
        "$items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $li)\n"
        "foreach ($it in $items) {\n"
        "  $texts = $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,\n"
        "    (New-Object System.Windows.Automation.PropertyCondition(\n"
        f"      [System.Windows.Automation.AutomationElement]::NameProperty, '{display}')))\n"
        "  if ($texts.Count -gt 0) {\n"
        "    $r = $it.Current.BoundingRectangle\n"
        "    Write-Output ('{0},{1},{2},{3}' -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)\n"
        "    exit\n"
        "  }\n"
        "}\n"
        "Write-Output 'MISSING'\n")
    if "," not in rect:
        print(f"  select {display}: {rect}")
        return False
    x, y, w, hh = (int(v) for v in rect.split(","))
    move_abs(x + w // 2, y + hh // 2)
    time.sleep(0.3)
    user32.mouse_event(0x0002, 0, 0, 0, 0)
    time.sleep(0.09)
    user32.mouse_event(0x0004, 0, 0, 0, 0)
    print(f"  select {display}: clicked {x + w // 2},{y + hh // 2}")
    return True


def current_map_title() -> str:
    return ps(
        "$c = New-Object System.Windows.Automation.PropertyCondition($prop, \"MapTitle\")\n"
        "$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)\n"
        "if ($el -eq $null) { Write-Output 'NOTITLE'; exit }\n"
        "Write-Output $el.Current.Name\n")


def capture_overlay() -> None:
    """悬浮窗是无边框 Topmost 小窗，得趁它还在的时候单独拍。"""
    h = wait_win_exact("Tarkov Atlas Overlay", 8)
    if not h:
        invoke("TitleOverlayButton")
        h = wait_win_exact("Tarkov Atlas Overlay", 8)
    if not h:
        print("  悬浮窗未出现，跳过")
        return
    # 标题栏已不存在（无边框透明窗）：用客户区矩形，避开透明投影透出的背景
    l, t, r, b = client_box(h)
    move_abs((l + r) // 2, t + 12)
    time.sleep(0.6)
    move_abs((l + r) // 2 + 1, t + 13)   # 再动一下，避免悬停计时器提前停摆
    time.sleep(2.0)
    ImageGrab.grab(bbox=(l, t, r, b), all_screens=True).save(DOCS / "overlay.png")
    print(f"  {DOCS / 'overlay.png'}  {r - l}x{b - t}")
    move_abs(2, 2)


def hide_overlay() -> None:
    for hwnd, text in list_windows("Tarkov Atlas Overlay"):
        if text == "Tarkov Atlas Overlay":
            user32.ShowWindow(hwnd, 0)   # SW_HIDE
            time.sleep(0.8)
            return
    hotkey(VK_CTRL, VK_ALT, VK_M)
    time.sleep(0.8)


def main() -> None:
    steps = sys.argv[1:] or ["main", "quests", "settings", "overlay"]
    h = restart()

    if "overlay" in steps:
        # 悬浮窗的地图跟随主窗口：森林接近正方形，能铺满方形透明窗，
        # 不会在上下留出透出桌面的透明带
        select_map("森林")
        time.sleep(1.5)
        capture_overlay()

    if set(steps) & {"main", "quests", "settings"}:
        hide_overlay()

    if "main" in steps:
        select_map("海关")             # 保险：seed 万一没生效，这里显式点选
        time.sleep(1.5)
        invoke_by_name("复位")         # 别用键盘 HOME：焦点在列表上时会跳回第一项
        time.sleep(2.0)
        title = current_map_title()
        print(f"  current map: {title}")
        if "海关" not in title:
            raise SystemExit(f"切图失败，当前是「{title}」")
        grab(h, DOCS / "main.png")

    if "quests" in steps:
        invoke("QuestToggleButton")
        time.sleep(2.5)
        grab(h, DOCS / "quests.png")
        invoke("QuestToggleButton")      # 收起，别影响后续
        time.sleep(1.0)

    if "settings" in steps:
        invoke("TitleSettingsButton")
        h2 = wait_dialog("设置", 12)
        if h2:
            grab(h2, DOCS / "settings.png", box_fn=client_box)
            user32.PostMessageW(h2, 0x0010, 0, 0)   # WM_CLOSE：不保存
            time.sleep(1.0)
        else:
            print("  设置窗口未出现")

    subprocess.run(["taskkill", "/IM", "TarkovAtlas.exe", "/F"], capture_output=True)
    print("done")


if __name__ == "__main__":
    main()
