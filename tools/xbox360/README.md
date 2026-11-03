# Native Xbox 360 build input

Mac and Linux preview builds call `prepare.py` before publishing. The old default
depended on a contributor-fork Windows release asset that now returns HTTP 404.

The default now uses the existing official `desktop-preview-v0.1.0` Linux package:

`https://github.com/Achievement-Labs/achievement-labs/releases/download/desktop-preview-v0.1.0/AchievementLabs-linux-0.1.0.1-x64.tar.gz`

This is a bootstrap input, not the version of the app being built. The archive is
pinned by SHA-256 and bounded to 96 MiB. Preparation reads only its exact regular
`AchievementLabs-linux-x64/AchievementLabs.Core.dll` entry in memory, with a 256 MiB
assembly limit. It never extracts the package to the filesystem or executes a
downloaded executable.

The preparer requires exactly one valid 556-byte shared resource, checks its
certificate/prime relationship, and verifies its independent approved fingerprint.
The official bootstrap package came from the previously validated native build
that explicitly embedded the user-authorized shared identity. Stable Windows
releases are not interchangeable bootstrap inputs: they may omit this raw embedded
resource and instead accept session/local-build material.

Generated `shared/signing.dat` and the fictional `shared/synthetic.dat` remain
Git-excluded build inputs. Do not publish raw signing material, private profiles,
custom keyvaults or account credentials. Native packages embed only the approved
shared resource and exclude loose signing files and synthetic profiles.

For an offline build, save the checksum-matching official archive and run:

```text
python tools/xbox360/prepare.py --archive PATH_TO_SAVED_ARCHIVE
```

The legacy `--exe` option remains an offline compatibility route for the original
checksum-pinned Windows fork build; it does not perform any legacy network fetch.
No arbitrary executable, package or replacement identity is accepted. A future
bootstrap rotation must update the archive pin deliberately and verify the same
approved resource before changing its independent fingerprint.

Checks:

```text
python -m unittest discover -s tools/xbox360 -p test_prepare.py
node tools/xbox360/shared/core-checks.cjs
```

## GPD catalog

Desktop and mobile apps add games to a loaded profile from the public GPD catalog on the
`gpd-catalog` branch (served through `raw.githubusercontent.com`). Apps check each download's size
and SHA-256 against `index.json`, convert it to the locked catalog form (`Gpd.template` in
`shared/profile-core.js`, mirrored byte-for-byte by `Xbox360GpdTemplate.cs`), insert it as a new
STFS file, and re-sign. The console adds the dashboard record on its next sign-in.

Publish GPDs with `node tools/xbox360/gpd-catalog.cjs add CATALOG_DIR TITLEID.gpd ...` and check with
`verify`. To create the branch the first time:

```text
git switch --orphan gpd-catalog
git rm -rf --cached . && git clean -fdx
cp -r tools/xbox360/gpd-catalog-branch/. .   # from a main worktree
node PATH_TO_MAIN/tools/xbox360/gpd-catalog.cjs add . YOUR_GPDS/*.gpd
git add -A && git commit -m "Seed GPD catalog"
```

Opt-in contributions export a locked copy locally and open a prefilled `gpd-contribution` issue;
nothing is uploaded automatically.
