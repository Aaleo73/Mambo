import QtQuick
import Mambo

Item {
    id: bar

    required property var shell
    readonly property bool maximized: shell.visibility === Window.Maximized

    height: 40
    opacity: reveal

    property real reveal: 0.62

    NumberAnimation on reveal {
        from: 0.62
        to: 1
        duration: Theme.motionHover
        easing.type: Easing.BezierSpline
        easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
        running: true
    }

    MouseArea {
        anchors.fill: parent
        onPressed: function (mouse) {
            if (mouse.button === Qt.LeftButton)
                shell.startSystemMove()
        }
    }

    Row {
        x: 8
        anchors.verticalCenter: parent.verticalCenter
        spacing: 2

        HistoryButton {
            iconName: "back"
            label: "后退"
        }
        HistoryButton {
            iconName: "forward"
            label: "前进"
        }
    }

    Row {
        id: windowButtons
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        z: 2

        WindowButton {
            iconName: "minimize"
            toolTip: "最小化"
            onActivated: shell.showMinimized()
        }
        WindowButton {
            iconName: bar.maximized ? "restore" : "maximize"
            toolTip: bar.maximized ? "还原" : "最大化"
            onActivated: bar.maximized ? shell.showNormal() : shell.showMaximized()
        }
        WindowButton {
            iconName: "close"
            toolTip: "关闭"
            danger: true
            onActivated: Qt.quit()
        }
    }

    component HistoryButton: Item {
        id: history

        property string iconName: ""
        property string label: ""

        width: 28
        height: 28
        opacity: 0.45

        Accessible.role: Accessible.Button
        Accessible.name: label
        Accessible.ignored: false

        Rectangle {
            anchors.fill: parent
            radius: 6
            color: "transparent"
        }

        Icon {
            anchors.centerIn: parent
            width: 16
            height: 16
            name: history.iconName
            strokeColor: Theme.textPrimary
            strokeWidth: 2.2
        }

        MouseArea {
            anchors.fill: parent
            onPressed: function (mouse) { mouse.accepted = true }
        }
    }
}
