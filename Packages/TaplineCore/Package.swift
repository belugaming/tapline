// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TaplineCore",
    platforms: [.macOS("26.0")],
    products: [
        .library(name: "TaplineCore", targets: ["TaplineCore"]),
    ],
    targets: [
        // Realtime audio kernel. Written in C so the IO callback never touches
        // the Swift runtime (no ARC, no allocation, no locks).
        .target(
            name: "TaplineDSP",
            linkerSettings: [.linkedFramework("CoreAudio")]
        ),
        .target(
            name: "TaplineCore",
            dependencies: ["TaplineDSP"],
            linkerSettings: [.linkedFramework("Accelerate")]
        ),
        .testTarget(
            name: "TaplineCoreTests",
            dependencies: ["TaplineCore", "TaplineDSP"]
        ),
    ]
)
