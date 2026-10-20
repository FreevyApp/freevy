# Freevy

A small Windows text expander. It keeps its own snippets and can import an existing Breevy library once, so Breevy abbreviations keep working without Breevy. Built from Breevy's observable behaviour and data format, without using its code; the format notes live in `../Breevy/docs`.

## Layout

- `src/Freevy.Core`: snippet model, matching engine, JSON data store, and the Breevy importer (`Import/Breevy`).
- `src/Freevy.App`: tray app with global keyboard and mouse hooks and `SendInput` output.
- `tests/Freevy.Core.Tests`: unit tests on a synthetic fixture.

## Data

Everything lives in `%APPDATA%\Freevy\`:

- `snippets.json`: abbreviations, expansions, folders, trigger and case modes. Protected snippets have an empty `Expansion` and an `Encrypted` value instead.
- `settings.json`: typing, backup and security settings. The SFTP password and key passphrase are stored encrypted for the current Windows user (DPAPI).
- `vault.json`: salt, iteration count and verifier for the master password. Without it protected snippets cannot be decrypted.
- `backup-state.json`: when the last backup ran and its result.
- `backups\`: local backups (default location).
- `snippets.<timestamp>.bak.json`, `vault.<timestamp>.bak.json`: copies written before an import or a master password change.

On first start, when `snippets.json` does not exist yet and `%APPDATA%\Breevy\abbreviations.bvy` does, the Breevy library and its delimiter settings are imported automatically. After that Breevy's files are never read again unless you choose File > Import from Breevy. Edits to the JSON files are picked up automatically.

## Protected snippets and the master password

Tools > Set master password creates `vault.json` and offers to protect the snippets in folders whose name contains "password". Protected expansions are encrypted with AES-256-GCM; the key is derived from the master password with PBKDF2-SHA256 (600,000 rounds) and only kept in memory while unlocked. It is wiped when the unlock time runs out (Settings > Security, default 5 minutes), on Tools > Lock (Ctrl+L) and, by default, when Windows is locked.

Typing a protected abbreviation while locked opens the unlock prompt; after unlocking, the expansion is typed into the original window. There is no recovery: a forgotten master password makes protected snippets unreadable, also in backups.

## Backups

A backup is a zip with `snippets.json` and `vault.json` (protected snippets stay encrypted; other snippets are plain text). Settings > Backups configures:

- when: every N hours and/or a few minutes after snippets change, plus File > Back up now (Ctrl+B);
- where: the local folder, a synced folder (Google Drive for Desktop, OneDrive, Dropbox) and an SFTP server, each keeping the newest N backups;
- SFTP login by password and/or private key. "Test connection" shows the server's SHA-256 host key to trust; later backups refuse a different key.

File > Restore from backup replaces snippets and vault from a zip after saving the current state as a `before-restore` backup.

## Build and run

```powershell
dotnet test
dotnet run --project src/Freevy.App -c Release
```

Quit Breevy first, otherwise both programs expand the same abbreviation. `--data <folder>` uses a different data folder (handy for testing), `--tray` starts without opening the window.

A single-file build:

```powershell
dotnet publish src/Freevy.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Tray menu

Left-click opens the window. The menu toggles expansion, locks or unlocks protected snippets, runs a backup, reloads the files and opens the data folder.

## Not yet

Google Drive API upload (OAuth), accounts with server-side sync, macros, multi-line expansions in Breevy imports, per-application rules.
