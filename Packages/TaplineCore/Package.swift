// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "TaplineCore",
    platforms: [.macOS("15.0")],
    products: [
        .library(name: "TaplineCore", targets: ["TaplineCore"]),
    ],
    targets: [
        // Realtime audio kernel. Written in C so the IO callback never touches
        // the Swift runtime (no ARC, no allocation, no locks).
        // Optimised even in Debug: the IO callback and the EQ fitter are hot loops.
        .target(
            name: "TaplineDSP",
            cSettings: [.unsafeFlags(["-O2"])],
            linkerSettings: [.linkedFramework("CoreAudio"), .linkedFramework("Accelerate")]
        ),
        .target(
            name: "TaplineCore",
            dependencies: ["TaplineDSP"],
            swiftSettings: [.unsafeFlags(["-O"], .when(configuration: .debug))],
            linkerSettings: [.linkedFramework("Accelerate")]
        ),
        .testTarget(
            name: "TaplineCoreTests",
            dependencies: ["TaplineCore", "TaplineDSP"]
        ),
    ]
)
