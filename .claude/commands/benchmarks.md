Run performance benchmarks using BenchmarkDotNet.

Execute:
```bash
pwsh scripts/benchmarks/run-benchmarks.ps1
```

Or filter to specific benchmarks:
```bash
pwsh scripts/benchmarks/run-benchmarks.ps1 -Filter "*TracingBenchmarks*"
```

This will:
- Run all benchmarks in the Whizbang.Benchmarks project
- Generate detailed performance reports
- Create HTML and markdown reports
- Display summary in console

Results location:
- `benchmarks/Whizbang.Benchmarks/BenchmarkDotNet.Artifacts/results/`

Use when:
- Measuring performance changes
- Optimizing critical paths
- Verifying zero-allocation claims
- Before/after performance comparisons
