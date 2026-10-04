# Privacy

Codex Swap Account stores profiles and application data locally. It has no telemetry of its own, no cloud profile sync, no reverse proxy, and no background Windows service. Login and usage checks invoke the Codex CLI, which may connect to OpenAI and use the selected account's credentials. Local storage does not mean that every invoked CLI operation is offline.

The switcher reads, hashes and copies authorization files for profile switching, backups and rollback. It checks paths, file sizes and backup integrity; it does not parse token fields for display or log the contents of `auth.json`. The switcher itself does not upload these files.

Profile credentials, rollback backups and removed-profile archives may contain plaintext authorization data. Protect them like passwords and do not share application-data or profile folders. These files are not encrypted by the program. It inherits filesystem access controls; restrictive ACLs are not enforced and actual user ACLs were not inspected during the source audit.

Logs are written under `%LOCALAPPDATA%\CodexProfileOverlay\logs` and are sanitized for token-like values and email addresses.

See [SECURITY.md](../SECURITY.md) for the threat model, validation scope and remaining risks. A successful build or test run is not proof of security.
