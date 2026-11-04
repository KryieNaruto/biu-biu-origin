// app_main.cpp —— 程序入口（WIN32 子系统）
// OWNER: winui-list
//
// 用法：
//   asset_viewer.exe [素材目录]
//   无参数时自动向上查找包含「素材」或 project.godot 的目录。
#include "viewer_app.h"

#include <commctrl.h>
#include <objbase.h>
#include <shellapi.h>
#include <string>

#pragma comment(lib, "comctl32.lib")
#pragma comment(lib, "shell32.lib")

namespace {

// 解析命令行：argv[1] 或 --root=<路径>
std::wstring ParseRootArg(int argc, wchar_t** argv) {
    for (int i = 1; i < argc; ++i) {
        std::wstring a(argv[i]);
        const std::wstring prefix = L"--root=";
        if (a.size() > prefix.size() && a.compare(0, prefix.size(), prefix) == 0) {
            return a.substr(prefix.size());
        }
        if (!a.empty() && a[0] != L'-') {
            return a;
        }
    }
    return std::wstring();
}

// 把相对路径转为绝对路径（相对当前工作目录）
std::wstring MakeAbsolute(const std::wstring& p) {
    if (p.empty()) return p;
    wchar_t buf[MAX_PATH * 2] = {0};
    DWORD n = ::GetFullPathNameW(p.c_str(), MAX_PATH * 2, buf, nullptr);
    if (n == 0 || n >= MAX_PATH * 2) return p;
    return std::wstring(buf, n);
}

} // namespace

int WINAPI wWinMain(HINSTANCE hInstance, HINSTANCE hPrev, LPWSTR lpCmdLine, int nCmdShow) {
    (void)hPrev;
    (void)lpCmdLine;
    (void)nCmdShow;

    // DPI：优先 Per-Monitor V2，旧系统回退到系统级 DPI 感知
    {
        typedef BOOL(WINAPI* SetCtxFn)(HANDLE);
        HMODULE user32 = ::GetModuleHandleW(L"user32.dll");
        bool done = false;
        if (user32) {
            SetCtxFn fn = (SetCtxFn)::GetProcAddress(user32, "SetProcessDpiAwarenessContext");
            if (fn) {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 == (HANDLE)-4
                if (fn((HANDLE)(LONG_PTR)-4)) done = true;
            }
        }
        if (!done) {
            typedef BOOL(WINAPI* SetAwareFn)(BOOL);
            if (user32) {
                SetAwareFn fn = (SetAwareFn)::GetProcAddress(user32, "SetProcessDPIAware");
                if (fn) fn(TRUE);
            }
        }
    }

    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    std::wstring root;
    if (argv) {
        root = ParseRootArg(argc, argv);
        ::LocalFree(argv);
    }
    root = MakeAbsolute(root);

    // 通用控件 / 视觉样式
    INITCOMMONCONTROLSEX icc{};
    icc.dwSize = sizeof(icc);
    icc.dwICC = ICC_STANDARD_CLASSES;
    ::InitCommonControlsEx(&icc);

    av::ViewerApp app;
    if (!app.Initialize(hInstance, root)) {
        ::MessageBoxW(nullptr, L"初始化失败：无法创建主窗口。", L"BiuBiu 素材快查看器",
                      MB_ICONERROR | MB_OK);
        return 1;
    }

    // --shot=<png 路径>：离屏渲染一帧存 PNG 后退出（交付自检用，不依赖屏幕截图）
    std::wstring shotPath;
    {
        int argc2 = 0;
        LPWSTR* argv2 = ::CommandLineToArgvW(::GetCommandLineW(), &argc2);
        if (argv2) {
            const std::wstring prefix = L"--shot=";
            for (int i = 1; i < argc2; ++i) {
                std::wstring a(argv2[i]);
                if (a.size() > prefix.size() && a.compare(0, prefix.size(), prefix) == 0) {
                    shotPath = MakeAbsolute(a.substr(prefix.size()));
                    break;
                }
            }
            ::LocalFree(argv2);
        }
    }
    if (!shotPath.empty()) {
        return app.RunCaptureMode(shotPath) ? 0 : 2;
    }

    return app.Run();
}
