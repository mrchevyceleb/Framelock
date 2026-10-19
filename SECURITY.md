# Security policy

## Supported versions

Only the [latest release](https://github.com/mrchevyceleb/Framelock/releases/latest) gets security fixes. Installed copies update themselves automatically, so a fix reaches everyone through a normal release.

## Reporting a vulnerability

Please don't open a public issue for a security problem. Report it privately instead:

1. Open the [Security tab](https://github.com/mrchevyceleb/Framelock/security) of this repository.
2. Choose **Report a vulnerability** and describe the problem: what an attacker can do, the steps to reproduce it, and which Framelock release you're running.

You should get a reply within a few days. Once a fix ships, the advisory is published with credit to you unless you'd rather stay anonymous.

## Scope

In scope: the Framelock app, its installer and update flow (Velopack packages published on this repository's releases), and the release workflow in `.github/workflows`.

Bundled components such as FFmpeg, .NET and Velopack have their own security processes. Report problems in those components to their projects; if Framelock ships an affected version, a report here is welcome too.
