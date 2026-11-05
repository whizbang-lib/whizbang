// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Tests;

// Every test in the project runs under one limit sized to the shared container's connections (#2069). Tests are
// parallel by default; see EFCoreTestBase and ai-docs/efcore-test-parallelism.md for when one serializes and why.
[assembly: ParallelLimiter<EFCorePostgresParallelism>]
