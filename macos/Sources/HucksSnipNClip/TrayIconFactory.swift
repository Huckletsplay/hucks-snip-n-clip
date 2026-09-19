import AppKit

/// Huck’s Snip ’n’ Clip mark. The idle state is a system-tinted template. While recording, the
/// approved H interior becomes a left audio meter and proportional right CPU meter.
enum TrayIconFactory {
    private static let inputColor = NSColor(
        calibratedRed: 63.0 / 255.0, green: 157.0 / 255.0, blue: 98.0 / 255.0, alpha: 1)
    private static let inputWarningColor = NSColor(
        calibratedRed: 201.0 / 255.0, green: 138.0 / 255.0, blue: 46.0 / 255.0, alpha: 1)
    /// The right post is amber for ordinary load and only turns red at the top of its travel.
    /// A post that was red from the first pixel read as a fault at every level, so the one state
    /// worth noticing - the machine actually being overloaded - had nothing left to say it with.
    private static let strainColor = NSColor(
        calibratedRed: 201.0 / 255.0, green: 138.0 / 255.0, blue: 46.0 / 255.0, alpha: 1)
    private static let strainWarningColor = NSColor(
        calibratedRed: 192.0 / 255.0, green: 80.0 / 255.0, blue: 63.0 / 255.0, alpha: 1)

    /// Shared by both posts, so the icon has one idea of "into the warning band" rather than two.
    static let meterWarningThreshold: CGFloat = 0.85
    private static let pausedColor = NSColor(
        calibratedRed: 201.0 / 255.0, green: 138.0 / 255.0, blue: 46.0 / 255.0, alpha: 1)

    static func makeIcon(
        recording: Bool = false,
        paused: Bool = false,
        finishing: Bool = false,
        inputLevel rawInputLevel: Double = 0,
        strainLevel rawStrainLevel: Double = 0
    ) -> NSImage {
        let side: CGFloat = 18
        let inputLevel = CGFloat(min(1.0, max(0.0, rawInputLevel)))
        let strainLevel = CGFloat(min(1.0, max(0.0, rawStrainLevel)))
        let image = NSImage(size: NSSize(width: side, height: side), flipped: false) { rect in
            let scale = side / 64
            let path = makeHPath(scale: scale)

            if recording {
                if finishing {
                    drawFinishingState(in: path, scale: scale)
                } else if paused {
                    drawPausedState(in: path, scale: scale)
                } else {
                    drawRecordingMeters(
                        in: path,
                        scale: scale,
                        inputLevel: inputLevel,
                        strainLevel: strainLevel)
                }
                return true
            }

            drawRestingState(in: path, scale: scale)
            return true
        }

        image.isTemplate = !recording
        image.accessibilityDescription = finishing
            ? "Huck’s Snip ’n’ Clip — finishing recording"
            : (paused
                ? "Huck’s Snip ’n’ Clip — recording paused"
                : (recording ? "Huck’s Snip ’n’ Clip — recording" : "Huck’s Snip ’n’ Clip"))
        return image
    }

    /// The floating controller's H: the same geometry, meters and paused/finishing marks as the
    /// menu-bar H, on an opaque white body so it reads over any desktop. Drawn at the origin of the
    /// current graphics context in the H's 64-unit square.
    static func drawControllerH(
        scale: CGFloat,
        inputLevel rawInputLevel: Double,
        strainLevel rawStrainLevel: Double,
        paused: Bool,
        finishing: Bool
    ) {
        let path = makeHPath(scale: scale)
        let body = NSColor.white
        if finishing {
            drawFinishingState(
                in: path,
                scale: scale,
                body: body,
                mark: NSColor(calibratedWhite: 70.0 / 255.0, alpha: 1))
        } else if paused {
            drawPausedState(in: path, scale: scale, body: body)
        } else {
            drawRecordingMeters(
                in: path,
                scale: scale,
                inputLevel: CGFloat(min(1.0, max(0.0, rawInputLevel))),
                strainLevel: CGFloat(min(1.0, max(0.0, rawStrainLevel))),
                body: body)
        }
    }

    static func controllerHPath(scale: CGFloat) -> NSBezierPath {
        return makeHPath(scale: scale)
    }

    private static func makeHPath(scale: CGFloat) -> NSBezierPath {
        let path = NSBezierPath()
        path.move(to: point(6, 6, scale))
        path.line(to: point(29, 6, scale))
        path.line(to: point(29, 27, scale))
        path.line(to: point(35, 27, scale))
        path.line(to: point(35, 6, scale))
        path.line(to: point(58, 6, scale))
        path.line(to: point(58, 58, scale))
        path.line(to: point(35, 58, scale))
        path.line(to: point(35, 37, scale))
        path.line(to: point(29, 37, scale))
        path.line(to: point(29, 58, scale))
        path.line(to: point(6, 58, scale))
        path.close()
        return path
    }

    /// The chosen grayscale resting identity: two reduced cut strokes on the left and a six-hole
    /// film edge on the right. It uses alpha knockouts so the system tint remains correct on every
    /// menu-bar appearance; no tone or hue is required to read either half.
    private static func drawRestingState(in path: NSBezierPath, scale: CGFloat) {
        NSColor.black.setFill()
        path.fill()

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        path.addClip()
        NSGraphicsContext.current?.compositingOperation = .clear

        let cuts = NSBezierPath()
        cuts.lineWidth = 4 * scale
        cuts.lineCapStyle = .square
        cuts.move(to: point(9, 15, scale))
        cuts.line(to: point(26, 29, scale))
        cuts.move(to: point(9, 49, scale))
        cuts.line(to: point(26, 35, scale))
        NSColor.black.setStroke()
        cuts.stroke()

        NSColor.black.setFill()
        for x in [38.0, 50.0] {
            for y in [13.0, 28.5, 44.0] {
                NSBezierPath.fill(NSRect(
                    x: x * scale,
                    y: y * scale,
                    width: 5 * scale,
                    height: 7 * scale))
            }
        }
    }

    private static func drawRecordingMeters(
        in path: NSBezierPath,
        scale: CGFloat,
        inputLevel: CGFloat,
        strainLevel: CGFloat,
        body: NSColor = NSColor.labelColor.withAlphaComponent(0.22)
    ) {
        body.setFill()
        path.fill()

        NSGraphicsContext.saveGraphicsState()
        path.addClip()

        let bottom = 6 * scale
        let fullHeight = 52 * scale

        // The input fill spans the left post plus the connector; the strain fill is the right post.
        fillMeter(
            x: 6 * scale,
            width: 29 * scale,
            bottom: bottom,
            fullHeight: fullHeight,
            level: inputLevel,
            base: inputColor,
            warning: inputWarningColor,
            style: .capAboveThreshold)
        fillMeter(
            x: 35 * scale,
            width: 23 * scale,
            bottom: bottom,
            fullHeight: fullHeight,
            level: strainLevel,
            base: strainColor,
            warning: strainWarningColor,
            style: .wholeBar)

        NSGraphicsContext.restoreGraphicsState()
    }

    /// How a post shows that it has crossed the threshold. The two posts differ deliberately.
    enum MeterWarningStyle {
        /// Only the part of the bar above the threshold takes the warning colour. Audio loudness is
        /// a gradient - how far into the loud band matters - and this shows that distance.
        case capAboveThreshold
        /// The whole bar changes colour at once. Overload is not a gradient worth reading, it is a
        /// yes or no, and at an 18 pt menu bar a one-pixel cap is not something anyone catches out
        /// of the corner of an eye.
        case wholeBar
    }

    /// Fills one post from the bottom. Height always carries the actual reading, so colour stays the
    /// second cue and the icon remains legible to someone who cannot separate these hues.
    private static func fillMeter(
        x: CGFloat,
        width: CGFloat,
        bottom: CGFloat,
        fullHeight: CGFloat,
        level: CGFloat,
        base: NSColor,
        warning: NSColor,
        style: MeterWarningStyle
    ) {
        guard level > 0 else { return }
        let warned = level >= meterWarningThreshold

        if style == .wholeBar {
            (warned ? warning : base).setFill()
            NSBezierPath.fill(NSRect(x: x, y: bottom, width: width, height: fullHeight * level))
            return
        }

        base.setFill()
        NSBezierPath.fill(NSRect(x: x, y: bottom, width: width, height: fullHeight * level))
        guard warned else { return }
        warning.setFill()
        NSBezierPath.fill(NSRect(
            x: x,
            y: bottom + fullHeight * meterWarningThreshold,
            width: width,
            height: fullHeight * (level - meterWarningThreshold)))
    }

    private static func drawPausedState(
        in path: NSBezierPath,
        scale: CGFloat,
        body: NSColor = NSColor.labelColor.withAlphaComponent(0.22)
    ) {
        body.setFill()
        path.fill()

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        path.addClip()

        pausedColor.setFill()
        for x in [14.0, 42.0] {
            NSBezierPath.fill(NSRect(
                x: x * scale,
                y: 20 * scale,
                width: 8 * scale,
                height: 24 * scale))
        }
    }

    /// A hue-independent processing mark. Three square dots through the crossbar read as an
    /// ellipsis at 18 pt and cannot be confused with either the bottom-up meters or pause bars.
    private static func drawFinishingState(
        in path: NSBezierPath,
        scale: CGFloat,
        body: NSColor = NSColor.labelColor.withAlphaComponent(0.22),
        mark: NSColor = NSColor.labelColor
    ) {
        body.setFill()
        path.fill()

        NSGraphicsContext.saveGraphicsState()
        defer { NSGraphicsContext.restoreGraphicsState() }
        path.addClip()

        mark.setFill()
        for x in [13.0, 28.0, 43.0] {
            NSBezierPath.fill(NSRect(
                x: x * scale,
                y: 28 * scale,
                width: 8 * scale,
                height: 8 * scale))
        }
    }

    private static func point(_ x: CGFloat, _ y: CGFloat, _ scale: CGFloat) -> CGPoint {
        CGPoint(x: x * scale, y: y * scale)
    }
}
