// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Unit.Messaging.Tests.Generated;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Core.Tests.Messaging;

/// <summary>
/// A compiled receptor reached through the generated local dispatch paths (LocalInvokeAsync, SendAsync, a local publish)
/// that derives a strong id in its body (#2604, #2633). Host registrations only: the generated receptors and dispatcher.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Messaging/StrongIdDerivation.cs</code-under-test>
/// <code-under-test>src/Whizbang.Core/Dispatcher.cs</code-under-test>
/// <code-under-test>src/Whizbang.Generators/Templates/Snippets/DispatcherSnippets.cs</code-under-test>
[Category("Messaging")]
[NotInParallel("LocalInvokeDeriveFromProbe")] // the probe's recorder is shared by the host's receptor registration
public class LocalInvokeDeriveFromTests {
  [Test]
  public async Task LocalInvokeAsync_AtTopLevel_DeriveFromThrows_NamingLocalDispatchAsync() {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var dispatcher = services.GetRequiredService<IDispatcher>();

    await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromCommand("order-1"));

    await _assertThrewNamingLocalDispatchAsync(recorder);
  }

  [Test]
  public async Task SendAsync_AtTopLevel_DeriveFromThrows_NamingLocalDispatchAsync() {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var dispatcher = services.GetRequiredService<IDispatcher>();

    await dispatcher.SendAsync(new LocalInvokeDeriveFromCommand("order-1"));

    await _assertThrewNamingLocalDispatchAsync(recorder);
  }

  [Test]
  public async Task PublishAsync_AtTopLevel_DeriveFromThrows_NamingLocalDispatchAsync() {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var dispatcher = services.GetRequiredService<IDispatcher>();

    await dispatcher.PublishAsync(new LocalInvokeDeriveFromEvent((Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()));

    await _assertThrewNamingLocalDispatchAsync(recorder);
  }

  [Test]
  [Arguments(LocalInvokeDeriveFromPaths.VOID)]
  [Arguments(LocalInvokeDeriveFromPaths.SEND)]
  [Arguments(LocalInvokeDeriveFromPaths.TYPED)]
  [Arguments(LocalInvokeDeriveFromPaths.ANY)]
  [Arguments(LocalInvokeDeriveFromPaths.SYNC)]
  [Arguments(LocalInvokeDeriveFromPaths.VOID_SYNC)]
  [Arguments(LocalInvokeDeriveFromPaths.RUNNER)]
  [Arguments(LocalInvokeDeriveFromPaths.PUBLISH)]
  [Arguments(LocalInvokeDeriveFromPaths.CASCADE)]
  [Arguments(LocalInvokeDeriveFromPaths.RUNTIME)]
  public async Task LocalDispatch_FromInsideAHandling_TheInnerReceptorDerivesInItsOwnHandlingAsync(string path) {
    // A worker-stage receptor derives an id, then reaches a receptor locally; the inner one derives the same type and
    // purpose. In its own handling that is a second id; in the outer receptor's handling it was the same purpose twice.
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    services.GetRequiredService<IReceptorRegistry>().Register(
      new LocalInvokeDeriveFromRuntimeReceptor<LocalInvokeDeriveFromRuntimeCommand>(recorder), LifecycleStage.LocalImmediateInline);

    await _runOuterAsync(services, _outerEnvelope(path, Whizbang.Core.ValueObjects.MessageId.New(), scheme: null));

    var outcomes = recorder.Outcomes;
    await Assert.That(outcomes.Count).IsGreaterThanOrEqualTo(2);
    foreach (var outcome in outcomes) {
      await Assert.That(outcome.Error).IsNull().Because($"{outcome.Path}: the locally reached receptor runs in a handling of its own");
    }
    await Assert.That(outcomes.Select(o => o.Id).Distinct().Count()).IsEqualTo(outcomes.Count)
      .Because("each receptor, and each local invocation, derives under its own identity");
  }

  [Test]
  [Arguments(LocalInvokeDeriveFromPaths.VOID)]
  [Arguments(LocalInvokeDeriveFromPaths.TYPED)]
  [Arguments(LocalInvokeDeriveFromPaths.SYNC)]
  [Arguments(LocalInvokeDeriveFromPaths.RUNNER)]
  [Arguments(LocalInvokeDeriveFromPaths.PUBLISH)]
  public async Task LocalDispatch_ARetryOfTheOuterHandling_DerivesTheSameIdsAsync(string path) {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var messageId = Whizbang.Core.ValueObjects.MessageId.New();

    await _runOuterAsync(services, _outerEnvelope(path, messageId, scheme: HandlingDerivationScheme.OwnHandling));
    var first = recorder.Outcomes.Select(o => o.Id).ToList();
    await _runOuterAsync(services, _outerEnvelope(path, messageId, scheme: HandlingDerivationScheme.OwnHandling));
    var second = recorder.Outcomes.Skip(first.Count).Select(o => o.Id).ToList();

    await Assert.That(first).DoesNotContain((Guid?)null);
    await Assert.That(second).IsEquivalentTo(first, TUnit.Assertions.Enums.CollectionOrdering.Matching)
      .Because("a retry materializes a fresh envelope and invokes in the same order, so it derives the first run's ids");
  }

  [Test]
  public async Task LocalDispatch_ARowThatFirstRanUnderTheSharedScheme_KeepsItsIds_TheInnerSharesTheOuterHandlingAsync() {
    // A row with no recorded scheme ran on a build before #2633: its retry derives the old ids, the inner receptor in the
    // outer receptor's handling, under the outer receptor's identity
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var messageId = Whizbang.Core.ValueObjects.MessageId.New();
    var envelope = _outerEnvelope(LocalInvokeDeriveFromPaths.VOID_OTHER_PURPOSE, messageId, HandlingDerivationScheme.SharedWithCaller);

    await _runOuterAsync(services, envelope);

    var outerReceptorId = services.GetRequiredService<IReceptorRegistry>()
      .GetReceptorsFor(typeof(LocalInvokeDeriveFromOuterCommand), LifecycleStage.PostInboxInline)[0].ReceptorId;
    var oldId = StrongIdDerivation.Derive(new Handling(_outerEnvelope(LocalInvokeDeriveFromPaths.VOID_OTHER_PURPOSE, messageId, null),
      LocalInvokeDeriveFromHost.SERVICE_NAME, outerReceptorId), "4b7e2d9a-6c1f-4e3b-8a5d-9f0c1e2d3b4a", "inner", "LocalInvokeDeriveFromId");
    await Assert.That(recorder.Outcomes).Count().IsEqualTo(2);
    await Assert.That(recorder.Outcomes[1].Id).IsEqualTo(oldId)
      .Because("under the shared scheme the inner receptor derives exactly what the build before #2633 derived");
  }

  [Test]
  public async Task LocalDispatch_UnderTheSharedScheme_TheSamePurposeTwiceStillThrowsAsync() {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;

    await _runOuterAsync(services, _outerEnvelope(LocalInvokeDeriveFromPaths.VOID, Whizbang.Core.ValueObjects.MessageId.New(),
      HandlingDerivationScheme.SharedWithCaller));

    await Assert.That(recorder.Outcomes[1].Error!.Message).Contains("already called for purpose");
  }

  [Test]
  public async Task LocalDispatch_TheTwoSchemes_DeriveDifferentInnerIdsAsync() {
    var (services, recorder) = LocalInvokeDeriveFromHost.Build();
    await using var _ = services;
    var messageId = Whizbang.Core.ValueObjects.MessageId.New();

    await _runOuterAsync(services, _outerEnvelope(LocalInvokeDeriveFromPaths.VOID_OTHER_PURPOSE, messageId, HandlingDerivationScheme.SharedWithCaller));
    await _runOuterAsync(services, _outerEnvelope(LocalInvokeDeriveFromPaths.VOID_OTHER_PURPOSE, messageId, HandlingDerivationScheme.OwnHandling));

    var outcomes = recorder.Outcomes;
    await Assert.That(outcomes[0].Id).IsEqualTo(outcomes[2].Id).Because("the outer receptor's own ids never depend on the scheme");
    await Assert.That(outcomes[1].Id).IsNotEqualTo(outcomes[3].Id)
      .Because("the recorded scheme decides, so a retry across the deploy must use it rather than this build's");
  }

  private static async Task _runOuterAsync(ServiceProvider services, IMessageEnvelope envelope) {
    await using var scope = services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IReceptorInvoker>().InvokeAsync(envelope, LifecycleStage.PostInboxInline);
  }

  private static MessageEnvelope<LocalInvokeDeriveFromOuterCommand> _outerEnvelope(string path,
      Whizbang.Core.ValueObjects.MessageId messageId, HandlingDerivationScheme? scheme) => new() {
        MessageId = messageId,
        Payload = new LocalInvokeDeriveFromOuterCommand(path),
        Hops = [],
        DispatchContext = new MessageDispatchContext {
          Mode = DispatchModes.Outbox,
          Source = MessageSource.Inbox,
          HandlerName = "OuterHandler",
          DerivationScheme = scheme,
        },
      };

  private static async Task _assertThrewNamingLocalDispatchAsync(LocalInvokeDeriveFromRecorder recorder) {
    // Decision 1 (#2633): a fresh in-process call has no handled message, so a retry could not derive the same id. The
    // exception says so, naming local dispatch, instead of claiming the code is not a receptor body.
    await Assert.That(recorder.Outcomes).Count().IsEqualTo(1);
    var error = recorder.Outcomes[0].Error;
    await Assert.That(error).IsNotNull();
    await Assert.That(error!.Message).Contains("LocalInvokeAsync, SendAsync or a local publish");
    await Assert.That(error.Message).Contains("outside any handling");
  }
}

/// <summary>The host every local-invoke DeriveFrom test builds: the generated receptors, registry and dispatcher.</summary>
internal static class LocalInvokeDeriveFromHost {
  internal const string SERVICE_NAME = "orders-service";

  internal static (ServiceProvider Services, LocalInvokeDeriveFromRecorder Recorder) Build() {
    var recorder = new LocalInvokeDeriveFromRecorder();
    var services = new ServiceCollection()
      .AddSingleton(recorder)
      .AddSingleton<IServiceInstanceProvider>(new ServiceInstanceProvider(Guid.Empty, SERVICE_NAME, "test-host", 1));
    services.AddReceptors();
    services.AddWhizbangReceptorRegistry();
    services.AddWhizbangDispatcher();
    return (services.BuildServiceProvider(), recorder);
  }
}

/// <summary>The command the void local-invoke probe handles.</summary>
public sealed record LocalInvokeDeriveFromCommand(string Name) : ICommand;

/// <summary>The event the local publish probe handles.</summary>
public sealed record LocalInvokeDeriveFromEvent([property: StreamId] Guid OrderId) : IEvent;

/// <summary>The strong id the local-invoke probes derive.</summary>
[WhizbangId]
[PinnedId("4b7e2d9a-6c1f-4e3b-8a5d-9f0c1e2d3b4a")]
public readonly partial struct LocalInvokeDeriveFromId;

/// <summary>What each run of a probe's body derived, or the exception deriving threw, with the path it ran on.</summary>
public sealed class LocalInvokeDeriveFromRecorder {
  private readonly Lock _gate = new();
  private readonly List<(string Path, Guid? Id, Exception? Error)> _outcomes = [];

  /// <summary>Every outcome, in order.</summary>
  public IReadOnlyList<(string Path, Guid? Id, Exception? Error)> Outcomes {
    get {
      lock (_gate) {
        return [.. _outcomes];
      }
    }
  }

  /// <summary>Derives the probe id for <paramref name="purpose"/> in the current handling and records the outcome.</summary>
  internal void Derive(string path, string purpose = "order") {
    try {
      _record(path, LocalInvokeDeriveFromId.DeriveFrom(Handling.Current, purpose).Value, null);
    } catch (InvalidOperationException ex) {
      _record(path, null, ex);
    }
  }

  private void _record(string path, Guid? id, Exception? error) {
    lock (_gate) {
      _outcomes.Add((path, id, error));
    }
  }
}

/// <summary>A void receptor reached by local dispatch that derives a strong id in its body.</summary>
public sealed class LocalInvokeDeriveFromReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromCommand> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromCommand message, CancellationToken cancellationToken = default) {
    recorder.Derive("void");
    return ValueTask.CompletedTask;
  }
}

/// <summary>An event receptor reached by a local publish that derives a strong id in its body.</summary>
public sealed class LocalInvokeDeriveFromEventReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromEvent> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromEvent message, CancellationToken cancellationToken = default) {
    recorder.Derive("publish");
    return ValueTask.CompletedTask;
  }
}

/// <summary>The local dispatch paths the outer probe reaches the inner receptors on.</summary>
public static class LocalInvokeDeriveFromPaths {
  /// <summary>Void LocalInvokeAsync of a void receptor.</summary>
  public const string VOID = "void";
  /// <summary>Void LocalInvokeAsync of a void receptor that derives a purpose the outer receptor does not.</summary>
  public const string VOID_OTHER_PURPOSE = "void-other-purpose";
  /// <summary>SendAsync of a command a local receptor handles.</summary>
  public const string SEND = "send";
  /// <summary>Typed LocalInvokeAsync of a result-returning receptor.</summary>
  public const string TYPED = "typed";
  /// <summary>Void LocalInvokeAsync of a result-returning receptor (the any invoker).</summary>
  public const string ANY = "any";
  /// <summary>Typed LocalInvokeAsync of a synchronous receptor.</summary>
  public const string SYNC = "sync";
  /// <summary>Void LocalInvokeAsync of a void synchronous receptor.</summary>
  public const string VOID_SYNC = "void-sync";
  /// <summary>Void LocalInvokeAsync of a command two receptors handle (the local runner).</summary>
  public const string RUNNER = "runner";
  /// <summary>PublishAsync of an event a local receptor handles.</summary>
  public const string PUBLISH = "publish";
  /// <summary>Void LocalInvokeAsync of a receptor that returns an event a local receptor handles (the untyped publisher).</summary>
  public const string CASCADE = "cascade";
  /// <summary>Void LocalInvokeAsync of a command only a runtime-registered receptor handles.</summary>
  public const string RUNTIME = "runtime";
}

/// <summary>The command the outer, worker-stage probe handles: which path it reaches the inner receptors on.</summary>
public sealed record LocalInvokeDeriveFromOuterCommand(string Path) : ICommand;

/// <summary>A worker-stage receptor that derives an id, then reaches an inner receptor locally from inside its handling.</summary>
[FireAt(LifecycleStage.PostInboxInline)]
public sealed class LocalInvokeDeriveFromOuterReceptor(LocalInvokeDeriveFromRecorder recorder, IDispatcher dispatcher)
    : IReceptor<LocalInvokeDeriveFromOuterCommand> {
  /// <inheritdoc />
  public async ValueTask HandleAsync(LocalInvokeDeriveFromOuterCommand message, CancellationToken cancellationToken = default) {
    recorder.Derive("outer");
    switch (message.Path) {
      case LocalInvokeDeriveFromPaths.VOID:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromCommand("inner-1"));
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromCommand("inner-2"));
        break;
      case LocalInvokeDeriveFromPaths.VOID_OTHER_PURPOSE:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromOtherPurposeCommand());
        break;
      case LocalInvokeDeriveFromPaths.SEND:
        await dispatcher.SendAsync(new LocalInvokeDeriveFromCommand("inner-1"));
        break;
      case LocalInvokeDeriveFromPaths.TYPED:
        await dispatcher.LocalInvokeAsync<LocalInvokeDeriveFromAnswer>(new LocalInvokeDeriveFromQuery());
        break;
      case LocalInvokeDeriveFromPaths.ANY:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromQuery());
        break;
      case LocalInvokeDeriveFromPaths.SYNC:
        await dispatcher.LocalInvokeAsync<LocalInvokeDeriveFromAnswer>(new LocalInvokeDeriveFromSyncQuery());
        break;
      case LocalInvokeDeriveFromPaths.VOID_SYNC:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromSyncCommand());
        break;
      case LocalInvokeDeriveFromPaths.RUNNER:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromFanCommand());
        break;
      case LocalInvokeDeriveFromPaths.CASCADE:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromCascadingCommand());
        break;
      case LocalInvokeDeriveFromPaths.RUNTIME:
        await dispatcher.LocalInvokeAsync(new LocalInvokeDeriveFromRuntimeCommand());
        break;
      default:
        await dispatcher.PublishAsync(new LocalInvokeDeriveFromEvent((Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()));
        break;
    }
  }
}

/// <summary>The answer the result-returning probes give.</summary>
public sealed record LocalInvokeDeriveFromAnswer(Guid? Id);

/// <summary>A command whose receptor derives a purpose the outer receptor does not.</summary>
public sealed record LocalInvokeDeriveFromOtherPurposeCommand : ICommand;

/// <summary>A query a result-returning receptor answers.</summary>
public sealed record LocalInvokeDeriveFromQuery : ICommand;

/// <summary>A query a synchronous receptor answers.</summary>
public sealed record LocalInvokeDeriveFromSyncQuery : ICommand;

/// <summary>A command a void synchronous receptor handles.</summary>
public sealed record LocalInvokeDeriveFromSyncCommand : ICommand;

/// <summary>A command two receptors handle, so the local runner runs both.</summary>
public sealed record LocalInvokeDeriveFromFanCommand : ICommand;

/// <summary>Derives a purpose the outer receptor does not.</summary>
public sealed class LocalInvokeDeriveFromOtherPurposeReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromOtherPurposeCommand> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromOtherPurposeCommand message, CancellationToken cancellationToken = default) {
    recorder.Derive("other-purpose", "inner");
    return ValueTask.CompletedTask;
  }
}

/// <summary>A result-returning receptor that derives in its body.</summary>
public sealed class LocalInvokeDeriveFromQueryReceptor(LocalInvokeDeriveFromRecorder recorder)
    : IReceptor<LocalInvokeDeriveFromQuery, LocalInvokeDeriveFromAnswer> {
  /// <inheritdoc />
  public ValueTask<LocalInvokeDeriveFromAnswer> HandleAsync(LocalInvokeDeriveFromQuery message, CancellationToken cancellationToken = default) {
    recorder.Derive("typed");
    return ValueTask.FromResult(new LocalInvokeDeriveFromAnswer(null));
  }
}

/// <summary>A synchronous receptor that derives in its body.</summary>
public sealed class LocalInvokeDeriveFromSyncQueryReceptor(LocalInvokeDeriveFromRecorder recorder)
    : ISyncReceptor<LocalInvokeDeriveFromSyncQuery, LocalInvokeDeriveFromAnswer> {
  /// <inheritdoc />
  public LocalInvokeDeriveFromAnswer Handle(LocalInvokeDeriveFromSyncQuery message) {
    recorder.Derive("sync");
    return new LocalInvokeDeriveFromAnswer(null);
  }
}

/// <summary>A void synchronous receptor that derives in its body.</summary>
public sealed class LocalInvokeDeriveFromSyncCommandReceptor(LocalInvokeDeriveFromRecorder recorder)
    : ISyncReceptor<LocalInvokeDeriveFromSyncCommand> {
  /// <inheritdoc />
  public void Handle(LocalInvokeDeriveFromSyncCommand message) => recorder.Derive("void-sync");
}

/// <summary>The first of the two receptors the local runner runs.</summary>
public sealed class LocalInvokeDeriveFromFanFirstReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromFanCommand> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromFanCommand message, CancellationToken cancellationToken = default) {
    recorder.Derive("runner-first");
    return ValueTask.CompletedTask;
  }
}

/// <summary>The second of the two receptors the local runner runs.</summary>
public sealed class LocalInvokeDeriveFromFanSecondReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromFanCommand> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromFanCommand message, CancellationToken cancellationToken = default) {
    recorder.Derive("runner-second");
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command whose receptor returns an event, which cascades to a local receptor through the untyped publisher.</summary>
public sealed record LocalInvokeDeriveFromCascadingCommand : ICommand;

/// <summary>The event the cascading receptor returns.</summary>
public sealed record LocalInvokeDeriveFromCascadedEvent([property: StreamId] Guid OrderId) : IEvent;

/// <summary>Returns the event that cascades; derives nothing itself.</summary>
public sealed class LocalInvokeDeriveFromCascadingReceptor : IReceptor<LocalInvokeDeriveFromCascadingCommand, LocalInvokeDeriveFromCascadedEvent> {
  /// <inheritdoc />
  public ValueTask<LocalInvokeDeriveFromCascadedEvent> HandleAsync(LocalInvokeDeriveFromCascadingCommand message,
      CancellationToken cancellationToken = default) =>
    ValueTask.FromResult(new LocalInvokeDeriveFromCascadedEvent((Guid)Whizbang.Core.ValueObjects.TrackedGuid.New()));
}

/// <summary>The cascaded event's local receptor, which derives in its body.</summary>
public sealed class LocalInvokeDeriveFromCascadedEventReceptor(LocalInvokeDeriveFromRecorder recorder) : IReceptor<LocalInvokeDeriveFromCascadedEvent> {
  /// <inheritdoc />
  public ValueTask HandleAsync(LocalInvokeDeriveFromCascadedEvent message, CancellationToken cancellationToken = default) {
    recorder.Derive("cascade");
    return ValueTask.CompletedTask;
  }
}

/// <summary>A command only a runtime-registered receptor handles.</summary>
public sealed record LocalInvokeDeriveFromRuntimeCommand : ICommand;

/// <summary>A runtime-registered receptor that derives in its body. Generic, so the generator does not discover it.</summary>
public sealed class LocalInvokeDeriveFromRuntimeReceptor<TMessage>(LocalInvokeDeriveFromRecorder recorder) : IReceptor<TMessage>
    where TMessage : IMessage {
  /// <inheritdoc />
  public ValueTask HandleAsync(TMessage message, CancellationToken cancellationToken = default) {
    recorder.Derive("runtime");
    return ValueTask.CompletedTask;
  }
}
