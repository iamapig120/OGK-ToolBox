# Precompiled controller module

This directory contains the Windows x64 controller module used by OGKToolBox 1.1.7.
Both development and installed builds launch `OGKToolBox.ControllerHost.exe` here;
the source checkout needs no private controller repository or controller compiler.

- `OGKToolBox.ControllerHost.exe`: self-contained process, unchanged from the verified 1.1.7 installer.
- `module.json`: app/module compatibility manifest.
- `artifact.json`: SHA-256 hashes of the executable and manifest.
- `LICENSE.txt`: separate proprietary component terms; this component is not MIT-licensed.

Run `npm run verify:controller` from the Electron project before development or
packaging. A mismatch fails the build instead of silently mixing module versions.
Checksums detect an accidental mismatch relative to the checked-in lock; they are
not a digital signature or a restriction on source-code forks.

Only replace this bundle with a compatible release supplied by its maintainer.
Do not rebuild it from private source in public CI, or update the manifest merely
to bypass a compatibility check. Firmware/HID implementations and hardware notes
are intentionally outside this repository.
