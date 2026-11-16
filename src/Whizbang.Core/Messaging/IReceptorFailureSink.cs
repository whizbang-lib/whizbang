// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Messaging;

/// <summary>
/// Receives each receptor that throws while <see cref="IReceptorInvoker.InvokeEveryReceptorAsync"/> runs every receptor
/// of a message at a lifecycle stage (#2310).
/// </summary>
/// <remarks>
/// The outbox flush passes one sink for a whole stage of a batch, so a failure costs nothing until one happens: the sink
/// logs the receptor, message type and message id, counts the error, and keeps the first exception for a caller that
/// must still rethrow it.
/// </remarks>
/// <docs>fundamentals/lifecycle/lifecycle-stages#distribute-failures</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/ReceptorInvokerFailureIsolationTests.cs</tests>
public interface IReceptorFailureSink {
  /// <summary>
  /// Records that a receptor threw at a stage. The invoker goes on to the next receptor.
  /// </summary>
  /// <param name="envelope">The envelope of the message the receptor was invoked for.</param>
  /// <param name="stage">The lifecycle stage the receptor ran at.</param>
  /// <param name="receptorId">The receptor that threw.</param>
  /// <param name="exception">What it threw.</param>
  void OnReceptorFailed(IMessageEnvelope envelope, LifecycleStage stage, string receptorId, Exception exception);
}
