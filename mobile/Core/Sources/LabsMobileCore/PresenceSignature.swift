import Foundation
import CryptoKit

public enum PresenceSignature {
    public static func header(method:String, url:URL, authorization:String, body:Data, key:Data, at date:Date = Date()) throws -> String {
        guard url.scheme == "https",url.host == "presence-heartbeat.xboxlive.com",url.user == nil,url.password == nil,url.port == nil || url.port == 443,url.query == nil,url.fragment == nil,["POST","DELETE"].contains(method),body.count <= 8192 else { throw LabsError.message("Invalid presence signing request.") }
        var version=UInt32(1).bigEndian,stamp=UInt64((Int64(date.timeIntervalSince1970)+11644473600)*10000000).bigEndian
        let v=withUnsafeBytes(of:&version) { Data($0) },t=withUnsafeBytes(of:&stamp) { Data($0) }
        var message=v;message.append(0);message.append(t);message.append(0)
        message.append(Data((method+"\0"+url.path+"\0"+authorization+"\0").utf8));message.append(body);message.append(0)
        var header=v;header.append(t);header.append(try P256.Signing.PrivateKey(rawRepresentation:key).signature(for:message).rawRepresentation)
        return header.base64EncodedString()
    }
}
