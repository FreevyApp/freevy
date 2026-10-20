# Google Drive OAuth and account sync: design

Status: proposal, nothing implemented. Covers two follow-ups to the existing backup and master password work: (1) uploading backups straight to Google Drive through its API, and (2) user accounts with server-side snippet sync so a user never loses snippets when a PC dies. Each part ends with open decisions and a recommendation.

## Where we start

Freevy keeps everything in `%APPDATA%\Freevy` as JSON. Backups are zips (`snippets.json` plus `vault.json`) sent to `IBackupTarget` implementations in `src/Freevy.Core/Backup/BackupTargets.cs`: a local folder, a synced folder (Google Drive for Desktop today) and SFTP. Protected snippets are encrypted with AES-256-GCM under a key derived from the master password (`src/Freevy.Core/Security/Vault.cs`), and machine-bound secrets use DPAPI (`src/Freevy.App/Secrets.cs`).

Two gaps matter for sync. Snippets have no stable identity: `Snippet` is a record without an id, so two devices cannot tell "edited" from "deleted and re-added". And deletions leave no trace, so a sync could not tell a deleted snippet from one the other device has not seen yet.

## Part 1: Google Drive upload via OAuth

### Flow

Use Google's installed-app flow ("Desktop app" OAuth client): the app opens the system browser on Google's consent page, listens on a loopback redirect `http://127.0.0.1:<random port>/`, and exchanges the returned code for an access token and a refresh token, using PKCE (S256). The official `Google.Apis.Auth` package does this (`GoogleWebAuthorizationBroker` with a `LocalServerCodeReceiver`). The browser is the user's own default browser, never an embedded web view, which Google blocks for sign-in.

The desktop client secret ships inside the app. Google documents that desktop clients cannot keep a secret, so this is expected; security rests on PKCE and the loopback redirect, not on the secret.

### Scope

Request only `https://www.googleapis.com/auth/drive.file`. It lets the app see and manage only files it created itself, which is all a backup target needs, and Google classifies it as non-sensitive, so no security assessment is required ([Choose Google Drive API scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth)). A side effect: backups uploaded by Freevy on one PC are visible to Freevy on another PC signed in to the same Google account, because both use the same OAuth client. A backup a user copies into the folder by hand is not visible to the app.

### Token storage

Store the refresh token encrypted with DPAPI, same as the SFTP password, in a new `google-token.dat` next to `settings.json`, by plugging a small DPAPI-backed `IDataStore` into the Google library instead of its default `FileDataStore`, which writes plain JSON to the user profile. "Disconnect Google Drive" revokes the token at Google and deletes the file.

### Upload target

A new `GoogleDriveBackupTarget : IBackupTarget` in Core:

1. Find or create a folder named `Freevy backups` (mime type `application/vnd.google-apps.folder`) and remember its file id in settings; if the id no longer resolves, create it again.
2. Upload the zip with a resumable upload (`files.create` with `uploadType=resumable`), so large or flaky uploads can resume.
3. List the folder's children (`'<folderId>' in parents and trashed = false`), apply the existing `BackupArchive.SelectExpired` rule and delete the expired ones.

Restore gets a "From Google Drive..." option that lists the same folder and downloads the chosen zip into a temp file before handing it to `BackupArchive.Restore`.

### Google Cloud setup the owner has to do once

1. Create a Google Cloud project (for example "Freevy") and enable the Google Drive API.
2. Configure the OAuth consent screen (Google Auth Platform): app name, support email, logo optional, user type External, scope `drive.file`.
3. Create an OAuth client of type "Desktop app" and put its client id and secret into the build, for example in an embedded `google-oauth.json`, not in the repository if it is ever made public.
4. Set the publishing status to "In production". In "Testing" status, refresh tokens for an external app expire after 7 days and only up to 100 listed test users can sign in ([Manage App Audience](https://support.google.com/cloud/answer/15549945?hl=en)). With only non-sensitive scopes, verification is not mandatory; brand verification is only needed to show the app name and logo on the consent screen ([OAuth App Verification Help Center](https://support.google.com/cloud/answer/13463073?hl=en)).

Not verified yet: whether Google still shows an "unverified app" warning for a production app that requests only `drive.file`. Google's documentation ties that warning to sensitive and restricted scopes. Check it with the real client before shipping.

### Part 1 decisions

- **Keep the synced-folder target after the API target exists?** Recommendation: yes. It also covers OneDrive, Dropbox and NAS folders, and it costs nothing to keep.
- **One Google client for all users, or bring-your-own client id?** Recommendation: one client owned by the project. Bring-your-own is only worth it if Freevy stays a personal tool.

## Part 2: accounts with server-side sync

### Goals and non-goals

Goals: sign in on a new PC and get every snippet back; edit on two PCs and have both converge; the server cannot read snippets; work fully offline and sync later.

Non-goals for the first version: sharing snippets between users or teams, a web editor, real-time collaborative editing. A web editor conflicts with end-to-end encryption unless it does the crypto in the browser, so it is a later decision.

### Recommended stack

Laravel API on a Forge-managed server, MySQL (or MariaDB), Laravel Sanctum personal access tokens, one token per device. This matches the owner's existing Laravel and Forge setup, so deployment, backups and TLS follow known routines. The server is small: auth, key material, and an item store with revisions. Host it in the EU, since the users are likely in the EU and the server stores account data (email addresses) even though snippets are encrypted.

Alternatives considered: a hosted backend such as Supabase or Firebase (less code, but another vendor and its own data-processing terms) and a self-hosted generic sync server (no account system). Neither saves enough to beat a small Laravel app the owner already knows how to run.

### End-to-end encryption

Recommendation: encrypt every snippet end to end, not only protected ones. Abbreviations, descriptions and ordinary expansions also leak private data (addresses, phone numbers, email templates, account names), and encrypting everything removes a whole class of questions about what the server may see.

Key hierarchy, modelled on how Bitwarden and 1Password separate login from encryption:

1. **Account password.** The user signs up with email and one password. The client derives `masterKey = Argon2id(password, salt = normalized email)` (or PBKDF2-SHA256 with 600,000 rounds, as `Vault` does today, if adding an Argon2 library is unwanted).
2. **Server login.** The client sends `authHash = PBKDF2-SHA256(masterKey, password, 1 round)` instead of the password. The server stores a normal password hash (bcrypt or Argon2) of `authHash`. The server never sees the password or `masterKey`.
3. **Data key.** At sign-up the client generates a random 256-bit `dataKey`, encrypts it with `masterKey` (AES-256-GCM) and uploads only the wrapped key. Every sync item is encrypted with `dataKey`. Changing the password re-wraps `dataKey`; no item has to be re-encrypted.
4. **Device unlock.** After sign-in the device stores `dataKey` wrapped with DPAPI, so expansion keeps working after a reboot without asking for the account password, just as unprotected snippets work today.
5. **Protected snippets keep their extra layer.** A protected snippet's expansion stays sealed with the vault key (the local master password), and `vault.json` is synced as its own encrypted item. So on a stolen but signed-in PC, protected snippets still need the master password. Whether the vault password must equal the account password is an open decision below.

Recovery: with true end-to-end encryption, a forgotten account password means the data is gone, also for the operator. Offer a recovery key at sign-up (a random 128-bit value shown once as 26 base32 characters, also used to wrap `dataKey`) and tell the user to print or store it. The local zip backups remain a second safety net.

What the server still sees: email address, number of items, their sizes, change times and which device changed them. Padding item sizes to fixed buckets (for example multiples of 256 bytes) hides exact expansion lengths cheaply.

### Data model

Client side, before any server work:

- Add `Guid Id` to `Snippet`, generated on create and import, kept on edit. Existing files get ids on first load (and are saved back).
- Track deletions as tombstones (`Id`, deleted-at) in a new `sync-state.json`, together with the server cursor and the set of locally changed ids not yet pushed.

Server tables:

| Table | Columns |
|---|---|
| `users` | id, email, auth hash, KDF name and parameters, wrapped data key, wrapped data key by recovery key, created and verified timestamps |
| `devices` | id, user id, name, Sanctum token id, last seen |
| `items` | id (client UUID), user id, revision (bigint, per-user monotonic), ciphertext (blob), deleted (bool), updated at, updated by device |
| `user_revisions` | user id, current revision (row lock gives a per-user sequence) |

One item is one snippet, encrypted as a whole: the JSON of the snippet (abbreviations, expansion or sealed expansion, description, folder, trigger, case, dates) sealed with `dataKey`. Folders are not separate items; they follow from snippet paths, as they do locally. `vault.json` and an optional "synced settings" item (delimiters, defaults; never DPAPI secrets or backup destinations) are items with fixed ids.

### Sync protocol

- `GET /api/sync?since=<revision>` returns every item with a higher revision (including tombstones) and the new cursor. Paginated.
- `POST /api/sync` sends local changes as `{id, baseRevision, ciphertext | deleted}`. For each item the server accepts it if `baseRevision` equals the stored revision (or the item is new), assigns the next revision and returns it. Otherwise it returns a conflict with the current server version.
- The client always pulls before it pushes, then pushes, then pulls again if anything was rejected.
- Triggers: on start, a few seconds after a local save (debounced), every few minutes while running, and on "Sync now".

Conflict rule (recommended): per-snippet last writer wins by the snippet's own `Modified` time, but if both sides changed the expansion text, keep the losing version as a new snippet in a `Conflicts` folder, so no text is silently lost. Deleted-versus-edited resolves to "keep the edit". Conflicts are rare for a single user with a couple of PCs, so this beats field-level merging in cost.

### API surface

| Method and path | Purpose |
|---|---|
| `POST /api/prelogin` | Returns KDF name and parameters for an email (also for unknown emails, with fake parameters, so it does not reveal which emails exist) |
| `POST /api/register` | Email, auth hash, KDF parameters, wrapped data key, recovery-wrapped data key |
| `POST /api/login` | Email, auth hash, device name; returns a Sanctum token and the wrapped data key |
| `POST /api/logout` | Revokes the device token |
| `GET /api/sync`, `POST /api/sync` | Pull and push items |
| `GET /api/devices`, `DELETE /api/devices/{id}` | List and sign out devices |
| `POST /api/account/password` | New auth hash and re-wrapped data key, after re-authentication |
| `GET /api/account/export`, `DELETE /api/account` | Data export (ciphertext plus key material) and account deletion |

Rate-limit `prelogin`, `login` and `register`, require email verification before sync, and offer TOTP two-factor login later.

### Client changes

- Core: `Snippet.Id`; a `SyncState` store; a `SyncEngine` that is pure logic (merge pull results, pick conflicts, build push batches) and unit-testable without a server; an `ISyncTransport` with an HTTP implementation.
- App: an "Account" tab in Settings (sign up, sign in, sign out, recovery key, devices), a sync status in the status bar and tray, "Sync now" in the File menu.
- Backups stay as they are. Sync is not a backup: a deletion syncs everywhere within seconds, while a zip from yesterday still has the snippet.

### Privacy and legal

Even with encrypted content, the service stores personal data (email addresses, IP addresses in logs, device names), so it needs a privacy policy, a processing record, EU hosting, account deletion and export, and log retention limits. The operator cannot read snippets, which keeps the data-subject access answer simple: "we hold ciphertext we cannot decrypt". Have the GDPR side reviewed before opening sign-up to other people.

### Part 2 decisions

1. **Encrypt all snippets end to end, or only protected ones?** Recommendation: all. See "End-to-end encryption".
2. **One password for account and vault, or two?** Recommendation: two, but allow the user to choose the same value. The vault password guards against someone using an already signed-in PC; folding it into the account password would remove that protection.
3. **Argon2id or PBKDF2 for the account key?** Recommendation: Argon2id (64 MiB, 3 iterations), via a maintained library such as Konscious.Security.Cryptography or libsodium bindings, because it resists GPU cracking far better. Fall back to PBKDF2-SHA256 600,000 rounds only if adding a native or third-party crypto dependency is unacceptable.
4. **Offer a recovery key?** Recommendation: yes, shown once at sign-up with a confirmation step. Without it, a forgotten password loses everything that is not in a local zip.
5. **Conflict handling?** Recommendation: last writer wins per snippet with conflict copies, as described above.
6. **Hosting?** Recommendation: a small Forge-managed server in an EU region, separate from client projects, with daily database backups.
7. **Who can sign up?** Recommendation: invite-only (owner and friends) for the first release, open sign-up only after the GDPR review and some real-world sync use.
8. **Free or paid?** Open; it decides whether billing (for example Laravel Cashier with Stripe or Paddle) belongs in the first server release. Recommendation: leave billing out until there are users who ask for the service.

## Suggested order of work

1. **Drive OAuth target.** Self-contained, a few days of work, immediately useful.
2. **Snippet ids and tombstones.** Small, local only, and needed by any sync design; worth doing even if the server is postponed.
3. **SyncEngine with an in-memory fake transport.** Proves the merge and conflict rules with unit tests before any server exists.
4. **Laravel API.** Auth, key endpoints, item store, rate limits, tests.
5. **Account UI and HTTP transport,** then a closed beta on the owner's own PCs.
6. **GDPR documents and review,** then decide on wider sign-up.
