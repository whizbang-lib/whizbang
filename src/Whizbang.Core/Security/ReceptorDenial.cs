// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;
using Whizbang.Core.Security.Attributes;

namespace Whizbang.Core.Security;

/// <summary>
/// What a consumer worker does with a message whose receptors an interceptor denied before the message is completed
/// (#1996), shared by every worker that runs inbox receptors before it settles a message: the stages it asks about,
/// the dead-letter reason each action maps to, and the text the dead-letter record carries.
/// </summary>
/// <docs>fundamentals/security/security#denied-action</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerReceptorDenialTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/ServiceBusConsumerWorkerReceptorDenialTests.cs</tests>
internal static class ReceptorDenial {
  /// <summary>The stages inbox delivery runs a message's receptors at, which the interceptors are asked about first.</summary>
  internal static readonly LifecycleStage[] InboxHandlingStages = [
    LifecycleStage.PreInboxInline, LifecycleStage.PreInboxDetached,
    LifecycleStage.PostInboxInline, LifecycleStage.PostInboxDetached];

  /// <summary>
  /// The dead-letter reason a denial is recorded under: <see cref="MessageFailureReason.ReceptorDenied"/> for
  /// <see cref="DeniedAction.DeadLetter"/>, <see cref="MessageFailureReason.ReceptorQuarantined"/> for
  /// <see cref="DeniedAction.Quarantine"/>; null for an action that does not dead-letter.
  /// </summary>
  internal static MessageFailureReason? DeadLetterReasonFor(DeniedAction action) => action switch {
    DeniedAction.DeadLetter => MessageFailureReason.ReceptorDenied,
    DeniedAction.Quarantine => MessageFailureReason.ReceptorQuarantined,
    _ => null,
  };

  /// <summary>The text a denied message's dead-letter record or failure carries.</summary>
  internal static string Describe(Guid messageId, string? messageType, InterceptorResult denial) =>
    $"A receptor interceptor denied inbox message {messageId} ({messageType ?? "unknown type"}) with "
    + $"DeniedAction.{denial.OnDenied}: {denial.Reason ?? "no reason given"}";
}
