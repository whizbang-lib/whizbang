using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Unit tests (no Postgres) for the A1 increment-5 scheduled-close bridge: the built-in
/// <see cref="ScheduledStreamCloseReceptor"/> turns a fired <see cref="ScheduledStreamClose"/> occurrence into
/// an <see cref="IStreamCloser.CloseAsync"/> call, and <see cref="ScheduledStreamCloseReceptorRegistrar"/>
/// runtime-registers it at the three default lifecycle stages.
/// </summary>
/// <docs>fundamentals/events/ephemeral-events</docs>
[Category("Shard2")]
public class ScheduledStreamCloseReceptorTests {
  private sealed class RecordingCloser : IStreamCloser {
    public (Guid StreamId, long Through, bool Archive)? LastCall { get; private set; }
    public int Calls { get; private set; }
    public Task<StreamCloseResult> CloseAsync(Guid streamId, long throughVersion, bool archive = false, CancellationToken cancellationToken = default) {
      Calls++;
      LastCall = (streamId, throughVersion, archive);
      return Task.FromResult(new StreamCloseResult("closed", 5));
    }
  }

  /// <summary>Captures what the receptor logged, so a "did nothing" path can be told apart from
  /// a "did nothing and said nothing" one.</summary>
  private sealed class RecordingLogger : ILogger<ScheduledStreamCloseReceptor> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
      Func<TState, Exception?, string> formatter) {
      ArgumentNullException.ThrowIfNull(formatter);
      Entries.Add((logLevel, formatter(state, exception)));
    }
  }

  private sealed class RecordingRegistry : IReceptorRegistry {
    public List<(Type Msg, LifecycleStage Stage)> Registered { get; } = [];
    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage =>
      Registered.Add((typeof(TMessage), stage));
    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  [Test]
  public async Task Receptor_FiredOccurrence_CallsStreamCloserWithPayloadAsync() {
    var closer = new RecordingCloser();
    var services = new ServiceCollection();
    services.AddSingleton<IStreamCloser>(closer);
    await using var sp = services.BuildServiceProvider();
    var receptor = new ScheduledStreamCloseReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledStreamCloseReceptor>.Instance);

    var target = Guid.NewGuid();
    await receptor.HandleAsync(new ScheduledStreamClose(target, ThroughVersion: 900, Archive: true));

    await Assert.That(closer.Calls).IsEqualTo(1);
    await Assert.That(closer.LastCall!.Value.StreamId).IsEqualTo(target);
    await Assert.That(closer.LastCall!.Value.Through).IsEqualTo(900L);
    await Assert.That(closer.LastCall!.Value.Archive).IsTrue()
      .Because("The receptor forwards the occurrence's {streamId, throughVersion, archive} straight to IStreamCloser.CloseAsync.");
  }

  [Test]
  public async Task Receptor_NoStreamCloserRegistered_IsInertAsync() {
    var services = new ServiceCollection();   // no IStreamCloser
    await using var sp = services.BuildServiceProvider();
    var logger = new RecordingLogger();
    var receptor = new ScheduledStreamCloseReceptor(
      sp.GetRequiredService<IServiceScopeFactory>(), logger);
    var streamId = Guid.NewGuid();

    // Must not throw — a host without an IStreamCloser simply ignores the occurrence.
    await receptor.HandleAsync(new ScheduledStreamClose(streamId, 1, false));

    // Inert is not the same as silent: a scheduled close that fired and closed nothing is a
    // configuration fault, so the ignored occurrence must name the stream it dropped.
    var warnings = logger.Entries.Count(e =>
      e.Level == LogLevel.Warning
      && e.Message.Contains(streamId.ToString(), StringComparison.Ordinal));
    await Assert.That(warnings).IsEqualTo(1)
      .Because("A fired ScheduledStreamClose with no IStreamCloser must warn once, naming the stream.");
  }

  [Test]
  public async Task Registrar_RegistersReceptorAtThreeDefaultStagesAsync() {
    var registry = new RecordingRegistry();
    var services = new ServiceCollection();
    services.AddSingleton<IReceptorRegistry>(registry);
    await using var sp = services.BuildServiceProvider();
    var registrar = new ScheduledStreamCloseReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledStreamCloseReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);

    await Assert.That(registry.Registered.Count).IsEqualTo(3);
    var stages = new HashSet<LifecycleStage>();
    foreach (var (msg, stage) in registry.Registered) {
      await Assert.That(msg).IsEqualTo(typeof(ScheduledStreamClose));
      stages.Add(stage);
    }
    await Assert.That(stages.Contains(LifecycleStage.LocalImmediateInline)).IsTrue();
    await Assert.That(stages.Contains(LifecycleStage.PreOutboxInline)).IsTrue();
    await Assert.That(stages.Contains(LifecycleStage.PostInboxInline)).IsTrue()
      .Because("A receptor without [FireAt] fires at all three default stages, so the occurrence reaches it in-process and over the inbox.");
  }

  [Test]
  public async Task Registrar_NoRegistry_IsInertAsync() {
    var services = new ServiceCollection();   // no IReceptorRegistry
    await using var sp = services.BuildServiceProvider();
    var registrar = new ScheduledStreamCloseReceptorRegistrar(
      sp, sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<ScheduledStreamCloseReceptor>.Instance);

    await registrar.StartAsync(CancellationToken.None);   // must not throw
  }
}
