// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Sagas;

/// <summary>The run is committed: apply what its steps staged.</summary>
/// <docs>fundamentals/sagas/commit-rollback</docs>
/// <tests>tests/Whizbang.Sagas.Tests/SagaOutcomeTests.cs</tests>
public interface ISagaCommittedEvent : ISagaOutcomeEvent;
