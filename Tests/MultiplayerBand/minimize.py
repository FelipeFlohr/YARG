"""Keeps the windows of native runs minimized without taking the focus (Windows only; does nothing elsewhere)."""
import os
import subprocess
import threading

SW_SHOWMINNOACTIVE = 7


class Minimizer:
    """Minimizes every top-level window of the processes it is given, each window once, so a window the user opens
    again on purpose stays open."""

    def __init__(self, interval=0.5):
        self._pids = set()
        self._done = set()
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._interval = interval
        if os.name == "nt":
            threading.Thread(target=self._loop, daemon=True).start()

    def popen_kwargs(self):
        if os.name != "nt":
            return {}
        # Asks the process to open its first window minimized; the loop covers programs that ignore it
        info = subprocess.STARTUPINFO()
        info.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        info.wShowWindow = SW_SHOWMINNOACTIVE
        return {"startupinfo": info}

    def add(self, proc):
        with self._lock:
            self._pids.add(proc.pid)

    def stop(self):
        self._stop.set()

    def _loop(self):
        import ctypes
        from ctypes import wintypes

        user32 = ctypes.windll.user32
        enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def visit(hwnd, _):
            pid = wintypes.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            with self._lock:
                wanted = pid.value in self._pids
            if wanted and hwnd not in self._done and user32.IsWindowVisible(hwnd) and \
                    user32.GetWindow(hwnd, 4) == 0:  # GW_OWNER: only top-level windows without an owner
                user32.ShowWindow(hwnd, SW_SHOWMINNOACTIVE)
                self._done.add(hwnd)
            return True

        callback = enum_proc(visit)
        while not self._stop.wait(self._interval):
            user32.EnumWindows(callback, 0)
