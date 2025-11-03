# Whizbang Benchmarks

Performance benchmarks for Whizbang core components using BenchmarkDotNet.

## Running Benchmarks

**Run all benchmarks with combined markdown report (RECOMMENDED):**

```bash
cd benchmarks/Whizbang.Benchmarks
echo "*" | dotnet run -c Release --join
```

This creates a single `Combined-report.md` file with all 43 benchmarks.

**Run all benchmarks (separate reports):**

```bash
dotnet run -c Release
```

This creates individual report files per benchmark class.

**IMPORTANT**: Always run benchmarks in Release mode (`-c Release`) for accurate results.

## Running Specific Benchmarks

**Run specific benchmark class:**

```bash
dotnet run -c Release --filter *ExecutorBenchmarks*
dotnet run -c Release --filter *SequenceProviderBenchmarks*
dotnet run -c Release --filter *PartitionRouterBenchmarks*
dotnet run -c Release --filter *PolicyEngineBenchmarks*
dotnet run -c Release --filter *TracingBenchmarks*
dotnet run -c Release --filter *SimpleBenchmarks*
```

**Run specific methods:**

```bash
# Executor Benchmarks
dotnet run -c Release --filter *SerialExecutor_SingleMessage*
dotnet run -c Release --filter *ParallelExecutor_100Messages*

# Sequence Provider Benchmarks
dotnet run -c Release --filter *SingleSequence*
dotnet run -c Release --filter *Generate100Sequences_Parallel*

# Partition Router Benchmarks
dotnet run -c Release --filter *SinglePartitionAssignment*
dotnet run -c Release --filter *ParallelAssignment_10000Keys*

# Policy Engine Benchmarks
dotnet run -c Release --filter *MatchPolicy_1Policy*
dotnet run -c Release --filter *MatchPolicy_20Policies*

# Tracing Benchmarks
dotnet run -c Release --filter *CreateMessageId*
dotnet run -c Release --filter *TraceStore_Store1000Envelopes*
```

## Advanced Options

### Quick Validation (Dry Run)

Test that benchmarks compile and run without full benchmarking:

```bash
dotnet run -c Release --job dry
dotnet run -c Release --filter *ExecutorBenchmarks* --job dry
```

The `--job dry` flag runs each benchmark once with minimal overhead - useful for:

- Verifying benchmarks work after code changes
- Quick smoke testing before full benchmark runs
- CI/CD validation

### Combined Results Report

**Generate a single combined markdown report for all benchmarks:**

```bash
# The --join flag combines all benchmarks into ONE markdown file
echo "*" | dotnet run -c Release --join

# Results saved to BenchmarkDotNet.Artifacts/results/ as:
# - Combined-report.md (SINGLE file with all 43 benchmarks)
# - Combined-report.html
# - Combined-report.csv
```

**Quick validation with combined report:**

```bash
# Fast dry run with all results in one file
echo "*" | dotnet run -c Release --join --job dry
```

**Without --join (default behavior):**

```bash
# This creates SEPARATE report files per benchmark class
dotnet run -c Release

# Results: ExecutorBenchmarks-report.md, TracingBenchmarks-report.md, etc.
```

**Key difference:**

- ✅ `--join` → Single combined markdown file with all benchmarks
- ❌ Without `--join` → Multiple markdown files (one per class)

### Other Useful Options

**Memory diagnostics only:**

```bash
dotnet run -c Release --memory
```

**Custom iteration count (faster, less accurate):**

```bash
dotnet run -c Release --iterationCount 3
```

**List available benchmarks without running:**

```bash
dotnet run -c Release --list flat
```

**Run and compare results:**

```bash
# Run baseline
dotnet run -c Release --filter *ExecutorBenchmarks*

# Make code changes, then run again
dotnet run -c Release --filter *ExecutorBenchmarks*

# BenchmarkDotNet will show comparisons in subsequent runs
```

## Available Benchmarks

### ExecutorBenchmarks (4 benchmarks)

- `SerialExecutor_SingleMessage()` - Serial execution baseline
- `ParallelExecutor_SingleMessage()` - Parallel execution single message
- `SerialExecutor_100Messages()` - Serial batch processing
- `ParallelExecutor_100Messages()` - Parallel batch processing with concurrency limit

### SequenceProviderBenchmarks (5 benchmarks)

- `SingleSequence()` - Single sequence generation baseline
- `Generate100Sequences_Serial()` - Sequential batch generation
- `Generate100Sequences_Parallel()` - Concurrent batch generation (100)
- `Generate1000Sequences_Parallel()` - Large concurrent batch (1000)
- `MultipleStreams_10Streams_100Each()` - Multi-stream generation (1000 total)

### PartitionRouterBenchmarks (5 benchmarks)

- `SinglePartitionAssignment()` - Single partition assignment baseline
- `Assign1000Keys()` - Batch assignment (1000 keys)
- `AssignVariedKeys_1000()` - Realistic key patterns (orders, users, tenants)
- `ParallelAssignment_10000Keys()` - Concurrent assignment (10K keys)
- `DifferentPartitionCounts()` - Variable partition counts (4, 8, 16, 32, 64)

### PolicyEngineBenchmarks (7 benchmarks)

- `MatchPolicy_1Policy_FirstMatch()` - Single policy baseline
- `MatchPolicy_5Policies_LastMatch()` - 5 policies, matches last (worst case)
- `MatchPolicy_5Policies_MiddleMatch()` - 5 policies, matches middle
- `MatchPolicy_20Policies_LastMatch()` - 20 policies stress test
- `MatchPolicy_100Times_1Policy()` - Repeated matching with 1 policy
- `MatchPolicy_100Times_5Policies()` - Repeated matching with 5 policies
- `MatchPolicy_100Times_20Policies()` - Repeated matching with 20 policies

### TracingBenchmarks (12 benchmarks)

- `CreateMessageId()` - UUIDv7 MessageId generation baseline
- `CreateCorrelationId()` - UUIDv7 CorrelationId generation
- `CreateCausationId()` - UUIDv7 CausationId generation
- `CreateMessageHop()` - Message hop with routing metadata
- `CreateMessageEnvelope()` - Full envelope construction
- `CreateEnvelopeWithMetadata()` - Envelope with 10 metadata entries
- `CreateEnvelopeWith3Hops()` - Envelope with current + 2 causation hops
- `GetCurrentTopicFromEnvelope()` - Extract current hop topic
- `GetCurrentSecurityContextFromEnvelope()` - Extract security context
- `GetCausationHopsFromEnvelope()` - Filter causation hops
- `TraceStore_Store1000Envelopes()` - Batch store (1000 envelopes)
- `PolicyDecisionTrail_Record100Decisions()` - Decision trail overhead

### SimpleBenchmarks (10 benchmarks)

- Core operations (ID generation, hops, envelopes, sequences, trace store)
- Simplified versions focusing on most critical paths

## Results

### Console Output Summary

When you run benchmarks, BenchmarkDotNet displays a **comprehensive summary table** at the end showing:

- All benchmarks that were run
- Performance metrics (Mean, Error, StdDev, Ratio)
- Memory allocation data
- Comparison to baselines

This console summary is your **combined overview** of all benchmarks run in that session.

### Saved Reports

Results are saved to `BenchmarkDotNet.Artifacts/results/` with:

**With `--join` flag (RECOMMENDED for combined results):**

- ✅ **Combined-report.md** - Single file with all 43 benchmarks
- ✅ **Combined-report.html** - Interactive web report
- ✅ **Combined-report.csv** - Raw data

**Without `--join` flag (default):**

- **ExecutorBenchmarks-report.md** (one per class)
- **TracingBenchmarks-report.md** (one per class)
- **...** (6 separate files total)

**Recommendation**: Use `echo "*" | dotnet run -c Release --join` to get a single markdown file with all results.

### Capturing Combined Results

To save the combined console summary:

```bash
# Redirect console output to a file
dotnet run -c Release | tee benchmark-results.txt

# Or on Windows
dotnet run -c Release > benchmark-results.txt
```

## Performance Notes

- **Always use Release mode** (`-c Release`) - Debug builds have significant overhead
- **Close other applications** - Background processes affect results
- **Relative performance** - Focus on comparing operations, not absolute numbers
- **Hardware variance** - Results vary by CPU, but relative differences are consistent
- **Baseline comparisons** - Each benchmark class has a baseline marked with `[Benchmark(Baseline = true)]`

### High Priority Warning (macOS/Linux)

You may see this warning:

```
Failed to set up high priority (Permission denied). In order to run benchmarks with high priority, make sure you have the right permissions.
```

**This is safe to ignore.** BenchmarkDotNet tries to set the process to high priority for more stable results, but this requires elevated permissions on Unix-like systems. The benchmarks still run correctly and produce valid results.

**To eliminate the warning** (optional):

- **macOS/Linux**: Run with `sudo` (not recommended for security reasons)
- **Alternative**: Accept the warning - it doesn't affect benchmark validity for most use cases

## Understanding Results

BenchmarkDotNet output includes:

- **Mean**: Average execution time
- **Error**: Margin of error
- **StdDev**: Standard deviation
- **Ratio**: Performance relative to baseline
- **Gen0/Gen1/Gen2**: Garbage collection counts per 1000 operations
- **Allocated**: Memory allocated per operation

Example interpretation:

```
|                          Method |      Mean |   Ratio | Allocated |
|-------------------------------- |----------:|--------:|----------:|
|    SerialExecutor_SingleMessage |  1.234 μs |    1.00 |     456 B |
| ParallelExecutor_SingleMessage |  1.567 μs |    1.27 |     512 B |
```

This shows ParallelExecutor has 27% more overhead for single messages but allocates only 12% more memory.

## Benchmark Categories by Performance Focus

**Throughput Benchmarks:**

- ExecutorBenchmarks (Serial vs Parallel)
- SequenceProviderBenchmarks (concurrent generation)
- PolicyEngineBenchmarks (matching speed with policy count scaling)

**Overhead Benchmarks:**

- TracingBenchmarks (observability cost)
- SimpleBenchmarks (ID generation, hop creation)

**Distribution Quality Benchmarks:**

- PartitionRouterBenchmarks (assignment consistency, varied keys)

## Total Benchmarks

**43 benchmarks across 6 classes:**

- ExecutorBenchmarks: 4
- SequenceProviderBenchmarks: 5
- PartitionRouterBenchmarks: 5
- PolicyEngineBenchmarks: 7
- TracingBenchmarks: 12
- SimpleBenchmarks: 10
