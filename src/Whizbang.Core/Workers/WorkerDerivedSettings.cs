// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Options;
using Whizbang.Core.Configuration;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Workers;

/// <summary>
/// The limits the worker pipeline derives from what varies from host to host (the processor count, the gate), supplied to
/// the startup settings report through the same functions the workers size themselves with (#2251).
/// </summary>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Workers/WorkerDerivedSettingsTests.cs</tests>
internal sealed class WorkerDerivedSettings(
    IOptions<PerspectiveWorkerOptions> perspective,
    IOptions<InboxDispatchWorkerOptions> inboxDispatch,
    WorkCoordinatorGate gate) : IDerivedSettingSource {
  /// <inheritdoc />
  public IEnumerable<IDerivedSetting> DerivedSettings => [
    WorkerThreadPoolFloor.Applied,
    PerspectiveWorker.DeriveWidth(perspective.Value.MaxConcurrentDrainConsumers, perspective.Value.MaxConcurrentPerspectives, gate.MaxConcurrent),
    InboxDispatchWorker.DerivePartitionCount(inboxDispatch.Value.MaxConcurrentDispatch, gate.MaxConcurrent),
  ];
}
