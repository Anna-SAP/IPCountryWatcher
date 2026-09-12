#pragma once
#include <windows.h>
struct ProbeData {
    DWORD version;
    DWORD proxyMode;
    DWORD operation;
    DWORD pid;
    DWORD source4;
    DWORD error4;
    DWORD error6;
    char ip4[64];
    char ip6[64];
};
