# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub's private vulnerability reporting:
[Security → Report a vulnerability](https://github.com/streamzh/streamzh/security/advisories/new). Do not open a public
issue, discussion or pull request for them.

Include:

- the StreamZH version (System → Status) and how it is installed (Windows installer or service, Linux package, Docker,
  macOS),
- what an attacker can do and what they need for it (network access, an account and its role, …),
- the steps to reproduce, and a proof of concept if you have one.

Leave out real API keys, passwords and personal data. We aim to answer within a week. Fixes are released as soon as
they are ready and credited in the release notes unless you prefer otherwise. Please give us a reasonable time to fix
the problem before you publish details.

## Supported versions

| Version | Security fixes |
|---|---|
| Latest release (1.0 release candidates until 1.0) | Yes |
| Older versions | No: please update |

## How StreamZH is secured

[docs/SECURITY.md](docs/SECURITY.md) describes what StreamZH protects, its threat model, the roles and owner-only
settings, browser protection, stored secrets, storage locations and how to run it safely.
