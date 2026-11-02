// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Security.Exceptions;

/// <summary>
/// Thrown in place of a receptor's invocation when a registered <see cref="IReceptorInterceptor"/> denied it with
/// <see cref="DeniedAction.DeadLetter"/>, <see cref="DeniedAction.Quarantine"/> or <see cref="DeniedAction.Throw"/>.
/// The receptor did not run and returned nothing, so nothing it would have emitted was appended or cascaded.
/// </summary>
/// <remarks>
/// <para>
/// A local dispatch hands this to its caller whatever the action, since a local call has no inbox to dead-letter from.
/// On inbox delivery the worker checks the interceptors before it completes the row: a dead-letter or quarantine
/// denial moves the row to the dead-letter store without a retry, and a throw denial is a failed attempt that the
/// attempts bound retries. A <see cref="DeniedAction.DropQuiet"/> denial never throws.
/// </para>
/// </remarks>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorGateTests.cs</tests>
public sealed class ReceptorInvocationDeniedException : UnauthorizedAccessException {
  /// <summary>The receptor that was not invoked. Null only when built through a general-purpose constructor.</summary>
  public Type? ReceptorType { get; }

  /// <summary>What the interceptor asked the pipeline to do with the message.</summary>
  public DeniedAction Action { get; }

  /// <summary>The interceptor's reason, for diagnostics only. Null when it gave none.</summary>
  public string? Reason { get; }

  /// <summary>Creates the exception with a default message.</summary>
  public ReceptorInvocationDeniedException()
    : base("A receptor interceptor denied the invocation.") {
  }

  /// <summary>Creates the exception with the given message.</summary>
  /// <param name="message">The error message.</param>
  public ReceptorInvocationDeniedException(string message)
    : base(message) {
  }

  /// <summary>Creates the exception with the given message and inner exception.</summary>
  /// <param name="message">The error message.</param>
  /// <param name="innerException">The inner exception.</param>
  public ReceptorInvocationDeniedException(string message, Exception innerException)
    : base(message, innerException) {
  }

  /// <summary>Creates the exception for a denied receptor invocation.</summary>
  /// <param name="receptorType">The receptor that was not invoked.</param>
  /// <param name="action">The action the interceptor chose.</param>
  /// <param name="reason">The interceptor's reason, if it gave one.</param>
  public ReceptorInvocationDeniedException(Type receptorType, DeniedAction action, string? reason)
    : base(_describe(receptorType, action, reason)) {
    ReceptorType = receptorType;
    Action = action;
    Reason = reason;
  }

  /// <summary>
  /// Creates the exception for a message an interceptor denied before any of its receptors ran, when the denial is
  /// known for the message rather than for one receptor (a consumer's admission check before it settles a message).
  /// </summary>
  /// <param name="message">The error message.</param>
  /// <param name="action">The action the interceptor chose.</param>
  /// <param name="reason">The interceptor's reason, if it gave one.</param>
  public ReceptorInvocationDeniedException(string message, DeniedAction action, string? reason)
    : base(message) {
    Action = action;
    Reason = reason;
  }

  private static string _describe(Type receptorType, DeniedAction action, string? reason) =>
    $"Receptor {TypeNameFormatter.DisplayName(receptorType)} was not invoked: "
    + (reason ?? "a receptor interceptor denied the invocation")
    + $" (DeniedAction.{action}).";
}
