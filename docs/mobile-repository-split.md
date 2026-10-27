# Mobile source moved

Android, iOS, their build workflows and mobile-only documentation now live at https://github.com/Achievement-Labs/achievement-labs-mobile.

The extraction baseline is b053785ec6e931d02650038c19aa91dae3bcf604. Contributor history remains here; the new repository records that attribution and byte-preserved catalogue provenance. No event mappings or desktop runtime behavior changed in this split.

Keep `tools/mobile`, `.github/workflows/mobile-events.yml`, desktop QR/sign-in transfer, and event sources here: they support existing releases and the shared event feed. New mobile builds check the mobile repository for application updates but keep this repository for event updates. Existing installed versions require a bridge release here to discover a future mobile migration release.

Pending PRs remain open: #22 moves to mobile; #14 needs its mobile and desktop changes separated; #24 retains Mac/Windows pairing changes here and moves mobile guides/mappings to mobile. Before merging #24, adapt its Mac signing/fixture preparation so it no longer depends on the removed `mobile/` tree. A Mac repository decision is separate from this mobile extraction.

Android release signing requires the existing permanent `LABS_ANDROID_*` secrets in the mobile repository. Secret values cannot be read back through GitHub; do not substitute a new signing identity. No release or version bump is part of this source move.
