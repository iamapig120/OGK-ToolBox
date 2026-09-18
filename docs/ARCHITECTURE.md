# Architecture

Electron is the only maintained desktop client. The deprecated WPF project remains compile-compatible only.

## Resource service

Electron Main starts the self-contained .NET API on loopback with a random per-process session token.
Application services handle scanning, configuration, resource access, backups and exports. The renderer
uses typed IPC and does not receive the API session token. Game data stays in the selected installation.

## Controller backends and providers

The renderer uses one controller snapshot and command interface. `electron/controller-registry.ts`
registers the available backends; `electron/controller-hub.ts` chooses which one supplies that snapshot
and receives ordinary commands.
An online selection is retained when another device appears. When multiple backends report connected
devices, the controller page shows their names as clickable tags at the top right; one device is shown
as a static name. Disconnected backends are omitted. If the active device disconnects, selection can
fall back to another connected backend, including after a previous manual choice.
Release and shutdown handling also covers inactive providers that may still own input resources.
Before changing backends, the previous running provider must confirm `release-all` with `Verified`.
If release fails or times out, the current selection is retained.

Each device implementation runs in a separate provider process:

| Backend | Process | Device access |
| --- | --- | --- |
| `builtin` | `resources/controller/OGKToolBox.ControllerHost.exe` | Existing C# controller implementation |
| `io4` | `dist-electron/electron/providers/io4-provider.js`, launched in Electron's Node mode | `node-hid` via `electron/simgeki-io4-controller.ts` |
| Explicit external provider | Module selected by `OGK_CONTROLLER_MODULE_DIR` | Developer-supplied Node adapter or native executable |

Electron Main supervises these processes and communicates over loopback HTTP/SSE; it does not open
the IO4 HID device itself. The built-in IO4 provider uses the same SDK transport offered to external
Node providers. The C# Host retains its existing implementation and application API v1 contract.
Sharing the wire contract does not require all implementations to use the Node SDK.

Setting `OGK_CONTROLLER_MODULE_DIR` is an exclusive override: only the selected external provider is
started, so the bundled backends do not also access its devices. Without an override, backend selection
routes the active view and commands; it is not a mixer of several devices into one input snapshot.

The manager validates the manifest and ready/health handshake before exposing state. Input is released
on focus loss and shutdown, failed processes can be restarted, and providers are stopped before an
application update. Resource browsing remains independent of controller availability.

The v1 snapshot may optionally declare `inputModes` with a current string ID and labelled options.
The manager presents mode options consistently to the UI, but sends the HTTP `input-mode` command only
when the provider itself declares those options. For older providers it maps the UI's `native` and
`keyboard` IDs to the existing boolean `mode` command; the supplied C# Host is not required to implement
the new optional command.
See [SimGEKI / IO4](SIMGEKI-IO4.md) for that adapter's device protocol and validation scope.

## Packaging

CI builds/tests .NET application code, verifies controller artifact hashes, tests/type-checks/builds Electron,
and packages the controller providers plus the API runtime. `build:main` compiles the main-process and
provider code and copies the SDK runtime into `dist-electron/sdk`. The package keeps the IO4 entry point,
its driver, the SDK runtime and required HID dependency files accessible outside the ASAR archive.
This layout must also be checked in the packaged application, not just in a development checkout.

An application update replaces the installed application components together. User indexes, caches and
backups in game directories remain separate. Runtime code does not fetch or compile controller sources.
