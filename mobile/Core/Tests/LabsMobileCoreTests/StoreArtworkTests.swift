import XCTest
@testable import LabsMobileCore
final class StoreArtworkTests:XCTestCase {
    private func fixture()->[StoreArtwork] {
        StoreArtwork.images(["LocalizedProperties":[["Images":[
            ["Width":1440.0,"Height":2160.0,"ImagePurpose":"Poster","Uri":"//store-images.s-microsoft.com/poster"],
            ["Width":1920.0,"Height":1080.0,"ImagePurpose":"SuperHeroArt","Uri":"https://store-images.s-microsoft.com/hero"]
        ]]]])
    }
    func testOrientationAndSplitViewSelectDifferentArtwork() {
        XCTAssertEqual(StoreArtwork.best(fixture(),width:390,height:844)?.lastPathComponent,"poster")
        XCTAssertEqual(StoreArtwork.best(fixture(),width:1024,height:768)?.lastPathComponent,"hero")
        XCTAssertEqual(StoreArtwork.best(fixture(),width:400,height:768)?.lastPathComponent,"poster")
    }
    func testMissingShapeAndEmptyMetadataAreSafe() {
        XCTAssertEqual(StoreArtwork.best(Array(fixture().suffix(1)),width:390,height:844)?.lastPathComponent,"hero")
        XCTAssertNil(StoreArtwork.best([],width:390,height:844))
        XCTAssertNil(StoreArtwork.best(fixture(),width:0,height:0))
    }
    func testUntrustedOrInsecureImagesAreRejected() {
        for bad in ["http://store-images.s-microsoft.com/x","https://user:secret@store-images.s-microsoft.com/x","https://microsoft.com.attacker.test/x","file:///x","javascript:bad"] { XCTAssertNil(StoreArtwork.secureURL(bad)) }
    }
}
