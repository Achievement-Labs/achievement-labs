import Foundation
import CoreGraphics
import ImageIO
import UniformTypeIdentifiers

let root = URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
let target = root.appendingPathComponent("mobile/App/Assets.xcassets/AppIcon.appiconset")
try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
let sourceURL = root.appendingPathComponent("src/AchievementLabs.App/Assets/achievement-labs-icon.png")
guard let source = CGImageSourceCreateWithURL(sourceURL as CFURL, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
      let context = CGContext(data: nil, width: 1024, height: 1024, bitsPerComponent: 8,
                              bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                              bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else {
    fatalError("Cannot decode the existing application icon or create its drawing surface")
}
context.setFillColor(CGColor(gray: 0, alpha: 1))
context.fill(CGRect(x: 0, y: 0, width: 1024, height: 1024))
context.interpolationQuality = .high
context.draw(image, in: CGRect(x: 0, y: 0, width: 1024, height: 1024))
guard let output = context.makeImage(),
      let destination = CGImageDestinationCreateWithURL(target.appendingPathComponent("Icon.png") as CFURL,
                                                        UTType.png.identifier as CFString, 1, nil) else {
    fatalError("Cannot encode the application icon")
}
CGImageDestinationAddImage(destination, output, nil)
guard CGImageDestinationFinalize(destination) else { fatalError("Cannot save the application icon") }
let contents = #"{"images":[{"filename":"Icon.png","idiom":"universal","platform":"ios","size":"1024x1024"}],"info":{"author":"xcode","version":1}}"#
try Data(contents.utf8).write(to: target.appendingPathComponent("Contents.json"))
try Data(#"{"info":{"author":"xcode","version":1}}"#.utf8).write(to: target.deletingLastPathComponent().appendingPathComponent("Contents.json"))
print("Prepared the existing app icon for iPhone and iPad.")
