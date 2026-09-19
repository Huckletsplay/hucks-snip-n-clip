import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

// Finder/application icon only; the menu bar continues to use its system-tinted template.
// Geometry: docs/icon/h-cut-film-16.svg and h-cut-film.svg. Keep the mark grayscale;
// Huck chose a grayscale-only resting mark on 2026-09-13. Do not extend Windows' legacy teal.
guard CommandLine.arguments.count == 2 else {
    fatalError("Usage: swift generate-app-icon.swift <output.iconset>")
}

let output = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)

func render(side: Int) throws -> CGImage {
    guard let context = CGContext(
        data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw NSError(domain: "HucksSnipNClipAppIcon", code: 1,
                      userInfo: [NSLocalizedDescriptionKey: "Could not create icon bitmap."])
    }
    // Finder cannot tint an application icon the way the menu bar tints a template image. Give
    // the fixed black mark a neutral light tile so it remains visible against both pale and dark
    // desktops while staying entirely grayscale.
    let tileInset = max(0.5, CGFloat(side) / 128)
    let tileRect = CGRect(x: tileInset, y: tileInset,
                          width: CGFloat(side) - tileInset * 2,
                          height: CGFloat(side) - tileInset * 2)
    let tile = CGPath(
        roundedRect: tileRect,
        cornerWidth: CGFloat(side) * 0.22,
        cornerHeight: CGFloat(side) * 0.22,
        transform: nil)
    context.addPath(tile)
    context.setFillColor(gray: 0.88, alpha: 1)
    context.fillPath()
    context.setFillColor(gray: 0.04, alpha: 1)

    if side == 16 {
        // Pixel-aligned small-size master, not the 64-unit mark shrunk down.
        context.setShouldAntialias(false)
        context.fill(CGRect(x: 1, y: 1, width: 6, height: 14))
        context.fill(CGRect(x: 9, y: 1, width: 6, height: 14))
        context.fill(CGRect(x: 7, y: 6, width: 2, height: 4))

        context.setBlendMode(.clear)
        for point in [(2, 3), (3, 4), (4, 5), (5, 6), (6, 7),
                      (2, 12), (3, 11), (4, 10), (5, 9), (6, 8)] {
            context.fill(CGRect(x: point.0, y: point.1, width: 1, height: 1))
        }
        for x in [10, 13] {
            for y in [3, 7, 11] {
                context.fill(CGRect(x: x, y: y, width: 1, height: 2))
            }
        }
    } else {
        let scale = CGFloat(side) / 64
        context.scaleBy(x: scale, y: scale)
        let path = CGMutablePath()
        let points: [CGPoint] = [
            CGPoint(x: 6, y: 6), CGPoint(x: 29, y: 6), CGPoint(x: 29, y: 27),
            CGPoint(x: 35, y: 27), CGPoint(x: 35, y: 6), CGPoint(x: 58, y: 6),
            CGPoint(x: 58, y: 58), CGPoint(x: 35, y: 58), CGPoint(x: 35, y: 37),
            CGPoint(x: 29, y: 37), CGPoint(x: 29, y: 58), CGPoint(x: 6, y: 58)
        ]
        path.addLines(between: points)
        path.closeSubpath()
        context.addPath(path)
        context.fillPath()

        if side >= 32 {
            // Clear alpha so Finder's background forms the chosen cut path and film edge.
            context.setBlendMode(.clear)
            context.setLineWidth(4)
            context.setLineCap(.square)
            context.move(to: CGPoint(x: 9, y: 15))
            context.addLine(to: CGPoint(x: 26, y: 29))
            context.move(to: CGPoint(x: 9, y: 49))
            context.addLine(to: CGPoint(x: 26, y: 35))
            context.strokePath()
            for x in [38.0, 50.0] {
                for y in [13.0, 28.5, 44.0] {
                    context.fill(CGRect(x: x, y: y, width: 5, height: 7))
                }
            }
        }
    }

    guard let image = context.makeImage() else {
        throw NSError(domain: "HucksSnipNClipAppIcon", code: 2,
                      userInfo: [NSLocalizedDescriptionKey: "Could not finish icon bitmap."])
    }
    return image
}

for size in [16, 32, 128, 256, 512] {
    for scale in [1, 2] {
        let name = "icon_\(size)x\(size)" + (scale == 2 ? "@2x" : "") + ".png"
        let url = output.appendingPathComponent(name)
        let image = try render(side: size * scale)
        guard let destination = CGImageDestinationCreateWithURL(
            url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            fatalError("Could not create \(url.path)")
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else {
            fatalError("Could not encode \(url.path)")
        }
    }
}
print("Generated the ten H icon representations in \(output.path)")
