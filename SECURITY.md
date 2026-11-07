# Security policy

Aethera manages servers and holds secrets, so security reports are taken seriously.

## Reporting a vulnerability

**Please do not open a public issue.** Report it privately through GitHub: on the repository, open **Security > Report a vulnerability** (<https://github.com/MihaiM21/Aethera-Forge/security/advisories/new>).

Include what you found, the version (Settings > System, or the release tag), how to reproduce it and what an attacker gains. A proof of concept helps. Please do not test against installations you do not own.

You can expect an acknowledgement within a few days. We will keep you updated, fix it, release a patched version and credit you in the advisory and the [changelog](CHANGELOG.md), unless you prefer to stay anonymous.

## Supported versions

Aethera is in beta (0.x): only the latest release receives security fixes. Update with *Update to X* in Settings or `aethera update`.

## What we consider in scope

The control plane (API, authentication, sessions, authorization between organizations), the agent and its protocol (mTLS, the command allowlist), the installer and updater, secret storage and redaction, and the web UI. How the platform is meant to resist abuse is described in the [trust model](docs/architecture/0006-trust-model.md); a way around it is a vulnerability.

Out of scope: vulnerabilities in the software you deploy with Aethera, findings that need an already compromised server or owner account, and missing hardening on a deployment that follows none of the [install guide](docs/install.md) (for example, running the UI over plain HTTP on the internet).
