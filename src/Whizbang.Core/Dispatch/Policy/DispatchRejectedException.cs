// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Dispatch.Policy;

/// <summary>
/// The dispatch policy refused a dispatch for a reason that is not a missing handler: a command routed
/// to the event store only, a schedule that cannot hold, or a result the call cannot return.
/// </summary>
/// <docs>fundamentals/dispatcher/dispatch-policy#decision</docs>
public sealed class DispatchRejectedException : InvalidOperationException {
  /// <summary>Creates the exception.</summary>
  public DispatchRejectedException() { }

  /// <summary>Creates the exception with a message.</summary>
  public DispatchRejectedException(string message) : base(message) { }

  /// <summary>Creates the exception with a message and its cause.</summary>
  public DispatchRejectedException(string message, Exception innerException) : base(message, innerException) { }

  /// <summary>Creates the exception for a refused dispatch.</summary>
  /// <param name="rejection">Why the dispatch was refused.</param>
  /// <param name="messageType">The message type.</param>
  /// <param name="message">What the caller should know.</param>
  public DispatchRejectedException(DispatchRejection rejection, Type messageType, string message) : base(message) {
    Rejection = rejection;
    MessageType = messageType;
  }

  /// <summary>Why the dispatch was refused.</summary>
  public DispatchRejection Rejection { get; }

  /// <summary>The message type, when known.</summary>
  public Type? MessageType { get; }
}
