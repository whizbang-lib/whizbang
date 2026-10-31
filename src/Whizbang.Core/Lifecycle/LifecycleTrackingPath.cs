// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Lifecycle;

/// <summary>
/// The processing path that tracks an event's lifecycle in this process. One event can be on several paths at once
/// (a local dispatch of an event this process also projects and publishes), and each path tracks it on its own: its
/// own stage guard, its own entry, its own end. One path ending its tracking never ends another path's (#1918).
/// </summary>
/// <docs>fundamentals/lifecycle/lifecycle-coordinator#live-tracking</docs>
/// <tests>tests/Whizbang.Core.Tests/Lifecycle/LifecycleTrackingPathTests.cs:EndingOnePathsTracking_NeverEndsAnotherPathsTrackingOfTheSameEventAsync</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Workers/PerspectiveWorkerDrainModeLifecycleTests.LocalDispatchOverlap.cs:LocalDispatchOfAnEventTheWorkerIsProjecting_BothPathsCompleteTheirOwnLifecycleAsync</tests>
public enum LifecycleTrackingPath {
  /// <summary>The dispatcher's in-process run of a message: its PostLifecycle after the local receptor.</summary>
  LocalDispatch,

  /// <summary>The perspective worker's projection of a stored event, through PostAllPerspectives and PostLifecycle.</summary>
  Perspective,

  /// <summary>The outbox publish worker's publication of a message to the transport.</summary>
  Outbox,

  /// <summary>A transport consumer's receipt of a message from the transport.</summary>
  Inbox
}
