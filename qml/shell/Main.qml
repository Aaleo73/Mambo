import QtQuick
import Mambo

Window {
    id: shell

    width: 1500
    height: 860
    minimumWidth: 1100
    minimumHeight: 720
    visible: true
    title: "Mambo"
    color: "transparent"
    flags: Qt.Window | Qt.FramelessWindowHint

    Item {
        id: glass
        anchors.fill: parent

        Rectangle {
            anchors.fill: parent
            color: Theme.glassBg
        }

        Item {
            anchors.fill: parent
            clip: true

            Rectangle {
                width: glass.width * 2
                height: glass.height * 2
                anchors.centerIn: parent
                rotation: 45
                gradient: Gradient {
                    GradientStop { position: 0.0; color: Theme.washStrong }
                    GradientStop { position: 1.0; color: Theme.washSoft }
                }
            }
        }

        Rectangle {
            anchors.fill: parent
            color: "transparent"
            border.width: 1
            border.color: Theme.glassBorder
        }

        Rectangle {
            height: 1
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.top: parent.top
            color: Theme.glassHighlight
        }

        Rectangle {
            height: 1
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.bottom: parent.bottom
            color: Theme.glassShade
        }
    }

    TitleBar {
        id: titleBar
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.top: parent.top
        shell: shell
        z: 2
    }

    Sidebar {
        id: sidebar
        anchors.left: parent.left
        anchors.top: titleBar.bottom
        anchors.bottom: parent.bottom
        z: 1
    }

    ContentWell {
        anchors.left: sidebar.right
        anchors.right: parent.right
        anchors.top: titleBar.bottom
        anchors.bottom: parent.bottom
        anchors.topMargin: 12
        z: 1
    }

    ResizeEdge {
        width: 6
        anchors.left: parent.left
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        cursor: Qt.SizeHorCursor
        edge: Qt.LeftEdge
    }
    ResizeEdge {
        width: 6
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.bottom: parent.bottom
        cursor: Qt.SizeHorCursor
        edge: Qt.RightEdge
    }
    ResizeEdge {
        height: 6
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.leftMargin: 208
        anchors.rightMargin: 132
        anchors.top: parent.top
        cursor: Qt.SizeVerCursor
        edge: Qt.TopEdge
    }
    ResizeEdge {
        height: 6
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        cursor: Qt.SizeVerCursor
        edge: Qt.BottomEdge
    }
    ResizeEdge {
        width: 10
        height: 10
        anchors.left: parent.left
        anchors.top: parent.top
        cursor: Qt.SizeFDiagCursor
        edge: Qt.LeftEdge | Qt.TopEdge
    }
    ResizeEdge {
        width: 10
        height: 10
        anchors.right: parent.right
        anchors.top: parent.top
        cursor: Qt.SizeBDiagCursor
        edge: Qt.RightEdge | Qt.TopEdge
    }
    ResizeEdge {
        width: 10
        height: 10
        anchors.left: parent.left
        anchors.bottom: parent.bottom
        cursor: Qt.SizeBDiagCursor
        edge: Qt.LeftEdge | Qt.BottomEdge
    }
    ResizeEdge {
        width: 10
        height: 10
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        cursor: Qt.SizeFDiagCursor
        edge: Qt.RightEdge | Qt.BottomEdge
    }

    component ResizeEdge: MouseArea {
        property int edge: 0
        property int cursor: Qt.ArrowCursor

        z: 4
        hoverEnabled: true
        cursorShape: cursor
        enabled: shell.visibility !== Window.Maximized
        onPressed: function (mouse) {
            if (mouse.button === Qt.LeftButton)
                shell.startSystemResize(edge)
        }
    }
}
