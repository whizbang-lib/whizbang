// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Component.Tests.PolicyBehavior;

// Messages that fill the decision table's (kind, owned, handler) cells the PolicyBehavior types leave
// empty, for DecisionTableExecutionComponentTests. Each receptor records into PolicyProbe.

namespace Whizbang.Core.Component.Tests.PolicyBehavior.Owned {
  public record ScOwnedEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public sealed class ScOwnedEventReceptor : IReceptor<ScOwnedEvent> {
    public ValueTask HandleAsync(ScOwnedEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(ScOwnedEventReceptor), message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>An owned event nothing here handles.</summary>
  public record ScOwnedUnhandledEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;
}

namespace Whizbang.Core.Component.Tests.PolicyBehavior {
  /// <summary>An unowned event nothing here handles.</summary>
  public record ScUnhandledEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public record ScResponse(Guid Id);

  /// <summary>An unowned command whose handler returns a response, for rows that need a result.</summary>
  public record ScRpcCommand(Guid Id) : ICommand, IPolicyProbeMessage;

  public sealed class ScRpcCommandReceptor : IReceptor<ScRpcCommand, ScResponse> {
    public ValueTask<ScResponse> HandleAsync(ScRpcCommand message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(ScRpcCommandReceptor), message.Id);
      return ValueTask.FromResult(new ScResponse(message.Id));
    }
  }

  /// <summary>An unowned event whose handler returns a response, for rows that need a result.</summary>
  public record ScRpcEvent([property: StreamId] Guid Id) : IEvent, IPolicyProbeMessage;

  public sealed class ScRpcEventReceptor : IReceptor<ScRpcEvent, ScResponse> {
    public ValueTask<ScResponse> HandleAsync(ScRpcEvent message, CancellationToken cancellationToken = default) {
      PolicyProbe.Record(nameof(ScRpcEventReceptor), message.Id);
      return ValueTask.FromResult(new ScResponse(message.Id));
    }
  }
}
