#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winhttp.h>
#include "ProbeProtocol.h"
#pragma comment(lib, "winhttp.lib")

#ifdef _M_IX86
#pragma comment(linker, "/EXPORT:ProbeRun=_ProbeRun@4")
#else
#pragma comment(linker, "/EXPORT:ProbeRun")
#endif

// Network work runs in the explicit exported function, never inside DllMain/loader lock.
static DWORD Query(const wchar_t* host, DWORD proxyMode, char* output) {
    output[0] = 0;
    HINTERNET session = WinHttpOpen(L"IPCountryWatcher-Probe/1.0",
        proxyMode ? WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY : WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return GetLastError();
    WinHttpSetTimeouts(session, 3000, 3000, 3000, 4000);
    HINTERNET connection = WinHttpConnect(session, host, INTERNET_DEFAULT_HTTPS_PORT, 0);
    HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", L"/", NULL,
        WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE | WINHTTP_FLAG_REFRESH) : NULL;
    DWORD error = request ? 0 : GetLastError();
    if (request) {
        DWORD redirect = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
        WinHttpSetOption(request, WINHTTP_OPTION_REDIRECT_POLICY, &redirect, sizeof(redirect));
        // Never send the target user's integrated authentication credentials to a proxy/server.
        DWORD auth = WINHTTP_AUTOLOGON_SECURITY_LEVEL_HIGH;
        WinHttpSetOption(request, WINHTTP_OPTION_AUTOLOGON_POLICY, &auth, sizeof(auth));
        if (!WinHttpSendRequest(request, L"Cache-Control: no-cache\r\n", (DWORD)-1L,
            WINHTTP_NO_REQUEST_DATA, 0, 0, 0) || !WinHttpReceiveResponse(request, NULL)) {
            error = GetLastError();
        } else {
            DWORD status = 0, length = sizeof(status);
            if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                WINHTTP_HEADER_NAME_BY_INDEX, &status, &length, WINHTTP_NO_HEADER_INDEX)) error = GetLastError();
            else if (status != 200) error = 0x20000000UL + status;
            else {
                DWORD used = 0, read = 0;
                ULONGLONG start = GetTickCount64();
                for (;;) {
                    if (used >= 63 || GetTickCount64() - start > 8000) { error = ERROR_INVALID_DATA; break; }
                    if (!WinHttpReadData(request, output + used, 63 - used, &read)) { error = GetLastError(); break; }
                    used += read;
                    if (!read) break;
                }
                output[used] = 0;
                while (used && (output[used - 1] == '\n' || output[used - 1] == '\r' || output[used - 1] == ' '))
                    output[--used] = 0;
                if (!used) error = ERROR_INVALID_DATA;
                // A tiny allowlist also makes the fixed JSON protocol safe to serialize.
                for (DWORD i = 0; i < used; ++i) {
                    char c = output[i];
                    if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                          (c >= 'A' && c <= 'F') || c == '.' || c == ':')) error = ERROR_INVALID_DATA;
                }
            }
        }
        WinHttpCloseHandle(request);
    }
    if (connection) WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
    if (error) output[0] = 0;
    return error;
}

extern "C" DWORD WINAPI ProbeRun(void* parameter) {
    ProbeData* data = static_cast<ProbeData*>(parameter);
    if (!data || data->version != 1 || data->proxyMode > 1 || data->operation > 1) return ERROR_INVALID_PARAMETER;
    data->pid = GetCurrentProcessId();
    if (!data->operation) return 0; // Offline identity/transport smoke test.
    data->source4 = 1;
    data->error4 = Query(L"api.ipify.org", data->proxyMode, data->ip4);
    if (data->error4) {
        data->source4 = 2;
        data->error4 = Query(L"checkip.amazonaws.com", data->proxyMode, data->ip4);
    }
    data->error6 = Query(L"api6.ipify.org", data->proxyMode, data->ip6);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
