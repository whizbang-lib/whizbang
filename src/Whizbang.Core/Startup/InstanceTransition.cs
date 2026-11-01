// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.RunControl;

namespace Whizbang.Core.Startup;

/// <summary>
/// One transition of an instance, as its row records it (#1636): the stage and what it is doing, the lifecycle phase,
/// and the library version, written together in one update by <see cref="Messaging.IWorkCoordinator.RecordInstanceTransitionAsync"/>.
/// </summary>
/// <param name="InstanceId">The instance whose row records it.</param>
/// <param name="Stage">The stage the instance is in; <see langword="null"/> keeps the stage the row records (a phase
/// entered before any stage).</param>
/// <param name="Message">What the stage is doing; ignored when <paramref name="Stage"/> is null.</param>
/// <param name="Phase">The lifecycle phase: the stage's own (<see cref="StartupStages.LifecyclePhaseOf"/>), or a phase off
/// the startup ladder (a pause, a stand-by) that keeps the stage.</param>
/// <param name="LibraryVersion">The library version this instance runs; <see langword="null"/> keeps the recorded one.</param>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
/// <tests>tests/Whizbang.Core.Tests/Startup/StartupStageReporterTests.cs</tests>
public sealed record InstanceTransition(
  Guid InstanceId, StartupStage? Stage, string? Message, LifecyclePhase Phase, string? LibraryVersion);
