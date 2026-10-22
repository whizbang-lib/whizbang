// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>
/// The run is rolled back: discard what its steps staged and compensate for what they applied.
/// </summary>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public interface ISagaRolledBackEvent : ISagaOutcomeEvent;
