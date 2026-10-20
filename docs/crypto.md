# Freevy account cryptography

The contract between the freevy.app server, its web client and this desktop app. Every client must produce byte-identical results. In this repository the app side is `src/Freevy.Core/Sync/AccountCrypto.cs` and `BrowserSignIn.cs`; `tests/Freevy.Core.Tests` checks it against vectors produced by the website's JavaScript (`Fixtures/crypto-vectors.json`, `Fixtures/device-link.json`). The server only stores and returns these values and checks their shape.

## Encodings

- Base64 is standard base64 with padding (RFC 4648 section 4), not URL-safe.
- Strings are UTF-8 before hashing.

## Key derivation

| Value | Derivation |
|---|---|
| `kdf.salt` | 16 random bytes, chosen by the client at sign-up and on every password change |
| `masterKey` | Argon2id(password, salt, memory = `kdf.memory` KiB, iterations = `kdf.iterations`, parallelism = `kdf.parallelism`, length 32), version 0x13, no secret, no associated data |
| `authHash` | PBKDF2-HMAC-SHA256(password = `masterKey`, salt = UTF-8 password, 1 iteration, 32 bytes), sent as base64 |
| `wrapKey` | HKDF-SHA256(`masterKey`, salt = empty, info = `freevy-wrap-v1`, 32 bytes) |
| `recoveryWrapKey` | HKDF-SHA256(`recoveryKey`, salt = empty, info = `freevy-recovery-v1`, 32 bytes) |

The server stores `bcrypt(authHash)` in `users.password` and never sees the password, `masterKey` or any key below.

Defaults and accepted ranges for the Argon2id parameters are in `config/freevy.php` (default 64 MiB, 3 iterations, parallelism 1). Clients must use the parameters `POST /api/prelogin` returns for an existing account.

## Keys

- `dataKey`: 32 random bytes per account. Encrypts all synced items. Never changes on password change or recovery-key reset; replaced only when the user erases synced data.
- `recoveryKey`: 16 random bytes, shown once as 26 base32 characters (RFC 4648 alphabet `A-Z2-7`, no padding) in groups of four separated by `-`. Parsing ignores case and any character outside the alphabet.

## Sealed values

`seal(key, plaintext)` = `"v1." + base64(nonce[12] || ciphertext || tag[16])` with AES-256-GCM, a fresh random nonce, no associated data. This is WebCrypto's natural output order (ciphertext followed by tag). Note the desktop app's local vault uses a different layout (`nonce || tag || ciphertext`, no prefix); the two formats must not be mixed.

- `wrapped_data_key` = seal(`wrapKey`, `dataKey`)
- `recovery_wrapped_data_key` = seal(`recoveryWrapKey`, `dataKey`)

## Flows

- **Sign-up:** client generates salt, `dataKey`, `recoveryKey`; sends email, `authHash`, KDF parameters and both wraps; shows the recovery key once.
- **Login (web and app):** `POST /prelogin` (web) or `/api/prelogin` returns KDF parameters (fake but stable ones for unknown emails); client derives `authHash` and sends it. The app's `POST /api/login` also returns `wrapped_data_key`, which it unwraps locally.
- **Password change:** client unwraps `dataKey` with the old `masterKey`, derives a new salt and `masterKey`, re-wraps `dataKey`; sends the old `authHash` for proof plus the new values. The recovery wrap stays valid.
- **Reset with recovery key:** after the emailed token is checked, the server returns `recovery_wrapped_data_key`; client unwraps it with the recovery key and proceeds as for a password change. All device tokens are revoked.
- **Reset by erasing:** client creates a new `dataKey` and `recoveryKey`; the server replaces everything and deletes all synced items.

## Browser sign-in (desktop app)

The app can sign in through the website instead of asking for the password itself. The password stays in the browser, and only the app that started the sign-in can open the key.

1. The app creates a one-time ECDH P-256 key pair, a `verifier` (32 random bytes, base64url) and a `state` (18 random bytes, base64url). `challenge = base64url(SHA-256(ascii(verifier)))`. It opens `/link?device=<name>&key=<base64url SPKI>&challenge=<challenge>&state=<state>` and shows the fingerprint.
2. The page (signed in, verified email) shows the device name and the fingerprint: the first 4 bytes of `SHA-256(SPKI)` as a big-endian unsigned integer, modulo 10^8, as two groups of four digits. The user compares it with the app.
3. The browser derives `masterKey`, unwraps `dataKey`, creates its own one-time P-256 key pair and computes `linkKey = HKDF-SHA256(ECDH(browserPrivate, appPublic), empty salt, "freevy-link-v1")`. It sends `sealed_key = "v1." + base64(nonce | AES-256-GCM(linkKey, dataKey, aad = ascii(challenge)) | tag)` with `browser_key` (base64url SPKI), `device_key`, `device_name` and `challenge` to `POST /link`.
4. The server stores this for 5 minutes behind a random one-time code (only its SHA-256 is kept) and returns the code. The browser opens `freevy://link?code=<code>&state=<state>`.
5. Windows starts the app with that link; a second instance passes it to the running one over a per-user named pipe. The app checks `state` against its pending sign-in (10 minutes at most) and calls `POST /api/link/redeem` with `code`, `verifier` and `device_key`. The server checks the code is unused and unexpired, that `base64url(SHA-256(verifier))` equals the stored challenge and that the device key matches, marks the code used, and returns a device token, the email, `browser_key` and `sealed_key`.
6. The app derives the same `linkKey` from its private key and `browser_key` and opens `dataKey`.

A stolen link is useless: redeeming needs the verifier, and opening the key needs the app's private key, both of which stay in the app's memory. Test vector: `tests/Freevy.Core.Tests/Fixtures/device-link.json`.

## Synced items

- `itemKey` = HKDF-SHA256(`dataKey`, salt = empty, info = `freevy-items-v1`, 32 bytes).
- Plaintext is the UTF-8 JSON of the item, right-padded with spaces (0x20) to the next multiple of 256 bytes that leaves at least one space. JSON parsers ignore the trailing whitespace.
- `ciphertext` = `"v1." + base64(nonce[12] || ciphertext || tag[16])`, AES-256-GCM with associated data = the item id as lowercase UTF-8. The server therefore cannot move a ciphertext to another item id without decryption failing.
- Item ids are UUIDs chosen by the client. A snippet keeps its id for life.

Item JSON (version 1):

```json
{ "type": "snippet", "v": 1, "abbreviations": ["teh", "hte"], "expansion": "the", "description": "", "folder": "Typos",
  "case": "Adapt", "trigger": "WordStart, AfterDelimiter", "created": "2026-10-20T10:00:00Z", "modified": "2026-10-20T10:00:00Z" }
```

A protected snippet replaces `"expansion"` with `"sealed_expansion"`: the value the desktop vault produced, so it still needs the master password after sync. The vault header itself syncs as `{ "type": "vault", "v": 1, "salt": "...", "iterations": 600000, "verifier": "..." }` under the fixed id `00000000-0000-4000-8000-000000000001`.

## Sync protocol

All endpoints need a Sanctum token from `POST /api/login` and a confirmed email address; send `Accept: application/json`.

- `GET /api/sync?since=<revision>&limit=<n>` returns `{ items: [{ id, revision, deleted, ciphertext, updated_at }], cursor, more, revision }`. Items come in revision order; deletions are tombstones (`deleted: true`, `ciphertext: null`). Keep calling with `since = cursor` while `more` is true.
- `POST /api/sync` with `{ changes: [{ id, base_revision, ciphertext | deleted: true }] }`, at most 500 per call. `base_revision` is the revision the client last saw for that item, or `null` for a new item. Each result is `{ id, status: "ok", revision }` or `{ id, status: "conflict", current }`, where `current` is the server's item (or `null`). Accepted changes get consecutive revisions.
- `GET /api/sync/status` returns the account's revision, item count, bytes used and limits.
- A batch that would exceed the account quota is rejected as a whole with HTTP 413.

Client loop: pull until `more` is false, apply remote changes, then push local changes with their base revisions; for each conflict, merge (newer `modified` wins; if both sides changed the expansion, keep the other one as a copy), and push again with `base_revision = current.revision`.
