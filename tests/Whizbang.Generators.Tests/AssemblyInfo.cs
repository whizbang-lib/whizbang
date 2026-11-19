// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Core;
using Whizbang.Generators.Tests;

// Every test in the project runs under one limit sized to the memory its compilations hold (#2159).
[assembly: ParallelLimiter<GeneratorsTestParallelism>]
