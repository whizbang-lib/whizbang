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
