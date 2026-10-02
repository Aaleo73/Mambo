pragma Singleton

import QtQuick
import Mambo.Platform

QtObject {
    readonly property bool reduceMotion: MotionPreferences.reduceMotion

    function duration(milliseconds) {
        return reduceMotion ? 1 : milliseconds
    }

    readonly property color glassBg: Qt.rgba(246 / 255, 247 / 255, 249 / 255, 0.055)
    readonly property color glassBgStrong: Qt.rgba(1, 1, 1, 0.12)
    readonly property color glassBorder: Qt.rgba(1, 1, 1, 0.14)
    readonly property color glassShadow: Qt.rgba(15 / 255, 23 / 255, 42 / 255, 0.16)
    readonly property color glassHighlight: Qt.rgba(1, 1, 1, 0.18)
    readonly property color glassShade: Qt.rgba(1, 1, 1, 0.05)
    readonly property color washStrong: Qt.rgba(1, 1, 1, 0.075)
    readonly property color washSoft: Qt.rgba(1, 1, 1, 0.025)

    readonly property color textPrimary: Qt.rgba(24 / 255, 24 / 255, 24 / 255, 0.9)
    readonly property color textSecondary: Qt.rgba(30 / 255, 30 / 255, 30 / 255, 0.72)
    readonly property color textMuted: Qt.rgba(38 / 255, 38 / 255, 38 / 255, 0.55)

    readonly property color accent: "#0c68b8"
    readonly property color accentHover: "#095a9f"
    readonly property color accentLight: Qt.rgba(12 / 255, 104 / 255, 184 / 255, 0.16)
    readonly property color danger: "#c42b1c"
    readonly property color dangerHover: "#a52315"
    readonly property color rating: "#e5a00d"

    readonly property color neutralSoft: Qt.rgba(0, 0, 0, 0.035)
    readonly property color neutralWash: Qt.rgba(0, 0, 0, 0.06)
    readonly property color pressWash: Qt.rgba(0, 0, 0, 0.1)
    readonly property color sidebarActive: Qt.rgba(0, 0, 0, 0.12)
    readonly property color sidebarHover: Qt.rgba(0, 0, 0, 0.04)
    readonly property color sidebarPressed: Qt.rgba(0, 0, 0, 0.08)
    readonly property color contentBorder: Qt.rgba(0, 0, 0, 0.1)
    readonly property color fieldBorder: Qt.rgba(0, 0, 0, 0.2)
    readonly property color fieldFill: Qt.rgba(0, 0, 0, 0.025)

    readonly property int controlRadius: 12
    readonly property int cardLineHeight: 17
    readonly property int shellRadius: 8
    readonly property int contentRadius: 12

    readonly property int motionReduced: duration(80)
    readonly property int motionMicro: duration(80)
    readonly property int motionInteraction: duration(160)
    readonly property int motionImageReady: duration(140)
    readonly property int motionSurface: duration(240)
    readonly property int motionRoute: duration(280)
    readonly property int motionHeroContent: duration(320)
    readonly property int motionHeroForegroundExit: duration(180)
    readonly property int motionHeroTransition: duration(480)
    readonly property int motionBackdropSettle: duration(500)
    readonly property int motionBackdropSettleExit: duration(300)
    readonly property int motionMajorTransition: duration(800)
    readonly property int motionShellFold: duration(560)
    readonly property int motionPagePhase: duration(240)
    readonly property int motionContentExit: duration(260)
    readonly property int motionCoverTransition: duration(220)
    readonly property int motionDetailReveal: duration(340)
    readonly property int motionItemCount: duration(280)
    readonly property int motionShellEnter: duration(420)
    readonly property int motionHover: duration(200)
    readonly property int motionPress: duration(140)

    readonly property var easeEnter: [0, 0, 0.2, 1]
    readonly property var easeStandard: [0.4, 0, 0.2, 1]
    readonly property var easeFluid: [0.2, 0.8, 0.2, 1]
    readonly property var easeExit: [0.4, 0, 1, 1]
    readonly property var easeSettle: [0.22, 1, 0.36, 1]
    readonly property var easeLinear: [0, 0, 1, 1]

    readonly property var fontFamilies: ["MiSans", "Segoe UI Variable", "Segoe UI"]
}
