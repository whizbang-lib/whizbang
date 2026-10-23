// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.BuildCost.Benchmarks;

// Usage: dotnet run -c Release --project benchmarks/Whizbang.BuildCost.Benchmarks -- [--runs N] [--features N]
//        [--ordinary-files N] [--thresholds PATH] [--output PATH]
// Writes the markdown report to $GITHUB_STEP_SUMMARY when set, else to stdout, and to --output when given.
// Exits 1 when any measurement is over its threshold.
return await BuildCostProgram.RunAsync(args);
