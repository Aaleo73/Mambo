import QtQuick

Canvas {
    id: icon

    property string name: ""
    property color strokeColor: "#181818"
    property real strokeWidth: 1.8

    onNameChanged: requestPaint()
    onStrokeColorChanged: requestPaint()
    onWidthChanged: requestPaint()
    onHeightChanged: requestPaint()
    onStrokeWidthChanged: requestPaint()
    Component.onCompleted: requestPaint()

    onPaint: {
        const ctx = getContext("2d")
        ctx.clearRect(0, 0, width, height)
        const windowGlyph = name === "minimize" || name === "maximize" || name === "restore" || name === "close"
        const box = windowGlyph ? 10 : 24
        ctx.strokeStyle = strokeColor
        ctx.fillStyle = strokeColor
        ctx.lineCap = "round"
        ctx.lineJoin = "round"
        ctx.scale(width / box, height / box)
        ctx.lineWidth = strokeWidth

        if (name === "back" || name === "forward") {
            ctx.lineWidth = 2.2
            ctx.beginPath()
            if (name === "back") {
                ctx.moveTo(15, 18)
                ctx.lineTo(9, 12)
                ctx.lineTo(15, 6)
            } else {
                ctx.moveTo(9, 18)
                ctx.lineTo(15, 12)
                ctx.lineTo(9, 6)
            }
            ctx.stroke()
        } else if (name === "minimize") {
            ctx.fillRect(0, 4.5, 10, 1)
        } else if (name === "maximize") {
            ctx.lineWidth = 1
            ctx.strokeRect(0.5, 0.5, 9, 9)
        } else if (name === "restore") {
            ctx.lineWidth = 1
            ctx.strokeRect(2.5, 0.5, 7, 7)
            ctx.beginPath()
            ctx.moveTo(2.5, 2.5)
            ctx.lineTo(0.5, 2.5)
            ctx.lineTo(0.5, 9.5)
            ctx.lineTo(7.5, 9.5)
            ctx.lineTo(7.5, 7.5)
            ctx.stroke()
        } else if (name === "close") {
            ctx.lineWidth = 1.2
            ctx.beginPath()
            ctx.moveTo(1, 1)
            ctx.lineTo(9, 9)
            ctx.moveTo(9, 1)
            ctx.lineTo(1, 9)
            ctx.stroke()
        } else if (name === "search") {
            ctx.lineWidth = 2
            ctx.beginPath()
            ctx.arc(11, 11, 7, 0, Math.PI * 2)
            ctx.moveTo(16.2, 16.2)
            ctx.lineTo(20, 20)
            ctx.stroke()
        } else if (name === "home") {
            ctx.beginPath()
            ctx.moveTo(4, 11)
            ctx.lineTo(12, 4)
            ctx.lineTo(20, 11)
            ctx.lineTo(20, 20)
            ctx.quadraticCurveTo(20, 21, 19, 21)
            ctx.lineTo(5, 21)
            ctx.quadraticCurveTo(4, 21, 4, 20)
            ctx.closePath()
            ctx.moveTo(9, 21)
            ctx.lineTo(9, 13)
            ctx.lineTo(15, 13)
            ctx.lineTo(15, 21)
            ctx.stroke()
        } else if (name === "recent") {
            ctx.beginPath()
            ctx.arc(12, 12, 8, 0, Math.PI * 2)
            ctx.moveTo(12, 8)
            ctx.lineTo(12, 12)
            ctx.lineTo(15.2, 14)
            ctx.stroke()
        } else if (name === "settings") {
            ctx.beginPath()
            ctx.arc(12, 12, 2.4, 0, Math.PI * 2)
            ctx.stroke()
            ctx.beginPath()
            ctx.arc(12, 12, 5.4, 0, Math.PI * 2)
            ctx.stroke()
            ctx.lineWidth = 2.4
            ctx.beginPath()
            const teeth = 8
            for (let i = 0; i < teeth; ++i) {
                const angle = (Math.PI * 2 * i) / teeth - Math.PI / 2
                ctx.moveTo(12 + Math.cos(angle) * 5.2, 12 + Math.sin(angle) * 5.2)
                ctx.lineTo(12 + Math.cos(angle) * 8.6, 12 + Math.sin(angle) * 8.6)
            }
            ctx.stroke()
        }
    }
}
