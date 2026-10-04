# Isolated performance checks

Run the synthetic harness from the repository root:

```powershell
dotnet build benchmarks/CodexProfileOverlay.Benchmarks -c Release -p:Platform=x64
dotnet benchmarks/CodexProfileOverlay.Benchmarks/bin/x64/Release/net8.0/CodexProfileOverlay.Benchmarks.dll
```

It compares repeated snapshot searches with one dictionary per capture using the same
identity and name lookup expressions as desktop shutdown. It links the production
desktop selector and executable identity cache, checks equal lookup outputs, and
checks that a reused PID cannot inherit an executable validation decision.

It does not enumerate or terminate native processes, open windows, launch Codex,
load application settings, or access real profiles and authorization. Results are
microbenchmarks of synthetic data, not end-to-end switching or application startup
measurements. Run Release builds on the same machine to compare wall times; the
printed executable query counts are also useful when machine load changes.
