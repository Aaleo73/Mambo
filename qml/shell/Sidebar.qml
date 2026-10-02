import QtQuick
import QtQuick.Controls
import Mambo

Item {
    id: sidebar

    width: 208
    opacity: reveal
    transform: Translate { x: (1 - reveal) * -18 }

    property real reveal: 0

    NumberAnimation on reveal {
        from: 0
        to: 1
        duration: Theme.motionShellEnter
        easing.type: Easing.BezierSpline
        easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
        running: true
    }

    Column {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.leftMargin: 8
        anchors.rightMargin: 8
        anchors.topMargin: 12
        spacing: 8

        Item {
            id: search
            width: parent.width
            height: 36
            opacity: 0.45

            Rectangle {
                anchors.fill: parent
                radius: Theme.controlRadius
                color: Theme.fieldFill
                border.width: 1
                border.color: Theme.fieldBorder
            }

            Icon {
                x: 8
                anchors.verticalCenter: parent.verticalCenter
                width: 15
                height: 15
                name: "search"
                strokeColor: Theme.textMuted
                strokeWidth: 2
            }

            MouseArea {
                id: searchHover
                anchors.fill: parent
                hoverEnabled: true
                acceptedButtons: Qt.NoButton
            }

            ToolTip {
                visible: searchHover.containsMouse
                text: "连接服务器后可用"
                delay: 300
            }
        }
    }

    Column {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.leftMargin: 8
        anchors.rightMargin: 8
        anchors.topMargin: 56
        spacing: 2

        NavButton {
            width: parent.width
            label: "首页"
            iconName: "home"
            current: true
        }
        NavButton {
            width: parent.width
            label: "最近播放"
            iconName: "recent"
            locked: true
        }
    }

    NavButton {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        anchors.leftMargin: 8
        anchors.rightMargin: 8
        anchors.bottomMargin: 12
        label: "设置"
        iconName: "settings"
    }

    component NavButton: Item {
        id: button

        property string label: ""
        property string iconName: ""
        property bool current: false
        property bool locked: false
        property bool accentShown: false

        height: 40
        opacity: locked ? 0.45 : 1

        Accessible.role: Accessible.Button
        Accessible.name: label

        Rectangle {
            id: plate
            anchors.fill: parent
            radius: 8
            color: {
                if (button.locked)
                    return "transparent"
                if (area.containsPress && !button.current)
                    return Theme.sidebarPressed
                if (button.current)
                    return Theme.sidebarActive
                if (area.containsMouse)
                    return Theme.sidebarHover
                return "transparent"
            }

            Behavior on color {
                ColorAnimation {
                    duration: Theme.motionHover
                    easing.type: Easing.BezierSpline
                    easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
                }
            }
        }

        Rectangle {
            width: 3
            height: button.current && button.accentShown ? 16 : 0
            radius: 1.5
            anchors.left: parent.left
            anchors.verticalCenter: parent.verticalCenter
            color: Theme.accent

            Behavior on height {
                NumberAnimation {
                    duration: Theme.motionShellEnter
                    easing.type: Easing.BezierSpline
                    easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
                }
            }
        }


        Row {
            anchors.left: parent.left
            anchors.leftMargin: 12
            anchors.verticalCenter: parent.verticalCenter
            spacing: 12

            Icon {
                width: 18
                height: 18
                anchors.verticalCenter: parent.verticalCenter
                name: button.iconName
                strokeColor: Theme.textPrimary
                strokeWidth: 1.8
            }

            Text {
                anchors.verticalCenter: parent.verticalCenter
                text: button.label
                color: Theme.textPrimary
                font.pixelSize: 14
                font.weight: 600
            }
        }

        MouseArea {
            id: area
            anchors.fill: parent
            hoverEnabled: !button.locked
            enabled: !button.locked
            cursorShape: button.locked ? Qt.ArrowCursor : Qt.PointingHandCursor
            onPressed: function (mouse) { mouse.accepted = true }
        }

        Component.onCompleted: button.accentShown = true

        ToolTip {
            visible: button.locked && lockHover.containsMouse
            text: "连接服务器后可用"
            delay: 300
        }

        MouseArea {
            id: lockHover
            anchors.fill: parent
            enabled: button.locked
            hoverEnabled: button.locked
            acceptedButtons: Qt.NoButton
        }

        scale: !button.locked && area.containsPress ? 0.97 : 1

        Behavior on scale {
            NumberAnimation {
                duration: Theme.motionPress
                easing.type: Easing.BezierSpline
                easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
            }
        }
    }
}
