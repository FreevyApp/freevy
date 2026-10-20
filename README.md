# Freevy

<img src="design/logo/freevy-icon.png" alt="" width="64" align="right">

A free, modern text expander for Windows. Type a short abbreviation, get the whole sentence: signatures, addresses, replies, code, typo fixes. Freevy works fully offline; a free account adds end-to-end encrypted sync between your computers.

**[Download for Windows](https://freevy.app/download)** · [freevy.app](https://freevy.app) · [Privacy](https://freevy.app/privacy)

## Features

- Expands abbreviations in any Windows app, after a space or punctuation or instantly, with case adaptation (`teh`, `Teh`, `TEH`).
- A modern window with search, folders and many themes; lives in the tray and starts with Windows.
- Protected snippets for passwords: encrypted with a master password, unlocked for a few minutes at a time.
- Scheduled backups to a local folder, a synced folder (Google Drive, OneDrive, Dropbox) or an SFTP server.
- Optional free account: end-to-end encrypted sync with version history, and a web snippet manager. The server only ever stores data it cannot read.
- Imports an existing Breevy library on first start.
- Updates itself in the background.

## Security model

Snippets are encrypted on your PC before they are synced, with a key that comes from your password and never leaves your devices. The server stores ciphertext, a login verifier it cannot reverse, and your data key wrapped so only your password or recovery key can open it. Signing in through the browser hands the key to the app over an ECDH-sealed one-time link. The full design, with test vectors shared by the app and the website, is in [docs/crypto.md](docs/crypto.md); the sync protocol is in [docs/sync-design.md](docs/sync-design.md).

The app has no telemetry, no crash reporting and no ads. It contacts freevy.app only to sync (when you sign in) and to check for updates.

Found a security problem? See [SECURITY.md](SECURITY.md).

## Build

Requirements: Windows 10 or 11, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and the WebView2 runtime (included in current Windows).

```powershell
dotnet test
dotnet run --project src/Freevy.App -c Release
```

`--data <folder>` uses another data folder (handy for testing), `--tray` starts without opening the window.

Release builds: `build/release.ps1 -Version x.y.z` publishes the app and packs it with [Velopack](https://velopack.io) into `Freevy-win-Setup.exe` plus update packages (needs `dotnet tool install -g vpk`). `build/make-icon.ps1` redraws the icon from the shapes in `design/logo/`.

## Layout

| Path | Contents |
|---|---|
| `src/Freevy.Core` | Snippet model, matching engine, data store, vault, backups, sync engine and account crypto, Breevy importer |
| `src/Freevy.App` | Windows host: keyboard hooks and `SendInput` output, tray, WebView2 window, sync and update managers |
| `src/Freevy.App/ui` | The window's interface: plain HTML, CSS and JavaScript, no framework |
| `tests/Freevy.Core.Tests` | Unit tests, including crypto vectors produced by the website's JavaScript |

## Your data

Everything lives in `%APPDATA%\Freevy\`: `snippets.json`, `settings.json` (secrets such as the SFTP password are encrypted for your Windows user with DPAPI), `vault.json` (master password check), backups and usage counts. Uninstalling Freevy keeps this folder.

## Code signing

Windows releases are being moved to signing with an Open Source code signing certificate issued to Ozan Kurt, the maintainer; until then they are unsigned. Releases are built from tagged commits of this repository (the [release workflow](.github/workflows/release.yml) builds the same tag on GitHub Actions), each release's checksums are on the [download page](https://freevy.app/download), and installed apps verify every update against its checksum. The only automatic network connection is the update check, which can be turned off in Settings. Details: [freevy.app/code-signing](https://freevy.app/code-signing).

## License

[GNU GPL v3](LICENSE). You may use, study, change and share Freevy; versions you distribute must stay open under the same license. The freevy.app website and sync server are a separate, closed project.

Breevy and aBreevy8 are names of their respective owners; Freevy only reads their file format so you can bring your own abbreviations along.
