// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

// For DispatchShapeMatrixComponentTests: one message type per (kind, owned, handler shape). The source
// generator discovers every receptor here; the runtime shapes are registered by the test. Each receptor
// records into ShapeProbe.

using System.Collections.Concurrent;
using Whizbang.Core.Messaging;

namespace Whizbang.Core.Component.Tests.ShapeMatrix {
  /// <summary>Counts each message's handler runs and its follow-up's runs, keyed by the message id.</summary>
  public static class ShapeProbe {
    private static readonly ConcurrentDictionary<(string, Guid), int> _counts = new();

    public static void Ran(Guid id) => _counts.AddOrUpdate(("ran", id), 1, static (_, n) => n + 1);

    public static void FollowedUp(Guid id) => _counts.AddOrUpdate(("followup", id), 1, static (_, n) => n + 1);

    public static int RunsOf(Guid id) => _counts.TryGetValue(("ran", id), out var n) ? n : 0;

    public static int FollowUpsOf(Guid id) => _counts.TryGetValue(("followup", id), out var n) ? n : 0;
  }

  public interface IShapeMessage : IMessage {
    Guid Id { get; }
  }

  /// <summary>A response that is not a message, returned beside a follow-up in a tuple.</summary>
  public record ShResponse(Guid Id);

  /// <summary>A handler registered at runtime; generic, so the generator skips it.</summary>
  public sealed class ShRuntimeVoidReceptor<TMessage> : IReceptor<TMessage> where TMessage : IShapeMessage {
    public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  /// <summary>A result-returning handler registered at runtime; generic, so the generator skips it.</summary>
  public sealed class ShRuntimeResultReceptor<TMessage> : IReceptor<TMessage, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> where TMessage : IShapeMessage {
    public ValueTask<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult(new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id));
    }
  }
}

namespace Whizbang.Core.Component.Tests.ShapeMatrix.Owned {
  /// <summary>What a result-returning handler returns; its own receptor proves the result cascaded and ran.</summary>
  public record ShFollowUp([property: StreamId] Guid Id) : IEvent;

  public sealed class ShFollowUpReceptor : IReceptor<ShFollowUp> {
    public ValueTask HandleAsync(ShFollowUp message, CancellationToken cancellationToken = default) {
      ShapeProbe.FollowedUp(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdOwnVoidAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdOwnVoidAsyncReceptor : IReceptor<ShCmdOwnVoidAsync> {
    public ValueTask HandleAsync(ShCmdOwnVoidAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdOwnResultAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdOwnResultAsyncReceptor : IReceptor<ShCmdOwnResultAsync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public ValueTask<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> HandleAsync(ShCmdOwnResultAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult(new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id));
    }
  }

  public record ShCmdOwnTupleAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdOwnTupleAsyncReceptor : IReceptor<ShCmdOwnTupleAsync, (ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> {
    public ValueTask<(ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> HandleAsync(ShCmdOwnTupleAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult((new ShResponse(message.Id), new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id)));
    }
  }

  public record ShCmdOwnVoidSync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdOwnVoidSyncReceptor : ISyncReceptor<ShCmdOwnVoidSync> {
    public void Handle(ShCmdOwnVoidSync message) => ShapeProbe.Ran(message.Id);
  }

  public record ShCmdOwnResultSync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdOwnResultSyncReceptor : ISyncReceptor<ShCmdOwnResultSync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp Handle(ShCmdOwnResultSync message) {
      ShapeProbe.Ran(message.Id);
      return new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id);
    }
  }

  public interface IShCmdOwnPolymorphic : ICommand, IShapeMessage;

  public record ShCmdOwnPolymorphic(Guid Id) : IShCmdOwnPolymorphic;

  public sealed class ShCmdOwnPolymorphicReceptor : IReceptor<IShCmdOwnPolymorphic> {
    public ValueTask HandleAsync(IShCmdOwnPolymorphic message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdOwnLocalStage(Guid Id) : ICommand, IShapeMessage;

  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class ShCmdOwnLocalStageReceptor : IReceptor<ShCmdOwnLocalStage> {
    public ValueTask HandleAsync(ShCmdOwnLocalStage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdOwnRuntimeVoid(Guid Id) : ICommand, IShapeMessage;

  public record ShCmdOwnRuntimeResult(Guid Id) : ICommand, IShapeMessage;

  public record ShEvtOwnVoidAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtOwnVoidAsyncReceptor : IReceptor<ShEvtOwnVoidAsync> {
    public ValueTask HandleAsync(ShEvtOwnVoidAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtOwnResultAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtOwnResultAsyncReceptor : IReceptor<ShEvtOwnResultAsync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public ValueTask<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> HandleAsync(ShEvtOwnResultAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult(new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id));
    }
  }

  public record ShEvtOwnTupleAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtOwnTupleAsyncReceptor : IReceptor<ShEvtOwnTupleAsync, (ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> {
    public ValueTask<(ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> HandleAsync(ShEvtOwnTupleAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult((new ShResponse(message.Id), new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id)));
    }
  }

  public record ShEvtOwnVoidSync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtOwnVoidSyncReceptor : ISyncReceptor<ShEvtOwnVoidSync> {
    public void Handle(ShEvtOwnVoidSync message) => ShapeProbe.Ran(message.Id);
  }

  public record ShEvtOwnResultSync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtOwnResultSyncReceptor : ISyncReceptor<ShEvtOwnResultSync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp Handle(ShEvtOwnResultSync message) {
      ShapeProbe.Ran(message.Id);
      return new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id);
    }
  }

  public interface IShEvtOwnPolymorphic : IEvent, IShapeMessage;

  public record ShEvtOwnPolymorphic([property: StreamId] Guid Id) : IShEvtOwnPolymorphic;

  public sealed class ShEvtOwnPolymorphicReceptor : IReceptor<IShEvtOwnPolymorphic> {
    public ValueTask HandleAsync(IShEvtOwnPolymorphic message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtOwnLocalStage([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class ShEvtOwnLocalStageReceptor : IReceptor<ShEvtOwnLocalStage> {
    public ValueTask HandleAsync(ShEvtOwnLocalStage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtOwnRuntimeVoid([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public record ShEvtOwnRuntimeResult([property: StreamId] Guid Id) : IEvent, IShapeMessage;

}

namespace Whizbang.Core.Component.Tests.ShapeMatrix.Unowned {
  public record ShCmdExtVoidAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdExtVoidAsyncReceptor : IReceptor<ShCmdExtVoidAsync> {
    public ValueTask HandleAsync(ShCmdExtVoidAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdExtResultAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdExtResultAsyncReceptor : IReceptor<ShCmdExtResultAsync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public ValueTask<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> HandleAsync(ShCmdExtResultAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult(new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id));
    }
  }

  public record ShCmdExtTupleAsync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdExtTupleAsyncReceptor : IReceptor<ShCmdExtTupleAsync, (ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> {
    public ValueTask<(ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> HandleAsync(ShCmdExtTupleAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult((new ShResponse(message.Id), new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id)));
    }
  }

  public record ShCmdExtVoidSync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdExtVoidSyncReceptor : ISyncReceptor<ShCmdExtVoidSync> {
    public void Handle(ShCmdExtVoidSync message) => ShapeProbe.Ran(message.Id);
  }

  public record ShCmdExtResultSync(Guid Id) : ICommand, IShapeMessage;

  public sealed class ShCmdExtResultSyncReceptor : ISyncReceptor<ShCmdExtResultSync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp Handle(ShCmdExtResultSync message) {
      ShapeProbe.Ran(message.Id);
      return new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id);
    }
  }

  public interface IShCmdExtPolymorphic : ICommand, IShapeMessage;

  public record ShCmdExtPolymorphic(Guid Id) : IShCmdExtPolymorphic;

  public sealed class ShCmdExtPolymorphicReceptor : IReceptor<IShCmdExtPolymorphic> {
    public ValueTask HandleAsync(IShCmdExtPolymorphic message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdExtLocalStage(Guid Id) : ICommand, IShapeMessage;

  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class ShCmdExtLocalStageReceptor : IReceptor<ShCmdExtLocalStage> {
    public ValueTask HandleAsync(ShCmdExtLocalStage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShCmdExtRuntimeVoid(Guid Id) : ICommand, IShapeMessage;

  public record ShCmdExtRuntimeResult(Guid Id) : ICommand, IShapeMessage;

  public record ShEvtExtVoidAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtExtVoidAsyncReceptor : IReceptor<ShEvtExtVoidAsync> {
    public ValueTask HandleAsync(ShEvtExtVoidAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtExtResultAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtExtResultAsyncReceptor : IReceptor<ShEvtExtResultAsync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public ValueTask<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> HandleAsync(ShEvtExtResultAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult(new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id));
    }
  }

  public record ShEvtExtTupleAsync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtExtTupleAsyncReceptor : IReceptor<ShEvtExtTupleAsync, (ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> {
    public ValueTask<(ShResponse, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp)> HandleAsync(ShEvtExtTupleAsync message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.FromResult((new ShResponse(message.Id), new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id)));
    }
  }

  public record ShEvtExtVoidSync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtExtVoidSyncReceptor : ISyncReceptor<ShEvtExtVoidSync> {
    public void Handle(ShEvtExtVoidSync message) => ShapeProbe.Ran(message.Id);
  }

  public record ShEvtExtResultSync([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public sealed class ShEvtExtResultSyncReceptor : ISyncReceptor<ShEvtExtResultSync, global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp> {
    public global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp Handle(ShEvtExtResultSync message) {
      ShapeProbe.Ran(message.Id);
      return new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShFollowUp(message.Id);
    }
  }

  public interface IShEvtExtPolymorphic : IEvent, IShapeMessage;

  public record ShEvtExtPolymorphic([property: StreamId] Guid Id) : IShEvtExtPolymorphic;

  public sealed class ShEvtExtPolymorphicReceptor : IReceptor<IShEvtExtPolymorphic> {
    public ValueTask HandleAsync(IShEvtExtPolymorphic message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtExtLocalStage([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  [FireAt(LifecycleStage.LocalImmediateInline)]
  public sealed class ShEvtExtLocalStageReceptor : IReceptor<ShEvtExtLocalStage> {
    public ValueTask HandleAsync(ShEvtExtLocalStage message, CancellationToken cancellationToken = default) {
      ShapeProbe.Ran(message.Id);
      return ValueTask.CompletedTask;
    }
  }

  public record ShEvtExtRuntimeVoid([property: StreamId] Guid Id) : IEvent, IShapeMessage;

  public record ShEvtExtRuntimeResult([property: StreamId] Guid Id) : IEvent, IShapeMessage;

}

namespace Whizbang.Core.Component.Tests.ShapeMatrix {
  /// <summary>Builds the message for a (kind, owned, shape) cell.</summary>
  public static class ShapeCatalog {
    public static readonly string[] Kinds = ["Cmd", "Evt"];
    public static readonly string[] Ownership = ["Own", "Ext"];
    public static readonly string[] Shapes = ["VoidAsync", "ResultAsync", "TupleAsync", "VoidSync", "ResultSync", "Polymorphic", "LocalStage", "RuntimeVoid", "RuntimeResult"];

    public static IShapeMessage Create(string kind, string owned, string shape, Guid id) => (kind, owned, shape) switch {
      ("Cmd", "Own", "VoidAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnVoidAsync(id),
      ("Cmd", "Own", "ResultAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnResultAsync(id),
      ("Cmd", "Own", "TupleAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnTupleAsync(id),
      ("Cmd", "Own", "VoidSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnVoidSync(id),
      ("Cmd", "Own", "ResultSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnResultSync(id),
      ("Cmd", "Own", "Polymorphic") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnPolymorphic(id),
      ("Cmd", "Own", "LocalStage") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnLocalStage(id),
      ("Cmd", "Own", "RuntimeVoid") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeVoid(id),
      ("Cmd", "Own", "RuntimeResult") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeResult(id),
      ("Cmd", "Ext", "VoidAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtVoidAsync(id),
      ("Cmd", "Ext", "ResultAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtResultAsync(id),
      ("Cmd", "Ext", "TupleAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtTupleAsync(id),
      ("Cmd", "Ext", "VoidSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtVoidSync(id),
      ("Cmd", "Ext", "ResultSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtResultSync(id),
      ("Cmd", "Ext", "Polymorphic") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtPolymorphic(id),
      ("Cmd", "Ext", "LocalStage") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtLocalStage(id),
      ("Cmd", "Ext", "RuntimeVoid") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeVoid(id),
      ("Cmd", "Ext", "RuntimeResult") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeResult(id),
      ("Evt", "Own", "VoidAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnVoidAsync(id),
      ("Evt", "Own", "ResultAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnResultAsync(id),
      ("Evt", "Own", "TupleAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnTupleAsync(id),
      ("Evt", "Own", "VoidSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnVoidSync(id),
      ("Evt", "Own", "ResultSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnResultSync(id),
      ("Evt", "Own", "Polymorphic") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnPolymorphic(id),
      ("Evt", "Own", "LocalStage") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnLocalStage(id),
      ("Evt", "Own", "RuntimeVoid") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeVoid(id),
      ("Evt", "Own", "RuntimeResult") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeResult(id),
      ("Evt", "Ext", "VoidAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtVoidAsync(id),
      ("Evt", "Ext", "ResultAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtResultAsync(id),
      ("Evt", "Ext", "TupleAsync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtTupleAsync(id),
      ("Evt", "Ext", "VoidSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtVoidSync(id),
      ("Evt", "Ext", "ResultSync") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtResultSync(id),
      ("Evt", "Ext", "Polymorphic") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtPolymorphic(id),
      ("Evt", "Ext", "LocalStage") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtLocalStage(id),
      ("Evt", "Ext", "RuntimeVoid") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeVoid(id),
      ("Evt", "Ext", "RuntimeResult") => new global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeResult(id),
      _ => throw new ArgumentException($"No message for ({kind}, {owned}, {shape})"),
    };

    /// <summary>Registers the runtime handler a runtime-shape message needs; nothing for a generated shape.</summary>
    public static void RegisterRuntimeHandler(global::Whizbang.Core.Messaging.IReceptorRegistry registry, IShapeMessage message) {
      switch (message) {
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeVoid:
          registry.Register(new ShRuntimeVoidReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeVoid>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeResult:
          registry.Register(new ShRuntimeResultReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShCmdOwnRuntimeResult>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeVoid:
          registry.Register(new ShRuntimeVoidReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeVoid>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeResult:
          registry.Register(new ShRuntimeResultReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShCmdExtRuntimeResult>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeVoid:
          registry.Register(new ShRuntimeVoidReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeVoid>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeResult:
          registry.Register(new ShRuntimeResultReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Owned.ShEvtOwnRuntimeResult>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeVoid:
          registry.Register(new ShRuntimeVoidReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeVoid>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
        case global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeResult:
          registry.Register(new ShRuntimeResultReceptor<global::Whizbang.Core.Component.Tests.ShapeMatrix.Unowned.ShEvtExtRuntimeResult>(), global::Whizbang.Core.Messaging.LifecycleStage.LocalImmediateInline);
          break;
      }
    }
  }
}
