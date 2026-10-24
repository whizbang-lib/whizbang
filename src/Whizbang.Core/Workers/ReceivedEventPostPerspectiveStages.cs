// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// Which path fires a received event's post-perspective stages (PostAllPerspectives and PostLifecycle) in a
/// service: the receive path, or the worker that applies the event. Each stage fires once per event per service,
/// so exactly one of them may (#1676).
/// </summary>
/// <remarks>
/// An event a per-stream perspective folds completes in the perspective worker once every perspective has applied
/// it. A collective event is applied by the collective sink, which fires the stages itself once its set-based apply
/// commits; no per-stream perspective lists a collective event type, so the receive path read it as an event with no
/// perspectives and fired the stages as well, and every hook ran twice. A service with no collective sink never
/// applies the event, so there the receive path still fires them.
/// </remarks>
/// <docs>fundamentals/messaging/collective-events</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/ReceivedEventPostPerspectiveStagesTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Workers/InboxDispatchWorkerCollectiveLifecycleTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerCollectiveSinkTests.LifecycleOnce.cs</tests>
internal static class ReceivedEventPostPerspectiveStages {
  /// <summary>
  /// Whether the receive path fires the post-perspective stages for a received event: true when nothing in this
  /// service applies the event later.
  /// </summary>
  /// <param name="messageType">The received row's message type name.</param>
  /// <param name="payload">The deserialized payload, or null when it could not be deserialized.</param>
  /// <param name="services">The dispatch scope's services.</param>
  internal static bool FiredOnReceipt(string messageType, object? payload, IServiceProvider services) {
    if (payload is ICollectiveEvent && services.GetService<ICollectiveDispatcher>() is not null) {
      return false;
    }
    var registry = services.GetService<IPerspectiveRunnerRegistry>();
    if (registry is null) {
      return true;
    }
    var normalized = EventTypeMatchingHelper.NormalizeTypeName(messageType);
    return !registry.GetRegisteredPerspectives().Any(perspective => perspective.EventTypes
      .Any(eventType => string.Equals(normalized, EventTypeMatchingHelper.NormalizeTypeName(eventType), StringComparison.Ordinal)));
  }
}
