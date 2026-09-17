# Controller module

This directory contains the Windows x64 controller module for OGKToolBox 1.1.8.
Development and installed builds launch `OGKToolBox.ControllerHost.exe`.

- `OGKToolBox.ControllerHost.exe`: self-contained process; executable unchanged from 1.1.7.
- `module.json`: application/module compatibility metadata for 1.1.8, using module API 1 and protocol 1.
- `artifact.json`: SHA-256 hashes of the executable and manifest.
- `LICENSE.txt`: component license terms.

Run `npm run verify:controller` from the Electron project before development or
packaging. Checksums detect mismatched files; they are not digital signatures.
Module compatibility must be verified when updating release metadata.
