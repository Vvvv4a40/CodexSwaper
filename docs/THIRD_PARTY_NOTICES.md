# Third-party notices

CodexSwaper is a fork of ZOONGG/codex-swap-account (MIT). The original copyright and license are retained in LICENSE.txt.

The Windows x64 portable distribution includes .NET 8.0.31 runtime and Windows Desktop libraries (MIT, Microsoft and .NET contributors), Microsoft.Data.Sqlite 8.0.22 (MIT), SQLitePCLRaw 2.1.13 (Apache-2.0), and SQLite native code (public domain). System.Memory 4.5.3 is a resolved transitive build dependency (MIT). License texts and runtime third-party notices are included in the licenses folder. Development-only xUnit and VSTest packages are not shipped in the portable executable; their exact versions and content hashes remain in packages.lock.json.

Primary sources:

- https://github.com/dotnet/runtime
- https://github.com/dotnet/efcore
- https://github.com/ericsink/SQLitePCL.raw
- https://www.sqlite.org/copyright.html

The self-contained executable must be rebuilt when runtime security fixes become available. .NET 8 support ends on 2026-11-10; this release does not promise indefinite support. See https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core .
