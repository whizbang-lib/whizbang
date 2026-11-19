// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core;

namespace Whizbang.Core.Tests.ValueObjects;

// Ids for the version-acceptance tests. Kept in their own file so the generator has produced them
// before the tests reference them.

/// <summary>An id that takes any non-empty Guid version (name-derived, externally issued, and so on).</summary>
[WhizbangId(AcceptAnyVersion = true)]
public readonly partial struct AnyVersionTestId;

/// <summary>An id with the default rule: UUIDv7 only.</summary>
[WhizbangId]
public readonly partial struct StrictVersionTestId;
