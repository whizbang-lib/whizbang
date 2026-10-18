// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch;

/// <summary>
/// Thrown by a generated invoker when a receptor's result is not the type the caller asked for, most
/// often a tuple that holds the response alongside the events to cascade. It carries the whole result,
/// so the dispatcher extracts the response from it and cascades the rest without running the receptor
/// a second time. Derives from <see cref="InvalidCastException"/>, which a caller catching the old
/// failed cast still sees.
/// </summary>
/// <param name="result">Everything the receptor returned.</param>
/// <param name="requestedType">The response type the caller asked for.</param>
/// <docs>fundamentals/dispatcher/dispatcher#localinvokeasync</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/Dispatcher/DispatchExecutorParityComponentTests.cs</tests>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "RCS1194:Implement exception constructors",
  Justification = "The exception exists to carry a receptor's result; a constructor without one would make an instance the dispatcher cannot act on.")]
public sealed class ReceptorResultMismatchException(object result, Type requestedType)
    : InvalidCastException($"The receptor returned {result.GetType().Name}, not the requested {requestedType.Name}.") {
  /// <summary>Everything the receptor returned.</summary>
  public object Result { get; } = result;

  /// <summary>The response type the caller asked for.</summary>
  public Type RequestedType { get; } = requestedType;
}
