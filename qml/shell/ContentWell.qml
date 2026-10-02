import QtQuick
import QtQuick.Shapes
import Mambo

Item {
    id: well
    opacity: reveal
    transform: Translate { y: (1 - reveal) * 14 }

    property real reveal: 0

    NumberAnimation on reveal {
        from: 0
        to: 1
        duration: Theme.motionShellEnter
        easing.type: Easing.BezierSpline
        easing.bezierCurve: [0.22, 1, 0.36, 1, 1, 1]
        running: true
    }
    Shape {
        anchors.fill: parent
        preferredRendererType: Shape.CurveRenderer

        ShapePath {
            strokeColor: Theme.contentBorder
            strokeWidth: 1
            fillColor: "transparent"
            capStyle: ShapePath.FlatCap
            joinStyle: ShapePath.RoundJoin
            startX: 0.5
            startY: well.height
            PathLine {
                x: 0.5
                y: 12
            }
            PathArc {
                x: 12.5
                y: 0.5
                radiusX: 12
                radiusY: 12
                useLargeArc: false
                direction: PathArc.Clockwise
            }
            PathLine {
                x: well.width - 12
                y: 0.5
            }
            PathArc {
                x: well.width
                y: 12.5
                radiusX: 12
                radiusY: 12
                useLargeArc: false
                direction: PathArc.Clockwise
            }
        }
    }
}
