// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Messaging;

/// <summary>
/// How a handling derives the ids of a receptor it invokes locally (#2633). The scheme a handling first ran under is
/// recorded on its inbox row, and every retry of the row runs under the recorded scheme, so a handling that started on
/// one build and is retried on another keeps the ids of its first run.
/// </summary>
/// <remarks>
/// The numbers are a compatibility contract: they are stored in <c>wh_inbox_state.derivation_scheme</c>, and a row
/// that records none ran on a build that predates the column, which is <see cref="SharedWithCaller"/>.
/// </remarks>
/// <docs>fundamentals/identity/whizbang-ids#a-receptor-invoked-locally</docs>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/LocalReceptorHandlingTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Messaging.Tests/Messaging/LocalInvokeDeriveFromTests.cs</tests>
public enum HandlingDerivationScheme {
  /// <summary>
  /// A receptor invoked locally from inside a handling runs in that handling: it derives, sends and emits under the
  /// caller's identity and counters. The behavior of every build before #2633, and of a row that records no scheme.
  /// </summary>
  SharedWithCaller = 0,

  /// <summary>
  /// A receptor invoked locally from inside a handling runs in a handling of its own, anchored on an id derived for that
  /// one call (<see cref="LocalInvocationIdentity"/>): it derives, sends and emits under its own identity and counters.
  /// </summary>
  OwnHandling = 1,
}
