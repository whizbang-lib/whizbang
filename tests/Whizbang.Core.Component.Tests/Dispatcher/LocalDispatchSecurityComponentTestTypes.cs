// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

// Types for LocalDispatchSecurityComponentTests (#1419). Each message has a business receptor and a
// PostLifecycleInline receptor, so every local run reaches a lifecycle stage after its handler: the point
// where a missing security context used to be refused, after the handler had already run. The source
// generator discovers every receptor here, so the tests run the REAL generated lookups and registry.

namespace Whizbang.Core.Component.Tests.LocalDispatchSecurity;

/// <summary>What ran, per message id. Each test uses a fresh id.</summary>
public static class LocalSecurityProbe {
  private static readonly ConcurrentDictionary<Guid, int> _handled = new();
  private static readonly ConcurrentDictionary<Guid, int> _postLifecycle = new();

  public static void RecordHandled(Guid id) => _handled.AddOrUpdate(id, 1, static (_, n) => n + 1);
  public static void RecordPostLifecycle(Guid id) => _postLifecycle.AddOrUpdate(id, 1, static (_, n) => n + 1);

  public static int HandledCount(Guid id) => _handled.TryGetValue(id, out var n) ? n : 0;
  public static int PostLifecycleCount(Guid id) => _postLifecycle.TryGetValue(id, out var n) ? n : 0;
}

public record SlCommand(Guid Id) : ICommand;

public record SlResultCommand(Guid Id) : ICommand;

public record SlResult(Guid Id);

public record SlEvent([property: StreamId] Guid Id) : IEvent;

public sealed class SlCommandReceptor : IReceptor<SlCommand> {
  public ValueTask HandleAsync(SlCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class SlResultCommandReceptor : IReceptor<SlResultCommand, SlResult> {
  public ValueTask<SlResult> HandleAsync(SlResultCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.FromResult(new SlResult(message.Id));
  }
}

public sealed class SlEventReceptor : IReceptor<SlEvent> {
  public ValueTask HandleAsync(SlEvent message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class SlCommandPostLifecycle : IReceptor<SlCommand> {
  public ValueTask HandleAsync(SlCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordPostLifecycle(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class SlResultCommandPostLifecycle : IReceptor<SlResultCommand> {
  public ValueTask HandleAsync(SlResultCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordPostLifecycle(message.Id);
    return ValueTask.CompletedTask;
  }
}

[FireAt(LifecycleStage.PostLifecycleInline)]
[ReceptorIdempotent]
public sealed class SlEventPostLifecycle : IReceptor<SlEvent> {
  public ValueTask HandleAsync(SlEvent message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordPostLifecycle(message.Id);
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command the application lists in <c>ExemptMessageTypes</c>.</summary>
public record SlExemptCommand(Guid Id) : ICommand;

/// <summary>A framework-style control-plane command, never refused for a missing context.</summary>
public record SlControlPlaneCommand(Guid Id) : ICommand, IControlPlaneMessage;

/// <summary>A system event, never refused for a missing context.</summary>
public record SlSystemEvent([property: StreamId] Guid Id) : Whizbang.Core.SystemEvents.ISystemEvent;

public sealed class SlExemptCommandReceptor : IReceptor<SlExemptCommand> {
  public ValueTask HandleAsync(SlExemptCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class SlControlPlaneCommandReceptor : IReceptor<SlControlPlaneCommand> {
  public ValueTask HandleAsync(SlControlPlaneCommand message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}

public sealed class SlSystemEventReceptor : IReceptor<SlSystemEvent> {
  public ValueTask HandleAsync(SlSystemEvent message, CancellationToken cancellationToken = default) {
    LocalSecurityProbe.RecordHandled(message.Id);
    return ValueTask.CompletedTask;
  }
}
