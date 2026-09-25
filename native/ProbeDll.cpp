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

// Plain-text services return only the address; Cloudflare's /cdn-cgi/trace returns key=value lines.
static DWORD ExtractAddress(const char* body, DWORD used, char* output) {
    DWORD start = 0, end = used;
    for (DWORD i = 0; i + 3 <= used; ++i) {
        if ((i == 0 || body[i - 1] == '\n') && body[i] == 'i' && body[i + 1] == 'p' && body[i + 2] == '=') {
            start = i + 3;
            end = start;
            while (end < used && body[end] != '\n') ++end;
            break;
        }
    }
    while (end > start && (body[end - 1] == '\n' || body[end - 1] == '\r' || body[end - 1] == ' ')) --end;
    if (end == start || end - start > 63) return ERROR_INVALID_DATA;
    // A tiny allowlist also makes the fixed JSON protocol safe to serialize.
    for (DWORD i = start; i < end; ++i) {
        char c = body[i];
        if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
              (c >= 'A' && c <= 'F') || c == '.' || c == ':')) return ERROR_INVALID_DATA;
        output[i - start] = c;
    }
    output[end - start] = 0;
    return 0;
}

// Network work runs in the explicit exported function, never inside DllMain/loader lock.
static DWORD Query(const wchar_t* host, const wchar_t* path, DWORD proxyMode, char* output) {
    output[0] = 0;
    HINTERNET session = WinHttpOpen(L"IPCountryWatcher-Probe/1.0",
        proxyMode ? WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY : WINHTTP_ACCESS_TYPE_NO_PROXY,
        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session) return GetLastError();
    WinHttpSetTimeouts(session, 3000, 3000, 3000, 4000);
    HINTERNET connection = WinHttpConnect(session, host, INTERNET_DEFAULT_HTTPS_PORT, 0);
    HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", path, NULL,
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
                char body[1024];
                DWORD used = 0, read = 0;
                ULONGLONG start = GetTickCount64();
                for (;;) {
                    if (used >= sizeof(body) || GetTickCount64() - start > 8000) { error = ERROR_INVALID_DATA; break; }
                    if (!WinHttpReadData(request, body + used, (DWORD)(sizeof(body) - used), &read)) { error = GetLastError(); break; }
                    used += read;
                    if (!read) break;
                }
                if (!error) error = ExtractAddress(body, used, output);
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
    // Services must report the TCP peer, not X-Forwarded-For; keep NativeProcessProbe's labels in sync.
    data->source4 = 1;
    data->error4 = Query(L"ipv4.icanhazip.com", L"/", data->proxyMode, data->ip4);
    if (data->error4) {
        data->source4 = 2;
        data->error4 = Query(L"ipv4.icanhazip.com", L"/cdn-cgi/trace", data->proxyMode, data->ip4);
    }
    data->error6 = Query(L"ipv6.icanhazip.com", L"/", data->proxyMode, data->ip6);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
