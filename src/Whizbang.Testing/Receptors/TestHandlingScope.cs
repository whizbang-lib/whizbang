// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Testing.Receptors;

/// <summary>
/// The handling <see cref="TestHandling"/> entered (#2609). While it is not disposed, <see cref="Handling.Current"/> is
/// <see cref="Handling"/>; disposing it ends the handling and restores the one before it, as a receptor body returning
/// does. Disposing it again does nothing, so it never undoes a handling entered since.
/// </summary>
/// <docs>operations/testing/testing-receptors#derived-ids</docs>
/// <tests>tests/Whizbang.Testing.Tests/Receptors/TestHandlingTests.cs:Dispose_EndsTheHandling_AndRestoresTheOneBeforeItAsync</tests>
/// <tests>tests/Whizbang.Testing.Tests/Receptors/TestHandlingTests.cs:Dispose_ASecondTime_DoesNotUndoAHandlingEnteredSinceAsync</tests>
public sealed class TestHandlingScope : IDisposable, IAsyncDisposable {
  private readonly ReceptorHandling.Scope _scope;
  private bool _disposed;

  internal TestHandlingScope(ReceptorHandling.Scope scope, IMessageEnvelope envelope) {
    _scope = scope;
    Envelope = envelope;
    Handling = Handling.Current;
  }

  /// <summary>The handling entered: <see cref="Handling.Current"/> until the scope is disposed.</summary>
  public Handling Handling { get; }

  /// <summary>The envelope of the handled message; entering it again is a retry of the same handling.</summary>
  public IMessageEnvelope Envelope { get; }

  /// <summary>Ends the handling and restores the one before it.</summary>
  public void Dispose() {
    if (_disposed) {
      return;
    }
    _disposed = true;
    _scope.Dispose();
  }

  /// <summary>Ends the handling and restores the one before it.</summary>
  /// <returns>A completed task: ending a handling does no I/O.</returns>
  /// <remarks>
  /// Not an async method: the handling is ambient, and an async method's changes to it would not reach the test that
  /// awaits it, which would stay in the ended handling.
  /// </remarks>
  public ValueTask DisposeAsync() {
    Dispose();
    return ValueTask.CompletedTask;
  }
}
