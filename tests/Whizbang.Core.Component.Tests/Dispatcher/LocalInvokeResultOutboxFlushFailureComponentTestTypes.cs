// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Messaging;

// Types for LocalInvokeResultOutboxFlushFailureComponentTests (#1871). The source generator discovers the receptor,
// so the test runs the REAL generated dispatcher and its LocalInvokeAsync result cascade.

namespace Whizbang.Core.Component.Tests.FlushFailure;

/// <summary>A command addressed to one aggregate stream.</summary>
public record FlRecordCommand([property: StreamId] Guid Id) : ICommand;

/// <summary>The event the receptor returns for the command's stream.</summary>
public record FlRecordedEvent([property: StreamId] Guid Id) : IEvent;

/// <summary>Always returns its event, as the reported receptors do.</summary>
public sealed class FlRecordCommandReceptor : IReceptor<FlRecordCommand, FlRecordedEvent> {
  public ValueTask<FlRecordedEvent> HandleAsync(FlRecordCommand message, CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new FlRecordedEvent(message.Id));
}
