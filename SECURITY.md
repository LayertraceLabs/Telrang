# Security policy

## Reporting a vulnerability

Report it privately through GitHub: **Security → Report a vulnerability** on this repository. Do not open a public
issue.

Include the version (page footer, or the Status tab), the install format (Docker, Linux, Windows), and the steps to
reproduce. A first answer follows within a week.

## Supported versions

Only the latest release gets security fixes.

## Scope

In scope: the Telrang application, the Docker image, the Linux and Windows packaging, and the example configuration in
this repository. Out of scope: the reverse proxy, mail server or database you run alongside it, and findings that need
admin access to the instance.

What each part of an install reveals and how to harden it: [docs/deploy/security.md](docs/deploy/security.md).
