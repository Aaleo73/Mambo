import QtQuick
import QtQuick.Controls
import Mambo

Item {
    id: control

    property string iconName: ""
    property string toolTip: ""
    property bool danger: false
    signal activated

    width: 44
    height: 40

    Accessible.role: Accessible.Button
    Accessible.name: toolTip

    readonly property color restingColor: Theme.textSecondary
    readonly property color hoverFill: danger ? Theme.danger : Theme.neutralWash
    readonly property color pressFill: danger ? Theme.dangerHover : Theme.pressWash
    readonly property color hoverGlyph: danger ? "white" : Theme.textPrimary
    property color glyphColor: area.containsMouse ? hoverGlyph : restingColor

    scale: area.containsPress ? 0.94 : 1

    Behavior on scale {
        NumberAnimation {
            duration: Theme.motionPress
            easing.type: Easing.BezierSpline
            easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
        }
    }

    Behavior on glyphColor {
        ColorAnimation {
            duration: Theme.motionHover
            easing.type: Easing.BezierSpline
            easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
        }
    }

    Rectangle {
        id: plate
        anchors.fill: parent
        color: area.containsPress ? control.pressFill : (area.containsMouse ? control.hoverFill : "transparent")

        Behavior on color {
            ColorAnimation {
                duration: Theme.motionHover
                easing.type: Easing.BezierSpline
                easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
            }
        }
    }

    Icon {
        anchors.centerIn: parent
        width: iconName === "minimize" || iconName === "maximize" || iconName === "restore" || iconName === "close" ? 10 : 16
        height: iconName === "minimize" ? 10 : width
        name: control.iconName
        strokeColor: control.glyphColor
        strokeWidth: iconName === "close" ? 1.2 : 1
    }

    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onPressed: function (mouse) { mouse.accepted = true }
        onClicked: control.activated()
    }

    ToolTip {
        visible: area.containsMouse && control.toolTip !== ""
        text: control.toolTip
        delay: 400
    }
}
