# Security Policy

## Supported Versions

Security fixes are provided for the latest release on the `main` branch.

## Credential & Data Handling

- API keys and preferences are stored locally in `%LOCALAPPDATA%\ReadingPanel\settings.dat`, encrypted for the current Windows user with Windows DPAPI (`ProtectedData`).
- Password fields are explicitly excluded from desktop and browser selection capture.
- Offline dictionary lookups and Piper voice synthesis operate locally without network requests.

## Reporting a Vulnerability

If you discover a security vulnerability (for example, credential exposure, native messaging or named pipe privilege issues, or unsafe process/file handling), please **do not** open a public issue.

Instead, report it privately via [GitHub Security Advisories](https://github.com/Cusnd/reading-panel/security/advisories/new) or contact the maintainer at `i@cusnd.com`.

Please include:
- A clear description of the vulnerability and potential impact
- Steps to reproduce or a minimal proof of concept
- Affected edition (Online / Offline) and Windows version
