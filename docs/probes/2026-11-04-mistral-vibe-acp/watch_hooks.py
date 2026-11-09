"""Poll the Windows process table and print the ancestor chain of every kcap hook process seen."""
import ctypes, ctypes.wintypes as wt, sys, time

TH32CS_SNAPPROCESS = 0x2
class PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", ctypes.c_wchar * 260)]
k32 = ctypes.windll.kernel32

def snapshot():
    h = k32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    e = PROCESSENTRY32W(); e.dwSize = ctypes.sizeof(e)
    procs = {}
    ok = k32.Process32FirstW(h, ctypes.byref(e))
    while ok:
        procs[e.th32ProcessID] = (e.th32ParentProcessID, e.szExeFile)
        ok = k32.Process32NextW(h, ctypes.byref(e))
    k32.CloseHandle(h)
    return procs

deadline = time.time() + float(sys.argv[1])
seen = set()
while time.time() < deadline:
    procs = snapshot()
    for pid, (ppid, name) in procs.items():
        if name.lower() == "kcap.exe" and pid not in seen:
            seen.add(pid)
            chain, cur = [], pid
            for _ in range(10):
                if cur not in procs: chain.append(f"{cur}:<gone>"); break
                p, n = procs[cur]; chain.append(f"{cur}:{n}"); cur = p
            print(" <- ".join(chain), flush=True)
    time.sleep(0.02)
