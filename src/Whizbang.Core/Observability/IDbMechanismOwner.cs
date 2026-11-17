// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Observability;

/// <summary>
/// A hosted worker that names the mechanism its database statements are attributed to. The host opens a
/// <see cref="DbMechanismScope"/> for it around its start, so everything the worker's loop runs carries the name
/// without the worker opening a scope of its own.
/// </summary>
/// <docs>operations/observability/database-attribution</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/DbMechanismWorkerScopeTests.cs</tests>
public interface IDbMechanismOwner {
  /// <summary>The mechanism this worker's statements are attributed to.</summary>
  DbMechanism DbMechanism { get; }
}
