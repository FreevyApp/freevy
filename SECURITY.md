# Security policy

Freevy handles text people type, passwords in protected snippets and an encrypted sync account, so security reports are welcome and taken seriously.

## Reporting a vulnerability

Please do not open a public issue. Instead:

- use GitHub's **Report a vulnerability** button on this repository's Security tab, or
- email **hello@freevy.app** with "Security" in the subject.

Include what you found, how to reproduce it and the Freevy version (Settings shows it). You will get an answer within a few days. Please give us a reasonable time to fix the problem before you publish details; we credit reporters who want to be named.

## Scope

- The Windows app in this repository, including its encryption, sync client, update and sign-in flows.
- The protocol in [docs/crypto.md](docs/crypto.md), which the freevy.app server and web manager follow. Server-side problems can be reported the same way.

## Supported versions

Only the latest release is supported. Installed apps update themselves, so fixes reach users without action on their part.
