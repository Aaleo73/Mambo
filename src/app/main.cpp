#include "MotionPreferences.h"
#include "WindowBackdrop.h"

#include <QFont>
#include <QFontDatabase>
#include <QGuiApplication>
#include <QQmlApplicationEngine>
#include <QQuickStyle>
#include <QQuickWindow>
#include <QtQml/qqml.h>

#include <cstdio>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace {

void attachParentConsole()
{
    if (!AttachConsole(ATTACH_PARENT_PROCESS)) {
        return;
    }
    FILE *stream = nullptr;
    freopen_s(&stream, "CONOUT$", "w", stdout);
    freopen_s(&stream, "CONOUT$", "w", stderr);
}

void installMiSans()
{
    const QStringList faces = {
        QStringLiteral(":/fonts/MiSans-Regular.ttf"),
        QStringLiteral(":/fonts/MiSans-Medium.ttf"),
        QStringLiteral(":/fonts/MiSans-Semibold.ttf"),
        QStringLiteral(":/fonts/MiSans-Bold.ttf"),
    };
    for (const QString &face : faces) {
        if (QFontDatabase::addApplicationFont(face) < 0) {
            qWarning().noquote() << "font load failed:" << face;
        }
    }

    QFont font;
    font.setFamilies({
        QStringLiteral("MiSans"),
        QStringLiteral("Segoe UI Variable"),
        QStringLiteral("Segoe UI"),
    });
    font.setPixelSize(14);
    font.setWeight(QFont::Normal);
    QGuiApplication::setFont(font);
}

} // namespace

int main(int argc, char *argv[])
{
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    attachParentConsole();

    QGuiApplication::setHighDpiScaleFactorRoundingPolicy(
        Qt::HighDpiScaleFactorRoundingPolicy::PassThrough);
    QQuickWindow::setGraphicsApi(QSGRendererInterface::OpenGL);
    QQuickWindow::setDefaultAlphaBuffer(true);

    QGuiApplication app(argc, argv);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    installMiSans();

    auto *motion = new MotionPreferences(&app);
    qmlRegisterSingletonInstance("Mambo.Platform", 1, 0, "MotionPreferences", motion);

    QQmlApplicationEngine engine;
    QObject::connect(
        &engine,
        &QQmlApplicationEngine::objectCreationFailed,
        &app,
        []() { QCoreApplication::exit(1); },
        Qt::QueuedConnection);
    engine.loadFromModule("Mambo", "Main");
    if (engine.rootObjects().isEmpty()) {
        return 1;
    }

    auto *window = qobject_cast<QQuickWindow *>(engine.rootObjects().constFirst());
    if (window == nullptr) {
        return 1;
    }
    const auto apply = [window]() { applyWindowBackdrop(window); };
    QObject::connect(window, &QQuickWindow::visibleChanged, window, [window, apply](bool visible) {
        if (visible) {
            apply();
        }
    });
    if (window->isVisible()) {
        apply();
    }

    return QGuiApplication::exec();
}
