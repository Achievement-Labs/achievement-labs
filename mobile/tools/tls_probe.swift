import Foundation
// Unauthenticated HEAD probes through the actual production trust delegate.
// No user accounts, event tokens or event data. TLS must validate for v20.
let session = XboxSessions.shared
let group = DispatchGroup()
let lock = NSLock()
var collectorValidated = false
for host in ["mobile.events.data.microsoft.com","v20.events.data.microsoft.com","achievements.xboxlive.com","presence-heartbeat.xboxlive.com"] {
    group.enter(); var request = URLRequest(url:URL(string:"https://"+host+(host.contains("events.data") ? "/OneCollector/1.0/" : "/"))!)
    request.httpMethod = "HEAD"; request.timeoutInterval = 15
    session.dataTask(with:request) { _,response,error in
        if let error { let e=error as NSError; print("\(host): \(e.domain) \(e.code)") }
        else {
            print("\(host): TLS accepted, HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")
            if host == CollectorTrust.host, response is HTTPURLResponse { lock.lock(); collectorValidated = true; lock.unlock() }
        }
        group.leave()
    }.resume()
}
let finished = group.wait(timeout:.now()+25) == .success
session.invalidateAndCancel()
lock.lock(); let verified = collectorValidated; lock.unlock()
guard finished && verified else { fatalError("Production collector trust did not establish authenticated TLS") }
print("Production collector TLS verified. No event requests sent.")
