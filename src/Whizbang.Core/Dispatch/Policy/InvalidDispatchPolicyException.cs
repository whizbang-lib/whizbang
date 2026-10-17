// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>A dispatch policy or hook produced a decision the dispatcher cannot carry out.</summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#guardrails</docs>
public sealed class InvalidDispatchPolicyException : InvalidOperationException {
  /// <summary>Creates the exception.</summary>
  public InvalidDispatchPolicyException() { }

  /// <summary>Creates the exception with a message.</summary>
  public InvalidDispatchPolicyException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and its cause.</summary>
  public InvalidDispatchPolicyException(string message, Exception innerException) : base(message, innerException) { }
}
