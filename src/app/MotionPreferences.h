#pragma once

#include <QObject>

class QAbstractNativeEventFilter;

// Mirrors CSS prefers-reduced-motion on Windows via SPI_GETCLIENTAREAANIMATION.
class MotionPreferences : public QObject {
    Q_OBJECT
    Q_PROPERTY(bool reduceMotion READ reduceMotion NOTIFY reduceMotionChanged)

public:
    explicit MotionPreferences(QObject *parent = nullptr);
    ~MotionPreferences() override;

    bool reduceMotion() const;
    void refresh();

signals:
    void reduceMotionChanged();

private:
    QAbstractNativeEventFilter *filter_ = nullptr;
    bool reduceMotion_ = false;
};
