#include "MotionPreferences.h"

#include <QAbstractNativeEventFilter>
#include <QCoreApplication>
#include <QGuiApplication>

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

namespace {

class SettingChangeFilter : public QAbstractNativeEventFilter {
public:
    explicit SettingChangeFilter(MotionPreferences *owner)
        : owner_(owner)
    {
    }

    bool nativeEventFilter(const QByteArray &eventType, void *message, qintptr *result) override
    {
        Q_UNUSED(eventType);
        Q_UNUSED(result);
        const auto *msg = static_cast<MSG *>(message);
        if (msg != nullptr && msg->message == WM_SETTINGCHANGE) {
            owner_->refresh();
        }
        return false;
    }

private:
    MotionPreferences *owner_ = nullptr;
};

} // namespace

MotionPreferences::MotionPreferences(QObject *parent)
    : QObject(parent)
{
    refresh();
    filter_ = new SettingChangeFilter(this);
    if (auto *app = qobject_cast<QGuiApplication *>(QCoreApplication::instance())) {
        app->installNativeEventFilter(filter_);
    }
}

MotionPreferences::~MotionPreferences()
{
    if (auto *app = qobject_cast<QGuiApplication *>(QCoreApplication::instance())) {
        app->removeNativeEventFilter(filter_);
    }
    delete filter_;
    filter_ = nullptr;
}

bool MotionPreferences::reduceMotion() const
{
    return reduceMotion_;
}

void MotionPreferences::refresh()
{
    BOOL animationsEnabled = TRUE;
    if (!SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &animationsEnabled, 0)) {
        animationsEnabled = TRUE;
    }
    const bool reduce = animationsEnabled == FALSE;
    if (reduce == reduceMotion_) {
        return;
    }
    reduceMotion_ = reduce;
    emit reduceMotionChanged();
}
