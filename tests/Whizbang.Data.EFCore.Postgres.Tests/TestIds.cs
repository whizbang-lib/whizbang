// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Test ID types for EFCore.Postgres.Tests.
/// These strongly-typed IDs are used instead of raw Guids to demonstrate
/// the WhizbangId provider system and ensure type safety in tests.
/// </summary>

[WhizbangId]
public readonly partial struct TestOrderId;

[WhizbangId]
public readonly partial struct TestPerspectiveId;

/// <summary>An id that takes any non-empty Guid version, for the persistence-profile round trip.</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct TestAnyVersionId;
