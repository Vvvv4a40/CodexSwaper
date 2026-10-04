# Security review and policy — 1.1.0

This is an unofficial local account switcher derived from ZOONGG/codex-swap-account, commit 92b9dc6d378c1c920b0103ecb954dcfc313ff09a. A successful build is not proof of security. This review is a source and regression audit, not an independent penetration test or guarantee.

## Fixed in this release

- Credential, profile, migration and backup paths reject symbolic links, junctions and other reparse points, including existing ancestors. Backup cleanup does not traverse redirected targets. Windows reserved profile names and trailing dots are rejected.
- Backup finalization is part of the authorization transaction. A failure restores the previous authorization and active profile. Retention failures cannot invalidate a completed switch. Rollback verifies the backup checksum, length and manifest metadata before writing.
- Rollback after a desktop launch failure performs a fresh shutdown and final check of the new desktop before restoring authorization; it refuses mutation if selected processes remain alive.
- Desktop selection requires the installed OpenAI.Codex package path and current Windows session. Exact creation times and retained native handles guard PID identity. The SYSTEM sandbox service and unrelated CLI processes are not shutdown roots. Termination targets individual selected processes.
- PowerShell and Explorer use absolute Windows system paths. Relative PATH entries are skipped when locating the CLI. CLI stderr is drained without retaining it, and JSON response lines and notification counts are bounded.
- Logs redact token fields, API keys, bearer values, emails and identifiers. Message lengths, log file size and rotated generations are bounded. Log write failures do not turn a completed transaction into a failure.
- Publish output is restricted to a child of repository artifacts, rejects reparse points, stages before replacement and restores prior owned output on failure. Install/uninstall target the exact installed executable in the current session and preserve authorization, backups and unknown application-data files.
- Dependencies use lockfiles and nuget.org only. CI uses pinned action commits, read-only repository permissions, no persisted checkout credential, vulnerability auditing, tests and a source/history heuristic secret scan.

## Threat model and remaining risks

The tool assumes a trusted Windows user account, trusted installed Codex desktop/CLI, and trusted absolute PATH entries. It cannot protect credentials from malicious code already running as the same user or as an administrator. Path checks are not handle-based sandboxing; a hostile concurrent same-user path replacement or hardlink attack is not fully eliminated.

Authorization and rollback backups remain plaintext on disk because the Codex CLI uses auth.json. The program inherits filesystem access controls; this review has not inspected actual user ACLs and does not claim encryption or restrictive ACL enforcement. Protect the Windows account and disk. Do not upload auth.json, profile folders, personal settings, logs or backups.

Process snapshots and manual desktop relaunches can race the final authorization check. Real-account switching, protected worker shutdown, account freshness and concurrent desktop relaunches were not tested. Tests use synthetic authorization contents, isolated temporary junctions and only processes created by the test fixtures. The agent did not read or modify existing .codex, .codex-profiles or authorization files, launch normal switcher mode, install, configure startup or log in.

The portable executable is unsigned. SHA-256 establishes file integrity against the published checksum, not trustworthy behavior. Heuristic scans can miss secrets. Vulnerability reports cover currently known advisories from NuGet, not every bug or the separately bundled runtime.

The usage/login CLI may contact OpenAI. The switcher has no telemetry upload; that does not mean every invoked CLI operation is offline.

.NET 8.0.31 is bundled in this release. Microsoft ends .NET 8 support on 2026-11-10; self-contained applications must be rebuilt for runtime patches. See [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). A future maintained release should move to a supported LTS runtime.

## Validation

Run build.ps1, audit-dependencies.ps1, verify-repository-safety.ps1 -IncludeHistory and the isolated benchmark described in docs/PERFORMANCE.md. Release checksums and the exact commit are recorded with the GitHub release. No claim of absolute safety or ideal performance is made.

For security findings, use GitHub private vulnerability reporting if enabled; otherwise report the issue without publishing credentials or account details.
