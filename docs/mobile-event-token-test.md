# Mobile event-token experiment

This is an opt-in test build, not a published app release. Public fork .15 remains unchanged.

## Using it

1. Install Android 0.6.7 (15) or sign/sideload iOS 0.12.9 (27).
2. Sign in with Microsoft directly on that device. On iOS choose Xbox iOS and enable Remember sign-in. A PC QR/manual session has no mobile renewal grant and cannot run this experiment.
3. Stop any queue/presence session and keep the app open.
4. Open Settings → Account → Event token → Test & save mobile event token.
5. Report only the result text. Do not share a token, sign-in URL, QR transfer, account export or credential screenshot.

On success, Microsoft has issued an event token with matching user claims and an unexpired NotAfter value. The app saves it in the same protected credential store used by normal sign-in. This does not prove the collector will accept achievement events. The test never submits an achievement, presence heartbeat, telemetry event or event-collector request.

On refusal, the app reports the stage and HTTP status, stops without retrying alternative app/device identities, and retains the prior event token. Microsoft refresh-token rotation is saved before further exchanges so an unsuccessful Xbox request does not unnecessarily break remembered sign-in. Queue contents are not changed.

## Exchange and validation

The experiment uses the existing native mobile browser grant, renews its Microsoft credential, registers a fresh P-256 proof key with the actual mobile device type (Android/iOS), requests Xbox user authorization, then requests XSTS identity and events relying parties. A read-only Xbox profile request confirms the connected XUID before requesting the event token. Event user-hash/XUID claims, expiry and token formatting are validated before replacement. No token values or raw server error bodies are exposed in diagnostics or CI logs.

The Android Microsoft client configuration is the existing browser sign-in configuration. iOS explicitly requires its existing Xbox iOS profile; the experiment does not silently switch to the Windows profile or read other apps' credential stores. Server acceptance is unknown until the user runs the flow on a signed-in mobile device.

## Test coverage

Synthetic transports cover successful issuance, HTTP refusal, wrong account, wrong event identity, missing/expired dates, imported sessions without renewal credentials, refresh rotation, preservation of the old token, and the absence of achievement/presence/collector calls. CI validates native compilation and existing app flows without personal accounts. A real Microsoft-issued mobile token remains a separate user device test.
