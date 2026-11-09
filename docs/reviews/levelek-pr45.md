# Detailed review of levelek1337's PR #45

Reviewed November 9, 2026. [PR #45](https://github.com/Achievement-Labs/achievement-labs/pull/45)
is still open and cannot currently merge cleanly. This review is pinned to head
`18af152e0df1298e2a42f6ac63b9a54ca2899a22`, against PR base
`f59383637738d4e8d6d92d45f8842b876cd80fda`. Reformatting makes some project-file
diffs look larger than their behavioral changes. There are 24 changed paths,
including one generated binary and one file-mode-only change.

The compression idea and removal of unused Mono.Cecil were already taken into
the official app through PR #47, using a different implementation. Do not merge
this PR on top to obtain them a second time. Its .NET retargeting and helper
publishing changes are separate decisions, not prerequisites for compression.

## 1. `AchievementLabs.sln`: 15 additions

Adds the existing Enhancements project to the solution; adds its Debug/Release
configuration mappings for Any CPU, x64, and x86; nests it in the existing source
folder. The project still compiles as Any CPU even when a solution configuration
is named x86/x64. This improves IDE discoverability. It does not add a new runtime
feature or reduce package size. Project references already build Enhancements.
Safe to consider independently, but it does not address users' complaints.

## 2. `Directory.Build.props`: all five additions

| Added property | Exact effect | Assessment |
| --- | --- | --- |
| `AllowUnsafeBlocks=true` | Allows unsafe C# in every inheriting project | Broader than necessary; retain project-specific declarations. No size benefit by itself. |
| Release `DebugType=none` | Stops generating managed debugging symbols | Shrinks symbol artifacts, not the event catalog. Existing release packaging already excludes PDBs. Prefer retaining private build symbols for crash diagnosis. |
| Release `DebugSymbols=false` | Also disables symbol generation | Same tradeoff; largely redundant for our release EXE. |
| `AppendTargetFrameworkToOutputPath=false` | Removes the TFM folder from build outputs | Different framework builds of one project may share output paths. This is an output-layout change, not compression. |
| `AppendRuntimeIdentifierToOutputPath=false` | Removes the RID folder | Windows/Linux/macOS or x86/x64 builds of the same project can collide if the surrounding output root is reused. Keep isolated output paths. |

The last two lines also change paths expected by automation. Our existing CI and
release verification refer to framework-specific paths; copying these settings
would require checking every consumer of those paths.

## 3. `src/AchievementLabs.Core/AchievementLabs.Core.csproj`

Walkthrough in the order the changed file executes:

1. Changes Windows and portable target frameworks from .NET 9 to .NET 8. This is
   a runtime/toolchain migration, not a necessary consequence of event compression.
2. Moves the existing Events source property into the first property group.
   That reorganization alone does not change which JSON files are selected.
3. Adds `CompressEventsBundle` at four build hooks. MSBuild normally executes a
   target once within a project build, but this is an unnecessarily broad set of
   hooks for preparing a resource. The official implementation uses a focused
   resource preparation target.
4. Builds/restores the packer using nested MSBuild, removing RID and SelfContained
   properties. Those exclusions are useful, but other publish properties can
   still leak into the tool build. Our packer uses an independent CLI process.
5. Runs the packer into `src/AchievementLabs.Core/events.deflate`. This is a shared
   source-tree output. Parallel builds/source overrides can overwrite it. Our
   output is under each build's intermediate directory.
6. Replaces thousands of individual JSON resources with one optional compressed
   resource. The optional `Exists` condition can let a build proceed without the
   catalog if generation/resource timing is wrong. Our release verifier compares
   every bundled file with its selected source and fails on a mismatch.
7. Keeps the public totals/unlock-method catalogs and public site list as separate
   resources. Compressing Events does not automatically compress those catalogs.
8. Keeps existing linked source files, project references, and signing material
   conditions. Most of the remaining added XML is reformatting.
9. Removes the unused `Mono.Cecil` package reference. This was worth taking and
   is already in the official app. The PR does not remove every NuGet dependency;
   protected-data, Newtonsoft.Json, and MVVM references remain.

Our implementation preserves the compression benefit with Brotli-per-file inside
a ZIP, deterministic ordering, content-based invalidation, atomic output, shared
readers, and an exact embedded-resource/source check. It also covers the stats
reader, which independently consumed the original resources.

## 4. `tools/CompressEvents/CompressEvents.csproj`: eight new lines

A plain .NET 8 executable with implicit usings and nullable enabled. It exists
only to generate the bundle during the build. The tool does not run on users'
machines, authenticate, access Xbox, or host an API. Changing this tool's target
framework is independent of the shipping app's framework.

## 5. `tools/CompressEvents/Program.cs`: all 69 lines

| Lines at reviewed head | What the statements do | Review |
| --- | --- | --- |
| 1–2 | Import compression and text APIs | Required for the custom binary format. |
| 4–8 | Require at least two arguments; otherwise print usage and return failure | Sensible; extra arguments are ignored. |
| 10–11 | Convert source/output paths to absolute paths | Makes behavior independent of relative working directories. |
| 13–17 | Reject a missing source directory | Good early failure. |
| 19–21 | Enumerate every JSON recursively; prepare a path list and newest timestamp | Enumeration is not explicitly sorted. Output can differ with enumeration order. |
| 23–30 | Exclude paths containing `backup`, normalize separators, retain file mtimes | Excludes any path containing that text, not only backup directories. This is selection logic, not schema validation. |
| 32–36 | Skip work if output mtime is newer than the newest selected input | Important defect: file deletion, preserved timestamps, changed source roots, and restored old files can leave a stale bundle. |
| 38–39 | Create the output directory if needed | Normal setup. |
| 41–43 | Create a fixed `.tmp` output and BinaryWriter | Simultaneous invocations share the same temporary file. Our packer uses unique temporary names. |
| 45–46 | Write `LEV1` magic and the entry count | Defines a custom archive format. |
| 48–55 | Read one JSON fully and Deflate-compress it into memory | Compression is per file, not one cross-file Deflate stream. Both copies coexist temporarily. |
| 56 | Copy the compressed memory buffer into another byte array | Additional transient allocation. |
| 58–61 | Write path, uncompressed length, compressed length, and compressed bytes | The reader must enforce each field's limits; this format has no per-entry checksum. |
| 63 | Flush the binary writer | The surrounding `using` scopes also close the file. |
| 66 | Replace the destination with the temporary file | Good final replacement, but failure cleanup and concurrent-writer handling are missing. |
| 67–68 | Print byte/MB totals | Reports packed bundle size, not total release EXE size or memory usage. |
| 69 | Return success | Normal completion. |

There is no JSON schema check, actual-consumer rendering, or source-content hash
in this packer. A malformed recipe can be compressed successfully. Those are
separate release checks in the official implementation.

## 6. `src/AchievementLabs.Core/events.deflate`: binary inspection

I decoded the committed LEV1 blob rather than treating it as an opaque asset:

| Check | Result |
| --- | --- |
| Compressed blob | 12,984,721 bytes |
| Entry count | 1,135 |
| Expanded bytes | 298,508,758 |
| JSON files under the PR's selected Events source | 1,135 |
| Missing / extra / content mismatches | 0 / 0 / 0 |
| Duplicate names / incorrect recorded lengths / trailing bytes | 0 / 0 / 0 |

Each decompressed entry's Git blob hash matches the JSON file at the reviewed
commit. The committed blob is therefore consistent with that PR snapshot. This
does not make timestamp-only future rebuilds reliable. Generated bundles should
stay out of tracked source and be rebuilt/verified for each release.

## 7. `src/AchievementLabs.Core/LocalEventCatalogue.cs`

The two new imports support Deflate and UTF-8. The new static dictionary is
initialized on first use of the class and stores every compressed file in memory.
`LoadCompressedEntries` reads LEV1 magic, count, then each name/length/data entry.

Statement-level concerns:

- A missing resource or wrong magic silently returns an empty dictionary. Later
  reads then fail through the legacy fallback, which can obscure packaging errors.
- The declared uncompressed length is read and ignored.
- Count and compressed-length values have no explicit bounds.
- `ReadBytes(compLen)` is not checked for a short read.
- Duplicate normalized paths overwrite previous entries.
- All compressed entries are copied into byte arrays up front. They are not
  decompressed until selected, but the compressed catalog is resident in memory.
- A malformed static initialization can affect all future accesses to this class.

`FindResource` becomes `FindLegacyResource`. `HasResource` checks compressed entries
first, then legacy resources. `Resource` normalizes the requested path, inflates
that single entry, decodes UTF-8, and returns it; otherwise it reads the previous
individual resource or throws a missing-file error. Ordinary replacement recipes
are unchanged. This is a storage-layer change, not improved title support.

The PR only updates this consumer. The stats editor's independent resource reads
were the reason we subsequently introduced the shared `BundledEventFiles` reader.
Reverting to this isolated reader would reintroduce that integration risk.

## 8. `src/AchievementLabs.Desktop/AchievementLabs.Desktop.csproj`

This is the most consequential non-compression file:

| Changed block | Effect | Decision |
| --- | --- | --- |
| TargetFramework | Desktop .NET 9 → 8, Windows and portable | Do not copy as a size tweak. |
| AllowUnsafeBlocks | Permits unsafe C# directly | Existing project-specific needs can be handled without a global switch. |
| PublishSingleFile property group | Automatically enables LabsSingleFile and all-content/native extraction | Changes which helper targets run and what extracts at startup. Needs packaging tests for every supported platform. |
| CommunityToolkit.Mvvm reference | Adds a direct reference already available transitively through Core | Dependency declaration change, not dependency removal. No new feature. |
| Resources/content | Reformatting retains logos, stats content, helper inputs, and other content | No functional improvement from indentation changes. |
| BuildNativeHelpers | Restore+Build becomes Restore+Publish; helper outputs move; runtime IDs are explicit | The new helper publishes are framework-dependent. They need their matching runtime even though they are single-file. |
| CopyNativeHelpers | Copies from new helper folders | Must remain consistent with runtime launch paths and clean builds. |
| PublishNativeHelpers | Copies already built helper outputs into publish output | Existing missing-output guard remains useful. |
| BundleReleaseHelpers | Removes the required prepared-helper-directory failure and defaults to an obj folder | Can accept missing or stale helpers. The official release script deliberately prepares and verifies self-contained helpers. |
| RemovePdbsFromRelease | Removes PDBs from publish item lists at several hooks | Core idea already exists in official release packaging. |
| DeletePublishedPdbs | Deletes remaining PDBs after publishing | Redundant if publish selection is correct; private symbols can be kept elsewhere. |
| CleanSingleFilePublishDir | Deletes every publish subdirectory and every file except AchievementLabs.exe | Reject as written. This can hide missing bundle content, erase sidecars/diagnostics, and is Windows-filename-specific. It would be hazardous if enabled for portable single-file builds. |

Current macOS/Linux scripts use `PublishSingleFile=false`, so that last concern
does not claim the existing scripts necessarily execute the destructive target.
It is an incompatibility in the new target's own broad condition.

Our release script already builds the x64 Steam helper and x86 GFWL helper as
self-contained binaries, packages them into the main app, and verifies extraction.
A smaller framework-dependent helper is not equivalent on a machine without .NET.

## 9. `MainWindow.axaml`: both changed lines

The About text changes “Ultimate Xbox Achievement Unlocker” to “Xbox Achievement
Unlocker”. This is a credit wording correction; check the upstream project's
preferred name before changing it independently.

The second change removes the bottom-right “AL / NATIVE DEVELOPMENT” text. This
is now implemented in the release branch because the owner explicitly requested
it. The status/notice text in the same footer remains.

## 10. `MainWindow.axaml.cs`: Windows title-bar block

The Opened handler calls a new helper. The helper exits on non-Windows, retrieves
the HWND, requests immersive dark mode with DWM attribute IDs 20 and 19, then sets
caption color attribute 35 to black (`COLORREF` zero). The imported function is
`DwmSetWindowAttribute`. Exceptions are swallowed; HRESULTs are not inspected.

This changes native window chrome only, not Avalonia content or compression. It
is a reasonable independent cosmetic candidate. A refined version should try
the legacy attribute only if needed, tolerate unsupported caption-color options,
and check readability/high-contrast behavior. It has not been silently included
in the requested release changes.

## 11. .NET 8 compatibility edits

| File | Exact change | Why it exists |
| --- | --- | --- |
| `src/AchievementLabs.App/Services/Xbox360/Xbox360GpdCatalogClient.cs` | Conditional `Convert.ToHexStringLower` vs `ToHexString(...).ToLowerInvariant()` | Preserves lowercase SHA-256 formatting on .NET 8. Does not relax hash verification. |
| `src/AchievementLabs.Core/LanPairing.cs` | Conditional `X509CertificateLoader.LoadPkcs12` vs older certificate constructor | Allows the same exported PFX identity to load on .NET 8. Does not add a hosted sync API. |
| `src/AchievementLabs.Enhancements/EditionMapping.cs` | Wraps DeepEquals; .NET 8 parses both values into JsonNode before comparing | Compatibility allocation/workaround for existing validation, not broader edition support. |
| `tests/AchievementLabs.Enhancements.Tests/GpdCatalogChecks.cs` | Uses uppercase hex plus invariant lowercase | Makes the test fixture compile on .NET 8. No new test scenario. |

All of these are caused by the framework downgrade. None needs adopting while
the application continues on its existing framework.

Microsoft currently lists **both .NET 8 and .NET 9 ending support November 10,
2026**. As of this review, moving from 9 to 8 provides no support-lifetime benefit.
The longer-term runtime candidate is .NET 10 LTS, which needs its own migration
and native-helper/platform test matrix. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy),
[Microsoft end-of-support notice](https://devblogs.microsoft.com/dotnet/dotnet-8-9-end-of-support/).

## 12. Remaining project and mode changes: every path

| Path | Change and consequence |
| --- | --- |
| `src/AchievementLabs.Enhancements/AchievementLabs.Enhancements.csproj` | net9.0 → net8.0; follows the shared runtime change. |
| `src/AchievementLabs.GfwlInjector/AchievementLabs.GfwlInjector.csproj` | net9 Windows → net8 Windows; adds SelfContained=false, PublishSingleFile=true, and native extraction. Keeps x86. A single file still requires the appropriate runtime when framework-dependent. |
| `src/AchievementLabs.SteamIdle/AchievementLabs.SteamIdle.csproj` | Same framework-dependent/single-file changes, for the Steam helper. |
| `tests/AchievementLabs.Enhancements.Tests/AchievementLabs.Enhancements.Tests.csproj` | Retargets the test executable to net8 Windows. Does not add behavioral coverage. |
| `tests/AutomationChecks/AutomationChecks.csproj` | net9 Windows → net8 Windows. |
| `tools/EventCatalogChecks/EventCatalogChecks.csproj` | net9 Windows → net8 Windows. |
| `tools/UpdaterChecks/UpdaterChecks.csproj` | net9 → net8. |
| `tools/lan-pairing-checks/LanPairingChecks.csproj` | net9 → net8; existing pairing checks otherwise unchanged. |
| `tools/queue-verification-checks/QueueVerificationChecks.csproj` | net9 → net8; existing verification checks otherwise unchanged. |
| `tools/macos/build-preview.sh` | File mode 100755 → 100644; no text change. Removes executable permission when checked out on Unix. Do not adopt. |

## What remains worth adding

1. The requested footer removal is now included. The underlying compression and
   Mono.Cecil removal were already adopted in a verified form.
2. Adding Enhancements to the solution is a small IDE improvement, independent
   of shipping behavior.
3. Native Windows dark title-bar styling could be reviewed as an optional UI
   change, independently of package size.
4. Retain symbol exclusion at the release boundary, not destructive cleanup or
   globally disabled debugging information.

There is no new hosted API, catalog classification fix, faster totals lookup,
mobile cloud synchronization, or launcher account integration in this PR.
Those require separate work. No additional PR code was merged during this review.
