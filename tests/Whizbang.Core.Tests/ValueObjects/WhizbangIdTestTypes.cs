using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Test ID types for WhizbangIdProviderRegistry tests
// These are in a separate file to ensure source generator runs
// and produces Provider types before test code references them

[WhizbangId]
public readonly partial struct RegistryTestId1;

[WhizbangId]
public readonly partial struct RegistryTestId2;
