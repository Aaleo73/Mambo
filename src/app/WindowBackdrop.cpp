#include "WindowBackdrop.h"

#include <QDebug>
#include <QQuickWindow>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <dwmapi.h>

namespace {

constexpr DWORD kUseImmersiveDarkMode = 20;
constexpr DWORD kWindowCornerPreference = 33;
constexpr DWORD kSystemBackdropType = 38;
constexpr DWORD kMicaEffect = 1029;
constexpr DWORD kRoundCorners = 2;

constexpr DWORD kBackdropNone = 1;
constexpr DWORD kBackdropMica = 2;
constexpr DWORD kBackdropAcrylic = 3;
constexpr DWORD kBackdropTabbed = 4;

struct AccentPolicy {
    DWORD state;
    DWORD flags;
    DWORD gradientColor;
    DWORD animationId;
};

struct CompositionAttributeData {
    DWORD attribute;
    PVOID data;
    SIZE_T dataSize;
};

using SetCompositionAttribute = BOOL(WINAPI *)(HWND, CompositionAttributeData *);
using RtlGetVersionFn = LONG(WINAPI *)(OSVERSIONINFOEXW *);

DWORD windowsBuild()
{
    OSVERSIONINFOEXW info = {};
    info.dwOSVersionInfoSize = sizeof(info);
    const auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
        GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
    if (rtlGetVersion == nullptr || rtlGetVersion(&info) != 0) {
        return 0;
    }
    return info.dwBuildNumber;
}

HRESULT setDwordAttribute(HWND hwnd, DWORD attribute, DWORD value)
{
    return DwmSetWindowAttribute(hwnd, attribute, &value, sizeof(value));
}

// window-vibrancy only forwards this tint on the Windows 10 composition path.
// Windows 11's backdrop-type acrylic has no color argument, which is why the
// reference app still passes (236, 242, 249, 90) and the Win11 path ignores it.
bool applyWindows10Acrylic(HWND hwnd)
{
    const auto setComposition = reinterpret_cast<SetCompositionAttribute>(
        GetProcAddress(GetModuleHandleW(L"user32.dll"), "SetWindowCompositionAttribute"));
    if (setComposition == nullptr) {
        return false;
    }

    AccentPolicy policy = {};
    policy.state = 4; // ACCENT_ENABLE_ACRYLICBLURBEHIND
    policy.flags = 0;
    policy.gradientColor = 236u | (242u << 8) | (249u << 16) | (90u << 24);
    policy.animationId = 0;

    CompositionAttributeData data = {};
    data.attribute = 19; // WCA_ACCENT_POLICY
    data.data = &policy;
    data.dataSize = sizeof(policy);
    return setComposition(hwnd, &data) != FALSE;
}

void roundWindow(HWND hwnd)
{
    const DWORD preference = kRoundCorners;
    DwmSetWindowAttribute(hwnd, kWindowCornerPreference, &preference, sizeof(preference));

    // Sheet of glass: transparent QML pixels show the backdrop, opaque pixels stay.
    MARGINS margins = {-1, -1, -1, -1};
    DwmExtendFrameIntoClientArea(hwnd, &margins);
}

} // namespace

QString applyWindowBackdrop(QQuickWindow *window)
{
    if (window == nullptr) {
        return QStringLiteral("none");
    }

    const HWND hwnd = reinterpret_cast<HWND>(window->winId());
    roundWindow(hwnd);

    const DWORD build = windowsBuild();
    const bool backdropApi = build >= 22523;
    const bool swca = build >= 17763;
    const bool undocumentedMica = build >= 22000;

    QString mode = QStringLiteral("none");
    if (backdropApi && SUCCEEDED(setDwordAttribute(hwnd, kSystemBackdropType, kBackdropAcrylic))) {
        mode = QStringLiteral("acrylic");
    } else if (swca && !backdropApi && applyWindows10Acrylic(hwnd)) {
        mode = QStringLiteral("acrylic");
    } else {
        setDwordAttribute(hwnd, kUseImmersiveDarkMode, 0);
        if (backdropApi && SUCCEEDED(setDwordAttribute(hwnd, kSystemBackdropType, kBackdropTabbed))) {
            mode = QStringLiteral("tabbed");
        } else if (backdropApi && SUCCEEDED(setDwordAttribute(hwnd, kSystemBackdropType, kBackdropMica))) {
            mode = QStringLiteral("mica");
        } else if (undocumentedMica && SUCCEEDED(setDwordAttribute(hwnd, kMicaEffect, 1))) {
            mode = QStringLiteral("mica");
        } else if (backdropApi) {
            setDwordAttribute(hwnd, kSystemBackdropType, kBackdropNone);
        }
    }

    qInfo().noquote() << "window-backdrop:" << mode;
    window->requestUpdate();
    return mode;
}
