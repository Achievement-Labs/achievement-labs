# Automation implementation

This change implements the first working iteration in AchievementLabs.Core and the Avalonia Desktop app. Event tokens are reusable across titles; source title is provenance only. Xbox PC app remains the authorization source. Game Bar is deferred as an optional provider experiment.

## Build Queue

Open Auto unlocker → Build Queue. Enter the reference gamertag, decimal title ID, and speed; Add game builds a separate achievement checkpoint and appends the title. Add more games during a run or while it is waiting. Start build queue processes titles in order and remains running when empty until Stop is requested. Each active title is spoofed during its run. After cleanup stops that title, a configurable buffer (60 seconds by default) elapses before the next title starts spoofing and submitting achievements. Idle time counts toward the buffer; the first title has no initial buffer. Stop during the gap leaves the next job Pending. Save Settings persists the buffer, and it is fixed for the current run.

The manifest lives under the current Windows user's AchievementLabs local application data in `build-queue/manifest.json`; checkpoints live in its `jobs` subfolder, named by job UUID. Neither contains credentials. Manifest and checkpoint saves use temporary files followed by replacement. Each checkpoint has an owning XUID. The worker processes only jobs for its connected account. Duplicate pending/running/paused titles are rejected. Active jobs cannot be removed; pending jobs can be removed and appended again to change their order. Completed and failed rows remain visible.

On restart interrupted Running/WaitingForToken jobs become Paused. Use Resume / retry selected to queue that job again from its saved checkpoint, then Start if the worker is stopped. Failed requests remain at their current achievement; they are not skipped or automatically retried. Event payload progress is checkpointed after each accepted payload, so an accepted prefix is not deliberately replayed. An ambiguous transport failure requires an explicit retry; that retry may repeat a request whose server acceptance could not be observed. Completed means requests were accepted, not independently verified achievement progress.

The original single-game queue remains available. Old saved single-game sessions without an owning XUID require rebuilding before resume. Starting a build queue is blocked while another queue or standalone presence runs. Game configuration for each queued title is captured when added; later edits to the enqueue fields do not change existing jobs.

## Manual event tokens

Event tokens are entered manually in Settings and saved using the Windows-user protected token store. Automatic token retrieval has been removed.

## Token maintenance

The desktop checks once per minute. Xbox PC authorization is reacquired and profile-validated every 15 minutes, or within five minutes of a known expiration. OAuth authorization is renewed using its active profile's refresh response, independently of event-token acquisition. Rotated refresh responses are retained immediately, even if a subsequent Xbox authorization step fails, and persisted for native saved/remembered OAuth sessions. Legacy WPF JSON sign-ins are not rewritten. Manual authorization cannot renew itself and is labeled accordingly.

Renewal requires the same XUID; it never silently changes the connected account. A session generation check prevents an outdated result from reconnecting a replaced/disconnected session. Client replacement holds the desktop request semaphore, and workflow clients pick up changed XAUTH at their next request. Failed renewal retries after one minute. Queue credential checks also run before submissions and periodic heartbeats, so a long queue does not postpone renewal until completion. Authentication failure of a title submission is preserved for explicit retry rather than replayed automatically.

Known event expiration produces a stale-token notice at the validator's existing one-minute safety cutoff. Opaque tokens retain unknown expiration until Xbox rejects them. Server rejection is retained until replacement/removal. Event queues enter WaitingForToken, preserve the achievement checkpoint, and resume after a usable replacement is available; Stop cancels this wait. An explicitly rejected event payload is retried only after replacement, while a transport failure is not automatically replayed.

## Title presence

Standalone spoofing reacquires Xbox PC authorization and validates its account before every five-minute heartbeat. Auto unlock presence uses the maintained workflow authorization. Initial queue heartbeats are inside cleanup coverage. Stop-heartbeat failures cannot prevent resetting runner state. A failed or rejected stop request halts the build queue before the next title starts, preserving the next job as Pending. Heartbeats use elapsed time and are maintained during achievement delays and event-token waits when authorization and network access remain available.

Live presence acceptance is still required: observe the expected title from another Xbox client, confirm renewal during a long session, and test stop, source restart, and account mismatch. The legacy HTTP spoof route remains disabled and is not part of the updated UI flow. Game Bar remains a future comparison, not an implementation dependency.

## Validation

- Desktop development build and compiled Avalonia bindings.
- `dotnet run --project tests/AutomationChecks/AutomationChecks.csproj -- outputs/automation-checks`: token freshness, refresh scheduling, append during execution/idle, duplicates, active removal, cancellation, restart recovery, account isolation, explicit failure handling, corrupt-manifest preservation, handoff ordering and buffer timing, cancellation during the buffer, and halting on presence cleanup failure.
- Existing desktop offline smoke checks passed, including import/filter/search, protected session storage, catalog checks, and native rendering. The Build Queue tab was rendered and visually checked. No account calls were made.
- No release package, installer, publication, or live Xbox unlock is produced by this development work.