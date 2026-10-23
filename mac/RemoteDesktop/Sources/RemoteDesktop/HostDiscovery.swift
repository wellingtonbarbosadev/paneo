@preconcurrency import Foundation
import Darwin

struct DiscoveredHost: Equatable, Identifiable {
    var id: String { "\(name).\(hostname):\(port)" }
    let name: String
    let hostname: String
    let addresses: [String]
    let port: Int
}

@MainActor
final class HostDiscovery: NSObject, @preconcurrency NetServiceBrowserDelegate, @preconcurrency NetServiceDelegate {
    var onHostsChanged: (([DiscoveredHost]) -> Void)?
    private var browser: NetServiceBrowser?
    private var resolving: [NetService] = []
    private var hosts: [String: DiscoveredHost] = [:]
    private var stopped = true

    func start() {
        guard browser == nil else { return }
        stopped = false
        let browser = NetServiceBrowser()
        browser.delegate = self
        self.browser = browser
        browser.searchForServices(ofType: "_paneo._tcp.", inDomain: "local.")
    }

    func stop() {
        stopped = true
        browser?.stop()
        browser?.delegate = nil
        browser = nil
        resolving.forEach { $0.stop() }
        resolving.removeAll()
        hosts.removeAll()
        onHostsChanged?([])
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService, moreComing: Bool) {
        guard !stopped, self.browser === browser, hosts.count + resolving.count < 128,
              !resolving.contains(where: { $0.name == service.name }), hosts[service.name] == nil else { return }
        service.delegate = self
        resolving.append(service)
        service.resolve(withTimeout: 5)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        guard !stopped, self.browser === browser else { return }
        let removed = resolving.filter { $0.name == service.name }
        removed.forEach { $0.delegate = nil; $0.stop() }
        resolving.removeAll { $0.name == service.name }
        if hosts.removeValue(forKey: service.name) != nil { publish() }
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        guard !stopped, resolving.contains(where: { $0 === sender }) else { return }
        let addresses = (sender.addresses ?? []).compactMap(Self.numericAddress)
        let hostname = sender.hostName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard (1...65535).contains(sender.port), !sender.name.isEmpty, !hostname.isEmpty || !addresses.isEmpty else {
            resolving.removeAll { $0 === sender }
            sender.delegate = nil
            sender.stop()
            return
        }
        hosts[sender.name] = DiscoveredHost(name: sender.name, hostname: hostname, addresses: addresses, port: sender.port)
        resolving.removeAll { $0 === sender }
        sender.stop()
        publish()
    }

    func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) {
        guard !stopped else { return }
        resolving.removeAll { $0 === sender }
    }

    private func publish() { onHostsChanged?(Array(hosts.values).sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }) }

    private static func numericAddress(_ data: Data) -> String? {
        data.withUnsafeBytes { raw in
            guard let address = raw.baseAddress?.assumingMemoryBound(to: sockaddr.self) else { return nil }
            guard data.count >= MemoryLayout<sockaddr>.size else { return nil }
            switch Int32(address.pointee.sa_family) {
            case AF_INET where data.count >= MemoryLayout<sockaddr_in>.size: break
            case AF_INET6 where data.count >= MemoryLayout<sockaddr_in6>.size: break
            default: return nil
            }
            var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
            guard getnameinfo(address, socklen_t(data.count), &host, socklen_t(host.count), nil, 0, NI_NUMERICHOST) == 0 else { return nil }
            let bytes = host.prefix { $0 != 0 }.map { UInt8(bitPattern: $0) }
            return String(decoding: bytes, as: UTF8.self)
        }
    }
}
