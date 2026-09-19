// swift-tools-version:5.9
import PackageDescription

// Huck's Snip 'n' Clip - macOS. Built with Swift Package Manager rather than an Xcode project so
// the whole app builds from the command line with only the Command Line Tools installed.
let package = Package(
    name: "HucksSnipNClip",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "HucksSnipNClip",
            path: "Sources/HucksSnipNClip",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("AudioToolbox"),
                .linkedFramework("AVFoundation"),
                .linkedFramework("ScreenCaptureKit"),
                .linkedFramework("ServiceManagement"),
                .linkedFramework("Carbon"),
                .linkedFramework("CoreGraphics"),
                .linkedFramework("CoreMedia"),
                .linkedFramework("CoreVideo"),
                .linkedFramework("ImageIO"),
                .linkedFramework("UniformTypeIdentifiers")
            ]
        )
    ]
)
