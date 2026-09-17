# SimGEKI / IO4 communication

The IO4 provider reads matching SimGEKI and IO4-compatible devices through `node-hid` in a separate
Node process. Electron Main supervises it through the same application API v1 used by other controller
providers; it does not open the HID device itself. The provider entry point is
`electron/providers/io4-provider.ts`, with device logic in `electron/simgeki-io4-controller.ts`.

The controller UI selects the active backend. An online selection is not displaced when another device
appears. `OGK_CONTROLLER_MODULE_DIR` selects an external provider exclusively, so the bundled IO4 reader
does not also open a device being tested by that provider. See [provider integration](CONTROLLER-PROVIDERS.md).

## Device matching

| Device mode | VID:PID | HID collection |
| --- | --- | --- |
| SimGEKI native | `8088:0101` | Gamepad: Usage Page `01`, Usage `04` |
| IO4-compatible | `0CA3:0021` | Gamepad: Usage Page `01`, Usage `04` |
| SimGEKI configuration | `8088:0101` | Vendor: Usage Page `FF00` |

Other gamepads are ignored. Matching these identifiers is only the discovery step; a device must also
implement the report layout below. Additional hardware identities need their own documented matching and
protocol validation, rather than being assumed compatible from a product name alone.

## IO4 input report

Input report ID is `0x01`. Offsets below are relative to the payload after the report ID.

| Offset | Size | Meaning |
| --- | --- | --- |
| `0` | 2 | Roller/lever, unsigned 16-bit little-endian; center is `0x8000` |
| `24..27` | 4 | Coin state/count pairs (not currently exposed in the common controller snapshot) |
| `28..33` | 6 | Button matrix |

Button bits are mapped in `electron/simgeki-io4-controller.ts`. Left Side and Right Side are active-low;
the other mapped inputs are active-high. The lever is inverted and scaled into the UI's `0..1023` range.

## SimGEKI configuration report

Configuration uses report ID `AA` with a 63-byte payload. Payload byte 1 is the command, byte 2 of a
response is success (`01`), and payload byte 3 carries the mode for get/set operations.

| Command | Value | Purpose |
| --- | --- | --- |
| `01` | none | Read input mode |
| `02` | payload byte 3 | Set input mode |
| `81` | none | Persist current configuration |

Modes are `1 = IO4 compatible`, `2 = DLL input`, and `3 = simulated keyboard`. A mode change is shown as
verified only after set, persist, and readback all succeed. The vendor protocol is never sent to a generic
`0CA3:0021` IO4 device.

The application snapshot exposes supported modes through optional `inputModes` string IDs and labels.
This provider uses IDs `"1"`, `"2"` and `"3"` and maps the selected `input-mode` command to the device's
numeric values. Clients treat these IDs as opaque strings rather than applying that mapping to other
hardware. Other v1 providers, including the supplied C# Host, can continue using the existing boolean
`mode` command.

## Build and verification scope

`npm run build:main` compiles the provider into
`dist-electron/electron/providers/io4-provider.js` and includes the SDK runtime under `dist-electron/sdk`.
The Node entry, driver, SDK runtime and `node-hid` dependency must remain loadable in the installed layout.
Use the packaged application to verify native dependency loading as well as the development build.

Parser and fake-device tests check mappings and lifecycle paths without establishing hardware
compatibility. A functional release still needs real-device checks for input, reconnect, mode read/write
and readback. Do not infer support for every device advertising the IO4 IDs from simulated tests.

## Protocol reference

Protocol behavior was independently implemented with the public
[SimGEKI-WebControl](https://github.com/SimDevices-Project/SimGEKI-WebControl) project at commit
`35000a4680e93a933fc0a58c7194ec04f1d5f47d` as a reference; no source file from that AGPL-3.0 project is
bundled in OGKToolBox.
