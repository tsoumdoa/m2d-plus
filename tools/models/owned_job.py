"""Windows Job Object supervisor. Only processes assigned to this job are inspected/killed.

The child waits on stdin before loading Rhino; assignment and caps happen first.
Closing the job terminates descendants even if the venv launcher exits first.
"""
import ctypes as C
from ctypes import wintypes as W
import os
import json
from pathlib import Path
import subprocess
import time


class IO(C.Structure):
    _fields_ = [(n, C.c_ulonglong) for n in ('ReadOperationCount', 'WriteOperationCount', 'OtherOperationCount',
                                           'ReadTransferCount', 'WriteTransferCount', 'OtherTransferCount')]


class Basic(C.Structure):
    _fields_ = [('PerProcessUserTimeLimit', C.c_longlong), ('PerJobUserTimeLimit', C.c_longlong),
                ('LimitFlags', W.DWORD), ('MinimumWorkingSetSize', C.c_size_t), ('MaximumWorkingSetSize', C.c_size_t),
                ('ActiveProcessLimit', W.DWORD), ('Affinity', C.c_size_t), ('PriorityClass', W.DWORD), ('SchedulingClass', W.DWORD)]


class Extended(C.Structure):
    _fields_ = [('BasicLimitInformation', Basic), ('IoInfo', IO), ('ProcessMemoryLimit', C.c_size_t),
                ('JobMemoryLimit', C.c_size_t), ('PeakProcessMemoryUsed', C.c_size_t), ('PeakJobMemoryUsed', C.c_size_t)]


class Memory(C.Structure):
    _fields_ = [('cb', W.DWORD), ('PageFaultCount', W.DWORD)] + [(n, C.c_size_t) for n in
               ('PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage',
                'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]


def run(command, log_path, seconds, gib, admission_path=None, admission_timeout=3600):
    if os.name != 'nt':
        raise RuntimeError('This licensed local harness requires Windows')
    k = C.WinDLL('kernel32', use_last_error=True)
    signatures = {'CreateJobObjectW': ([C.c_void_p, W.LPCWSTR], W.HANDLE),
                  'SetInformationJobObject': ([W.HANDLE, C.c_int, C.c_void_p, W.DWORD], W.BOOL),
                  'QueryInformationJobObject': ([W.HANDLE, C.c_int, C.c_void_p, W.DWORD, C.c_void_p], W.BOOL),
                  'AssignProcessToJobObject': ([W.HANDLE, W.HANDLE], W.BOOL),
                  'TerminateJobObject': ([W.HANDLE, W.UINT], W.BOOL),
                  'OpenProcess': ([W.DWORD, W.BOOL, W.DWORD], W.HANDLE), 'CloseHandle': ([W.HANDLE], W.BOOL)}
    for name, (args, result) in signatures.items():
        fn = getattr(k, name); fn.argtypes, fn.restype = args, result
    ps = C.WinDLL('psapi', use_last_error=True).GetProcessMemoryInfo
    ps.argtypes, ps.restype = [W.HANDLE, C.POINTER(Memory), W.DWORD], W.BOOL
    resume = C.WinDLL('ntdll').NtResumeProcess
    resume.argtypes, resume.restype = [W.HANDLE], W.LONG
    job = k.CreateJobObjectW(None, None)
    if not job:
        raise C.WinError(C.get_last_error())
    limits = Extended()
    limits.BasicLimitInformation.LimitFlags = 0x2000 | 0x200  # KILL_ON_JOB_CLOSE | JOB_MEMORY
    limits.JobMemoryLimit = int(gib * 1024 ** 3)
    if not k.SetInformationJobObject(job, 9, C.byref(limits), C.sizeof(limits)):
        k.CloseHandle(job)
        raise C.WinError(C.get_last_error())
    started = time.perf_counter()
    admission_path = Path(admission_path) if admission_path is not None else None
    if admission_path is not None:
        admission_path.unlink(missing_ok=True)
    execution_started = None if admission_path is not None else started
    peak = 0; samples = []; reason = None; process = None
    try:
        with open(log_path, 'w', encoding='utf-8') as log:
            process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=log, stderr=subprocess.STDOUT,
                                       env=dict(os.environ, PYTHONDONTWRITEBYTECODE='1'),
                                       creationflags=subprocess.CREATE_NO_WINDOW | 0x4)  # CREATE_SUSPENDED
            if not k.AssignProcessToJobObject(job, W.HANDLE(int(process._handle))):
                # Rhino has not initialized; this is only the process we just created.
                process.kill(); process.wait()
                raise C.WinError(C.get_last_error())
            # Assign before any launcher code can run or create descendants.
            # Popen closes the primary thread handle, so resume the owned process.
            status = resume(W.HANDLE(int(process._handle)))
            if status < 0:
                raise OSError('NtResumeProcess failed: NTSTATUS ' + hex(status & 0xffffffff))
            process.stdin.write(b'go\n'); process.stdin.flush(); process.stdin.close()
            while True:
                # JOBOBJECT_BASIC_PROCESS_ID_LIST; all IDs belong to our job, never a name search.
                buf = C.create_string_buffer(8 + 1024 * C.sizeof(C.c_size_t))
                if not k.QueryInformationJobObject(job, 3, buf, C.sizeof(buf), None):
                    raise C.WinError(C.get_last_error())
                count = C.c_uint32.from_buffer(buf, 4).value
                pids = [C.c_size_t.from_buffer(buf, 8 + i * C.sizeof(C.c_size_t)).value for i in range(count)]
                private = 0
                for pid in pids:
                    handle = k.OpenProcess(0x1000 | 0x10, False, pid)
                    if handle:
                        try:
                            mem = Memory(); mem.cb = C.sizeof(mem)
                            if ps(handle, C.byref(mem), mem.cb):
                                private += mem.PrivateUsage
                        finally:
                            k.CloseHandle(handle)
                peak = max(peak, private)
                now = time.perf_counter()
                elapsed = now - started
                if execution_started is None and admission_path.exists():
                    admission = json.loads(admission_path.read_text(encoding='utf-8'))
                    if admission.get('status') == 'admitted':
                        # Windows perf_counter uses the system QPC across processes.
                        # Clamp the worker timestamp to this supervisor's lifetime.
                        execution_started = min(now, max(started, float(admission['admittedPerfCounter'])))
                if not samples or elapsed - samples[-1]['seconds'] >= 1:
                    samples.append({'seconds': round(elapsed, 3), 'privateBytes': private,
                                    'phase': 'execution' if execution_started is not None else 'admission'})
                if not pids:
                    break
                if execution_started is None and elapsed > admission_timeout:
                    reason = 'admission_timeout'
                if execution_started is not None and now - execution_started > seconds:
                    reason = 'wall_timeout'
                if private >= limits.JobMemoryLimit * .98:
                    reason = 'private_memory_limit'
                if reason:
                    k.TerminateJobObject(job, 124)
                    process.wait(timeout=15)
                    break
                time.sleep(.2)
            process.wait(timeout=15)
            measured = Extended()
            k.QueryInformationJobObject(job, 9, C.byref(measured), C.sizeof(measured), None)
            elapsed = time.perf_counter() - started
            admission_seconds = (execution_started - started) if execution_started is not None else elapsed
            return {'status': reason or ('exited' if process.returncode == 0 and execution_started is not None else 'worker_crash'),
                    'exitCode': process.returncode, 'ownedRootPid': process.pid,
                    'wallSeconds': elapsed - admission_seconds, 'elapsedSeconds': elapsed,
                    'admissionSeconds': admission_seconds, 'peakPrivateBytes': peak,
                    'jobPeakMemoryBytes': measured.PeakJobMemoryUsed,
                    'limits': {'wallSeconds': seconds, 'privateGiB': gib}, 'samples': samples,
                    'memoryMethod': 'sum of job-member PrivateUsage sampled every 200 ms; Windows hard job memory cap'}
    finally:
        k.CloseHandle(job)
        if process and process.poll() is None:
            process.wait(timeout=15)
