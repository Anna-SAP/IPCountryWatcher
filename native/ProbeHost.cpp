#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>
#include "ProbeProtocol.h"

static int Fail(DWORD error, const char* stage) {
    printf("{\"error\":%lu,\"stage\":\"%s\"}\n", error, stage);
    return 1;
}
static bool SameUser(HANDLE target) {
    HANDLE ownToken = NULL, targetToken = NULL;
    BYTE ownInfo[512], targetInfo[512]; DWORD length = 0;
    bool same = OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &ownToken) &&
        OpenProcessToken(target, TOKEN_QUERY, &targetToken) &&
        GetTokenInformation(ownToken, TokenUser, ownInfo, sizeof(ownInfo), &length) &&
        GetTokenInformation(targetToken, TokenUser, targetInfo, sizeof(targetInfo), &length) &&
        EqualSid(reinterpret_cast<TOKEN_USER*>(ownInfo)->User.Sid, reinterpret_cast<TOKEN_USER*>(targetInfo)->User.Sid);
    if (ownToken) CloseHandle(ownToken);
    if (targetToken) CloseHandle(targetToken);
    return same;
}
static HMODULE RemoteModule(DWORD pid, const wchar_t* name, bool fullPath) {
    for (int retry = 0; retry < 4; ++retry) {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
        if (snapshot == INVALID_HANDLE_VALUE) { if (GetLastError() == ERROR_BAD_LENGTH) continue; return NULL; }
        MODULEENTRY32W entry = {}; entry.dwSize = sizeof(entry);
        HMODULE found = NULL;
        if (Module32FirstW(snapshot, &entry)) do {
            if (_wcsicmp(fullPath ? entry.szExePath : entry.szModule, name) == 0) { found = entry.hModule; break; }
        } while (Module32NextW(snapshot, &entry));
        CloseHandle(snapshot);
        return found;
    }
    return NULL;
}
static LPTHREAD_START_ROUTINE RemoteSystemFunction(DWORD pid, const char* name) {
    FARPROC function = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), name);
    HMODULE owner = NULL;
    if (!function || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
        reinterpret_cast<LPCWSTR>(function), &owner)) return NULL;
    wchar_t filename[MAX_PATH];
    if (!GetModuleFileNameW(owner, filename, MAX_PATH)) return NULL;
    wchar_t* leaf = wcsrchr(filename, L'\\');
    HMODULE remote = RemoteModule(pid, leaf ? leaf + 1 : filename, false);
    if (!remote) return NULL;
    return reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<BYTE*>(remote) +
        (reinterpret_cast<BYTE*>(function) - reinterpret_cast<BYTE*>(owner)));
}
static DWORD RunThread(HANDLE process, LPTHREAD_START_ROUTINE entry, void* parameter, DWORD timeout) {
    if (!entry) return ERROR_PROC_NOT_FOUND;
    HANDLE thread = CreateRemoteThread(process, NULL, 0, entry, parameter, 0, NULL);
    if (!thread) return GetLastError();
    DWORD wait = WaitForSingleObject(thread, timeout);
    DWORD code = 0;
    if (wait != WAIT_OBJECT_0) code = ERROR_TIMEOUT;
    CloseHandle(thread);
    return code;
}

int wmain(int argc, wchar_t** argv) {
    // Only the adjacent, bundled probe DLL is loadable; no arbitrary DLL or endpoint argument.
    if (argc != 6) return Fail(ERROR_INVALID_PARAMETER, "arguments");
    wchar_t* end = NULL;
    unsigned long number = wcstoul(argv[1], &end, 10);
    if (!number || !end || *end) return Fail(ERROR_INVALID_PARAMETER, "pid");
    DWORD pid = number;
    unsigned __int64 created = _wcstoui64(argv[2], &end, 10);
    if (!created || !end || *end) return Fail(ERROR_INVALID_PARAMETER, "created");
    DWORD mode = wcstoul(argv[3], &end, 10);
    if (!end || *end || mode > 1) return Fail(ERROR_INVALID_PARAMETER, "mode");
    DWORD operation = wcstoul(argv[5], &end, 10);
    if (!end || *end || operation > 1) return Fail(ERROR_INVALID_PARAMETER, "operation");
    DWORD ownSession = 0, targetSession = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &ownSession) || !ProcessIdToSessionId(pid, &targetSession) ||
        ownSession != targetSession) return Fail(ERROR_ACCESS_DENIED, "session");
    HANDLE process = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
        PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid);
    if (!process) return Fail(GetLastError(), "open");
    FILETIME birth, exit, kernel, user;
    wchar_t actual[32768]; DWORD actualSize = 32768;
    if (!SameUser(process)) { CloseHandle(process); return Fail(ERROR_ACCESS_DENIED, "owner"); }
    if (!GetProcessTimes(process, &birth, &exit, &kernel, &user)) {
        DWORD error = GetLastError(); CloseHandle(process); return Fail(error, "created-read");
    }
    if (created != ((static_cast<unsigned __int64>(birth.dwHighDateTime) << 32) | birth.dwLowDateTime)) {
        CloseHandle(process); return Fail(ERROR_INVALID_PARAMETER, "created-mismatch");
    }
    if (!QueryFullProcessImageNameW(process, 0, actual, &actualSize)) {
        DWORD error = GetLastError(); CloseHandle(process); return Fail(error, "image-query");
    }
    if (_wcsicmp(actual, argv[4]) != 0) { CloseHandle(process); return Fail(ERROR_ACCESS_DENIED, "image-mismatch"); }
    wchar_t dll[MAX_PATH];
    DWORD size = GetModuleFileNameW(NULL, dll, MAX_PATH);
    wchar_t* leaf = wcsrchr(dll, L'\\');
    if (!size || size >= MAX_PATH || !leaf) { CloseHandle(process); return Fail(ERROR_BAD_PATHNAME, "path"); }
#ifdef _WIN64
    const wchar_t* dllName = L"IPCountryWatcher.Probe.x64.dll";
#else
    const wchar_t* dllName = L"IPCountryWatcher.Probe.x86.dll";
#endif
    if (wcscpy_s(leaf + 1, MAX_PATH - static_cast<size_t>(leaf + 1 - dll), dllName) != 0) {
        CloseHandle(process); return Fail(ERROR_BAD_PATHNAME, "path");
    }
    // A prior timed-out thread may still own the DLL. Never accumulate another probe in this process.
    if (RemoteModule(pid, dll, true)) { CloseHandle(process); return Fail(ERROR_BUSY, "already-loaded"); }
    HMODULE local = LoadLibraryExW(dll, NULL, DONT_RESOLVE_DLL_REFERENCES);
    FARPROC exported = local ? GetProcAddress(local, "ProbeRun") : NULL;
    if (!exported) { if (local) FreeLibrary(local); CloseHandle(process); return Fail(ERROR_MOD_NOT_FOUND, "payload"); }
    SIZE_T offset = reinterpret_cast<BYTE*>(exported) - reinterpret_cast<BYTE*>(local);
    FreeLibrary(local);
    SIZE_T bytes = (wcslen(dll) + 1) * sizeof(wchar_t), written = 0;
    void* remotePath = VirtualAllocEx(process, NULL, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remotePath) { CloseHandle(process); return Fail(GetLastError(), "allocate"); }
    DWORD error = 0;
    if (!WriteProcessMemory(process, remotePath, dll, bytes, &written) || written != bytes) error = ERROR_WRITE_FAULT;
    if (!error) error = RunThread(process, RemoteSystemFunction(pid, "LoadLibraryW"), remotePath, 10000);
    // A timeout must leave remote memory valid. Never terminate a thread inside someone else's application.
    if (error == ERROR_TIMEOUT) { CloseHandle(process); return Fail(error, "load-timeout"); }
    VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
    HMODULE remoteDll = RemoteModule(pid, dll, true);
    if (error || !remoteDll) { CloseHandle(process); return Fail(error ? error : ERROR_DLL_INIT_FAILED, "load"); }
    ProbeData data = {}; data.version = 1; data.proxyMode = mode; data.operation = operation;
    void* remoteData = VirtualAllocEx(process, NULL, sizeof(data), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remoteData) error = GetLastError();
    if (!error && (!WriteProcessMemory(process, remoteData, &data, sizeof(data), &written) || written != sizeof(data)))
        error = ERROR_WRITE_FAULT;
    if (!error) error = RunThread(process, reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<BYTE*>(remoteDll) + offset),
        remoteData, 45000);
    if (error == ERROR_TIMEOUT) { CloseHandle(process); return Fail(error, "probe-timeout"); }
    SIZE_T read = 0;
    if (!error && (!ReadProcessMemory(process, remoteData, &data, sizeof(data), &read) || read != sizeof(data)))
        error = ERROR_READ_FAULT;
    if (remoteData) VirtualFreeEx(process, remoteData, 0, MEM_RELEASE);
    DWORD unloadError = RunThread(process, RemoteSystemFunction(pid, "FreeLibrary"), remoteDll, 10000);
    CloseHandle(process);
    if (error) return Fail(error, "probe");
    if (unloadError) return Fail(unloadError, "unload");
    if (data.pid != pid) return Fail(ERROR_INVALID_DATA, "result");
    data.ip4[63] = 0; data.ip6[63] = 0;
    printf("{\"pid\":%lu,\"ip4\":\"%s\",\"ip6\":\"%s\",\"source4\":%lu,\"error4\":%lu,\"error6\":%lu}\n",
        data.pid, data.ip4, data.ip6, data.source4, data.error4, data.error6);
    return 0;
}
