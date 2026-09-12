#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
int main() {
    wchar_t name[96];
    swprintf_s(name, L"Local\\IPCountryWatcher.ProbeFixture.%lu", GetCurrentProcessId());
    HANDLE stop = CreateEventW(NULL, TRUE, FALSE, name);
    if (!stop) return 1;
    DWORD result = WaitForSingleObject(stop, 120000);
    CloseHandle(stop);
    return result == WAIT_OBJECT_0 ? 0 : 2;
}
