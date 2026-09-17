# Releasing

Electron + NSIS is the only shipped desktop client. Release builds include the resource API, the supplied
C# controller Host, and the Node IO4 provider with its runtime dependencies.

1. Install .NET 10 SDK and Node.js 24 on Windows x64.
2. Run `tools/Get-Vgmstream.ps1` to obtain the pinned audio decoder.
3. Run `tools/Build-Release.ps1`. Version defaults to the Electron package version.
4. Verify the installed application and relevant device workflows using the checks below.
5. To publish, explicitly run `tools/Build-Release.ps1 -Publish` with GitHub CLI installed and authenticated.

The build runs application tests, locked npm install, controller artifact validation, Electron tests,
typechecking and installer generation. It checks the packaged API runtime and exact controller files.
The supplied Host bundle version must match the app; use a compatible maintainer-supplied bundle when
updating the app version. Do not bypass compatibility checks by changing metadata alone.

`src/OGKToolBox.Electron/resources/controller/artifact.json` records the required SHA-256 hashes.
The Host and its accompanying files are included in the installer. `build:main` also compiles the IO4
provider and copies the SDK runtime into `dist-electron/sdk`. Packaging keeps the provider entry point,
driver, SDK runtime and required HID dependencies accessible outside ASAR.

After generating a Windows directory build, run the packaged controller check from
`src/OGKToolBox.Electron` (CI runs the same check):

```powershell
$env:OGK_CONTROLLER_PACKAGED_DIR = (Resolve-Path '../../artifacts/electron/win-unpacked').Path
node tests/controller-packaged.cjs
```

This loads the packaged native HID binding without opening devices, then substitutes an empty device
list to check the provider handshake, commands, crash recovery and shutdown. It also verifies that the
bundled Host executable is unchanged. It does not exercise the NSIS installer or real hardware.

Before publishing a controller change, verify the installed layout:

- The supplied Host can start, report a snapshot and stop.
- The IO4 provider can load `node-hid` from its installed location and exit cleanly without a device.
- An explicit `OGK_CONTROLLER_MODULE_DIR` starts only that external provider; clearing it restores
  bundled backend selection.
- An online selection is retained when another backend becomes available; manual selection routes the
  displayed state and commands to the intended backend.
- Backend switching proceeds only after the previous running provider confirms input release with
  `Verified`; a rejected, unconfirmed or timed-out release keeps the previous selection.
- With applicable real hardware, input, disconnect/reconnect, mode writes, persistence and readback behave
  correctly. Record hardware and firmware details separately from simulator results.

Passing parser, mock-device or process tests does not establish real-device compatibility. A successful
development launch also does not verify ASAR layout or native dependency loading in the installer.

Published releases go to `lynshp/OGKToolBox-releases` and contain an installer, its `.blockmap`, and
`latest.yml`. Their filenames and SHA-512 metadata must match. The publishing script never deletes an
existing release. A `v*` source tag triggers the release workflow; CI uses the `RELEASES_GITHUB_TOKEN`
secret for the releases repository. Secret values must never be embedded in source or artifacts.

User profiles, game indexes, local logs, private development directories, and history backups are not
release inputs. Bundled third-party files retain their own license terms; see `THIRD-PARTY-NOTICES.md`.
