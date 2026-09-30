// Loading is deliberately explicit: a test linked to the DLL cannot verify unloading it.
#include "ufsqlite.h"
#include <windows.h>
#include <iostream>

int main()
{
    wchar_t executable[32768];
    DWORD length = GetModuleFileNameW(nullptr, executable, 32768);
    if (length == 0 || length == 32768)
        return 1;
    std::wstring path(executable, length);
    path = path.substr(0, path.find_last_of(L"\\/") + 1) + L"uiframe_sqlite.dll";
    for (int i = 0; i < 8; ++i)
    {
        HMODULE library = LoadLibraryW(path.c_str());
        if (!library)
            return 2;
        auto create = reinterpret_cast<decltype(&ufsqlite_client_create)>(
            GetProcAddress(library, "ufsqlite_client_create"));
        auto release = reinterpret_cast<decltype(&ufsqlite_client_release)>(
            GetProcAddress(library, "ufsqlite_client_release"));
        uint64_t client = 0;
        if (!create || !release || create(UFSQLITE_ABI, &client) != UF_OK || release(client) != UF_OK)
            return 3;
        if (!FreeLibrary(library))
            return 4;
    }
    std::cout << "Windows DLL startup, final-client shutdown and unload passed.\n";
}
