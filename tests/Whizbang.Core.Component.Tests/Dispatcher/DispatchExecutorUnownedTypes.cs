// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Component.Tests.PolicyBehavior;

// Messages another service owns, for DispatchExecutorShapeComponentTests: what a cascade writes to the
// outbox for them must match what a send writes.

namespace Whizbang.Core.Component.Tests.ExecutorShapes.Unowned;

/// <summary>A command no receptor here handles or returns, so no generated cascade arm names it.</summary>
public record ExUnownedUnreturnedCommand(Guid Id) : ICommand, IPolicyProbeMessage;

/// <summary>A command a receptor here returns, so the generated cascade arm names it.</summary>
public record ExUnownedReturnedCommand(Guid Id) : ICommand, IPolicyProbeMessage;
