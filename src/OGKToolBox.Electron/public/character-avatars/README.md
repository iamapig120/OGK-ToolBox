# Character avatars

This directory is part of the Electron public asset bundle. Each `1000.png` through
`1016.png` is the first available character face sprite, exported from the matching
`anm_chara_*` Unity bundle.

Regenerate the bundled portraits from a local game installation before making a release:

```powershell
dotnet run --project tools/ExportCharacterAvatars -- "D:\Games\SDDT"
```

Vite copies this directory into `dist`, and `electron-builder` packages `dist` with the
application, so the role list does not read portraits from the user's game directory.
