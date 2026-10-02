#pragma once

#include <QString>

class QQuickWindow;

// Applies the same Windows backdrop sequence as emby-mpv-player's main.rs:
// acrylic, then tabbed, then mica. Returns the mode that stuck.
QString applyWindowBackdrop(QQuickWindow *window);
