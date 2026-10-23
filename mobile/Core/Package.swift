// swift-tools-version: 5.9
import PackageDescription
let package = Package(name: "LabsMobileCore", platforms: [.iOS(.v17), .macOS(.v13)], products: [.library(name: "LabsMobileCore", targets: ["LabsMobileCore"])], targets: [.target(name: "LabsMobileCore"), .testTarget(name: "LabsMobileCoreTests", dependencies: ["LabsMobileCore"])])
