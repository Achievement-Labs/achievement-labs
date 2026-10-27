# Windows to Mac local pairing preview

In both apps, connect the same Xbox account. On Windows keep the Xbox PC app signed in.

1. Windows: Settings → Sync → Pair a Mac on this network.
2. Mac: Settings → Sync → Pair with Windows on this network. Discovery starts automatically.
3. Select the Windows app and choose Pair selected PC. Compare the eight-digit code on both screens, then approve on both devices.
4. Optionally select Remember this device on both screens. Recognition is account scoped and protected by Windows DPAPI / the Mac app's Keychain-backed storage. Later connections still require transfer approval, but no file, long key or code entry.
5. Start the spoofer in Mac Game Session. Pairing itself never starts spoofing or an unlock queue.

The pairing window closes its listener after one connection attempt, two minutes, or closing the window. The Mac searches for eight seconds, retrying separately on each private IPv4 interface. Open the Windows pairing window again before retrying. Discovery carries only a random invitation and a port; Xbox authorization travels only inside mutually authenticated TLS after both approvals. During first pairing, certificates establish encryption and proof of private-key possession; the manually compared code authenticates the peer. Both sides commit fresh challenges before revealing them to prevent adaptive code selection. A remembered certificate identifies a previously approved peer; it does not bypass explicit approval or the same-account profile check.

The discovery follow-up (Mac 0.1.0.8 / Windows 1.0.8.17) also remembers the PC's private address when both devices choose Remember after a successful pairing. Future searches query that known address directly over UDP as well as broadcasting, and obtain the current temporary TCP port. This does not scan a subnet or transfer credentials during discovery. Devices from the earlier preview keep their identity and trust; because that version did not save addresses, one manual pairing in the new build may be needed to populate the address. A changed PC address may also require a fresh manual pairing if broadcasts are blocked. TLS device verification and account checks still apply.

Both pairing windows show safe discovery counters. Windows shows requests received/replies sent; Mac shows interfaces/probes/replies and socket-error enums. These counters exclude account identifiers, authorization, pairing keys and endpoint addresses. A failed individual UDP reply no longer stops the Windows responder. Network equipment or firewall policies may still block discovery; manual pairing remains available. The user confirmed manual LAN pairing and Mac spoofing work in the previous preview; the updated discovery itself still needs their network test.

If discovery finds nothing, allow local network access on Mac and allow the Windows app on your private network if prompted. Enter the private IPv4 address and port displayed on Windows as a fallback. This is not a router port-forwarding feature. Guest Wi-Fi, client isolation, firewalls or VPN routing can separate devices. The app does not modify firewall rules, install certificates or enable a network listener on startup. UDP discovery uses port 40761; TCP transfer uses the temporary port shown in the Windows window. IPv6-only networks are not supported in this preview.

Use Forget remembered network devices on either device to remove its local pairing identity and trust entries. A new connection then needs code comparison. Existing imported authorization remains memory-only and can be removed using Remove PC pairing. No Xbox refresh credentials or event tokens are transferred by LAN pairing.

Network pairing is a standard Settings feature with a built-in Guides tab. The artificial twenty-minute spoofer deadline has been removed. Known Xbox authorization expiry is honored; opaque authorization expiry remains unknown. Authorization remains memory-only on Mac and is cleared on disconnect/app close. Remembered devices do not implement unattended renewal. Pair again when Xbox refuses or authorization expires. Token acquisition failures do not trigger alternate-identity or heartbeat retries. See pairing-guide.md for the in-app instructions for PC to Mac and PC to mobile.

The synthetic checks use loopback and fake account authorization, covering mutual TLS, code agreement, either-side rejection, account mismatch, substituted invitation, discovery, bounded framing, duplicate JSON rejection and protected account-scoped trust storage. They do not demonstrate real Mac-to-Windows discovery or successful Xbox heartbeat access; those require the user's two-device test.

This is an unpublished preview of the unofficial fork. Upstream: https://github.com/Achievement-Labs/achievement-labs. App releases: https://github.com/thevampirebat/achievement-labs. Event catalogue routing remains upstream.
