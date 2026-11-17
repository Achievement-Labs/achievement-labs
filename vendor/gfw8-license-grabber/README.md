# GFW8 License Grabber backend

By Alec Breen | TheBreenis: https://github.com/thebreenis/gfw8-license-grabber
Pinned source: b01eb217ce4478dda4d567ccbac9bc2f3ca12271 (GUI 1.10.1 / CLI 1.6.1).

The backend and runtime are retained unchanged, including receipt validation,
installed-game protection, payload hashes, data backups, and helper cleanup.
Achievement Labs invokes them through tools/Acquire-Windows8License.ps1.
Only receipt and token acquisition are invoked; presence and achievement actions
are not part of acquisition. The native helper source is in upstream src/native.
See THIRD-PARTY-NOTICES.md for the runtime components.

Runtime execution copies the backend into a private per-user directory. Receipts,
tokens, and diagnostics are never written into this source or Events/Data.json.
