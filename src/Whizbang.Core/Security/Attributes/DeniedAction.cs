// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Behavior when a receptor-level <see cref="RequirePermissionAttribute"/> denies an
/// invocation. Selects what the receptor invocation pipeline should do with the message —
/// dead-letter for forensic auditing, quarantine for review without blocking the stream,
/// or silently drop for best-effort receptors.
/// </summary>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorTests.cs:PermissionMissing_OnDeniedQuarantine_DeniesWithQuarantineAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorTests.cs:MultipleDenials_StrictestActionWinsAsync</tests>
/// <tests>tests/Whizbang.Core.Tests/Security/ReceptorInterceptorPipelineTests.cs:EvaluateAll_PicksStrictestActionAsync</tests>
public enum DeniedAction {
  /// <summary>
  /// Refuse loudly. A local dispatch throws <see cref="Exceptions.ReceptorInvocationDeniedException"/> (an
  /// <see cref="UnauthorizedAccessException"/>) to its caller; inbox delivery moves the message straight to the
  /// dead-letter store (reason <c>ReceptorDenied</c>) without a retry. The default: loud and auditable.
  /// </summary>
  DeadLetter,
  /// <summary>
  /// Set the message aside for review without blocking the stream. Inbox delivery holds it in the dead-letter
  /// store under its own reason (<c>ReceptorQuarantined</c>), apart from failures, and never retries it on its
  /// own; a local dispatch throws <see cref="Exceptions.ReceptorInvocationDeniedException"/> to its caller.
  /// Useful for abuse detection.
  /// </summary>
  Quarantine,
  /// <summary>
  /// Acknowledge, log a warning, and drop the message. Use only on best-effort receptors
  /// where loss is genuinely fine (e.g., "increment a usage counter"). Silently discards
  /// the message — companion analyzer flags this on receptors whose <c>HandleAsync</c>
  /// returns a result.
  /// </summary>
  DropQuiet,
  /// <summary>
  /// Throw <see cref="Exceptions.ReceptorInvocationDeniedException"/> (an <see cref="UnauthorizedAccessException"/>)
  /// and let the retry policy decide: inbox delivery fails the attempt, and the attempts bound retries it.
  /// Rarely the right answer; prefer <see cref="DeadLetter"/>.
  /// </summary>
  Throw,
}
