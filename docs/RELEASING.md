# Releasing

Electron + NSIS is the only shipped desktop client. The controller is a precompiled proprietary component;
release builds never clone or compile its private implementation.

1. Install .NET 10 SDK and Node.js 24 on Windows x64.
2. Run `tools/Get-Vgmstream.ps1` to obtain the pinned audio decoder.
3. Run `tools/Build-Release.ps1`. Version defaults to the Electron package version.
4. Verify the installer and relevant device workflows before publishing a functional release.
5. To publish, explicitly run `tools/Build-Release.ps1 -Publish` with GitHub CLI installed and authenticated.

The build runs application tests, locked npm install, controller artifact validation, Electron tests,
typechecking and installer generation. It checks the packaged API runtime and exact controller files.
The precompiled bundle version must match the app; use a compatible maintainer-supplied bundle when
updating the app version. Do not bypass compatibility checks by changing metadata alone.

`src/OGKToolBox.Electron/resources/controller/artifact.json` records the required SHA-256 hashes.
The closed binary and its license are included in the installer. Controller source tests and hardware
protocol checks are maintained privately and are not included in this repository.

Published releases go to `lynshp/OGKToolBox-releases` and contain an installer, its `.blockmap`, and
`latest.yml`. Their filenames and SHA-512 metadata must match. The publishing script never deletes an
existing release. A `v*` source tag triggers the release workflow; CI uses the `RELEASES_GITHUB_TOKEN`
secret for the releases repository. Secret values must never be embedded in source or artifacts.

User profiles, game indexes, local logs, private development directories, and history backups are not
release inputs. Bundled third-party files retain their own license terms; see `THIRD-PARTY-NOTICES.md`.
