namespace Whizbang.Core.Security.Attributes;

/// <summary>
/// Behavior when a receptor-level <see cref="RequirePermissionAttribute"/> denies an
/// invocation. Selects what the receptor invocation pipeline should do with the message —
/// dead-letter for forensic auditing, quarantine for review without blocking the stream,
/// or silently drop for best-effort receptors.
/// </summary>
/// <docs>fundamentals/security/security#receptor-permission-gate</docs>
/// <tests>tests/Whizbang.Core.Tests/Security/SecurityAttributeTests.cs</tests>
public enum DeniedAction {
  /// <summary>
  /// Throw <see cref="UnauthorizedAccessException"/>. The message routes through the
  /// existing inbox-failure / dead-letter pipeline. Default — loud and auditable.
  /// </summary>
  DeadLetter,
  /// <summary>
  /// Move the message to a separate "denied" inbox table for forensic review without
  /// blocking the main stream. Useful for abuse detection.
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
  /// Throw <see cref="UnauthorizedAccessException"/> and let the consumer's retry policy
  /// decide. Rarely the right answer; prefer <see cref="DeadLetter"/>.
  /// </summary>
  Throw,
}
