# Release versions

Windows stable releases use `vMAJOR.MINOR.PATCH`, following [Semantic Versioning](https://semver.org/):

| Change | Example after v1.0.9 |
|---|---|
| Compatible bug fixes only | v1.0.10 |
| New compatible features | v1.1.0 |
| An incompatible change to the supported public contract | v2.0.0 |

When MINOR increases, PATCH resets to zero. When MAJOR increases, MINOR and PATCH
reset to zero. Version components are integers, not decimal places: patch 10 is
valid and does not itself require a minor bump. Choose the version based on the
change's meaning; never modify the contents of an already published version.

The platform-library additions and backward-compatible catalog refactor make
v1.1.0 the next Windows feature release. Desktop assembly/file versions retain a
fourth numeric build revision for the existing updater, e.g. `1.1.0.0`; the public
release tag remains `v1.1.0`. A later public hotfix is normally `v1.1.1`, rather
than silently replacing the v1.1.0 executable.

Local test labels such as `1.0.9-platform-test.17` identify test builds, not 17
stable patch releases. Drafts are not published. Prereleases are also excluded
from the current stable Windows updater. Its update.json uses a four-part numeric
version and the executable's exact final size and SHA-256. If manually distributed
release candidates use a higher fourth component than zero, choose a final build
revision greater than those candidates so their numeric updater can advance.

Historical four-component Windows tags remain understood by the updater. Android,
iOS, macOS preview and Linux preview versions have separate release tracks; the
Windows minor version does not automatically promote those previews to stable.
