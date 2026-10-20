// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;

// Commands and receptors for SendReceptorShapeIntegrationTests (#1379). They live in two
// namespaces so one can be declared owned and the other left unowned. The source generator
// discovers these receptors, so the tests exercise the REAL generated lookup tables.

namespace Whizbang.Core.Integration.Tests.SendShapes {
  /// <summary>
  /// Counts receptor invocations per command id. Each test sends commands with a fresh id, so
  /// parallel tests never observe each other's counts.
  /// </summary>
  public static class SendShapeInvocations {
    private static readonly ConcurrentDictionary<Guid, int> _counts = new();

    public static void Record(Guid id) => _counts.AddOrUpdate(id, 1, static (_, n) => n + 1);

    public static int CountFor(Guid id) => _counts.TryGetValue(id, out var n) ? n : 0;
  }
}

namespace Whizbang.Core.Integration.Tests.SendShapes.Owned {
  public sealed record OwnedVoidCommand(Guid Id) : ICommand;
  public sealed record OwnedSyncCommand(Guid Id) : ICommand;
  public sealed record OwnedVoidSyncCommand(Guid Id) : ICommand;
  public sealed record OwnedThrowingVoidCommand(Guid Id) : ICommand;
  public sealed record OwnedUnhandledCommand(Guid Id) : ICommand;
  // Receptor results are cascade candidates, so the generator requires them to be messages
  public sealed record OwnedSyncResult(Guid Id);

  public sealed class OwnedVoidCommandReceptor : IReceptor<OwnedVoidCommand> {
    public ValueTask HandleAsync(OwnedVoidCommand message, CancellationToken cancellationToken = default) {
      SendShapeInvocations.Record(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class OwnedSyncCommandReceptor : ISyncReceptor<OwnedSyncCommand, OwnedSyncResult> {
    public OwnedSyncResult Handle(OwnedSyncCommand message) {
      SendShapeInvocations.Record(message.Id);
      return new OwnedSyncResult(message.Id);
    }
  }

  public sealed class OwnedVoidSyncCommandReceptor : ISyncReceptor<OwnedVoidSyncCommand> {
    public void Handle(OwnedVoidSyncCommand message) => SendShapeInvocations.Record(message.Id);
  }

  public sealed record OwnedLocalInlineCommand(Guid Id) : ICommand;
  public sealed record OwnedLocalDetachedCommand(Guid Id) : ICommand;

  /// <summary>Declared at the local-dispatch stage, so it IS the command's handler.</summary>
  [Whizbang.Core.Messaging.FireAt(Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline)]
  public sealed class OwnedLocalInlineCommandReceptor : IReceptor<OwnedLocalInlineCommand> {
    public ValueTask HandleAsync(OwnedLocalInlineCommand message, CancellationToken cancellationToken = default) {
      SendShapeInvocations.Record(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  [Whizbang.Core.Messaging.FireAt(Whizbang.Core.Messaging.LifecycleStage.LocalImmediateDetached)]
  public sealed class OwnedLocalDetachedCommandReceptor : IReceptor<OwnedLocalDetachedCommand> {
    public ValueTask HandleAsync(OwnedLocalDetachedCommand message, CancellationToken cancellationToken = default) {
      SendShapeInvocations.Record(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public sealed class OwnedThrowingVoidCommandReceptor : IReceptor<OwnedThrowingVoidCommand> {
    public ValueTask HandleAsync(OwnedThrowingVoidCommand message, CancellationToken cancellationToken = default) {
      SendShapeInvocations.Record(message.Id);
      throw new InvalidOperationException($"receptor failed for {message.Id}");
    }
  }
}

namespace Whizbang.Core.Integration.Tests.SendShapes.Elsewhere {
  public sealed record UnownedVoidCommand(Guid Id) : ICommand;
  public sealed record ObservedOutgoingCommand(Guid Id) : ICommand;
  public sealed record ObservedOutgoingResultCommand(Guid Id) : ICommand;
  public sealed record ObservedOutgoingResult(Guid Id) : IMessage;

  /// <summary>
  /// A lifecycle hook on a command another service handles: it observes the command leaving
  /// through the outbox. It is NOT the command's handler, so SendAsync must not run it in place
  /// of routing the command to its owner.
  /// </summary>
  [Whizbang.Core.Messaging.FireAt(Whizbang.Core.Messaging.LifecycleStage.PostOutboxDetached)]
  public sealed class ObservedOutgoingCommandHook : IReceptor<ObservedOutgoingCommand> {
    public ValueTask HandleAsync(ObservedOutgoingCommand message, CancellationToken cancellationToken = default) {
      Whizbang.Core.Integration.Tests.SendShapes.SendShapeInvocations.Record(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  [Whizbang.Core.Messaging.FireAt(Whizbang.Core.Messaging.LifecycleStage.PostOutboxDetached)]
  public sealed class ObservedOutgoingResultCommandHook : IReceptor<ObservedOutgoingResultCommand, ObservedOutgoingResult> {
    public ValueTask<ObservedOutgoingResult> HandleAsync(ObservedOutgoingResultCommand message, CancellationToken cancellationToken = default) {
      Whizbang.Core.Integration.Tests.SendShapes.SendShapeInvocations.Record(message.Id);
      return ValueTask.FromResult(new ObservedOutgoingResult(message.Id));
    }
  }

  public sealed class UnownedVoidCommandReceptor : IReceptor<UnownedVoidCommand> {
    public ValueTask HandleAsync(UnownedVoidCommand message, CancellationToken cancellationToken = default) {
      Whizbang.Core.Integration.Tests.SendShapes.SendShapeInvocations.Record(message.Id);
      return ValueTask.CompletedTask;
    }
  }
}
