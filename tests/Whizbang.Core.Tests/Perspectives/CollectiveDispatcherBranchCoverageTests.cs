// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.Metrics;
using System.Linq.Expressions;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Tests.Perspectives;

/// <summary>
/// Branch backfill for <see cref="CollectiveDispatcher"/>: every metrics emission with metrics
/// wired (no-subscriber dispatch, missing resolver, missing executor, failed apply), and the
/// namespace tag of an event type that has no namespace.
/// </summary>
/// <remarks>
/// Each test reads its own <see cref="EventCategoryMetrics"/> instance's instruments, matched by
/// reference, so parallel tests that share the meter name cannot leak readings in.
/// </remarks>
[Category("Unit")]
[Category("CollectiveEvents")]
public class CollectiveDispatcherBranchCoverageTests {

  [Test]
  public async Task DispatchAsync_NoMatchingEntry_NamespacelessEvent_CountsDispatchWithEmptyNamespaceAsync() {
    var metrics = _newMetrics();
    var dispatcher = _build([], [], [], [], metrics);

    var evt = CollectiveDispatcherNamespacelessEventHost.Create(new TenantScope("t-1"));
    var result = await dispatcher.DispatchAsync(evt, Guid.NewGuid(), new object(), cancellationToken: default);

    await Assert.That(CollectiveDispatcherNamespacelessEventHost.EventType.Namespace).IsNull()
      .Because("precondition: the event type really has no namespace");
    await Assert.That(result.HandlerCount).IsEqualTo(0);
    var dispatched = _seriesWith(metrics.Dispatched, EventCategoryMetrics.Tags.SCOPE_KIND, "tenant");
    await Assert.That(dispatched.Count).IsEqualTo(1)
      .Because("a no-subscriber dispatch still counts producer activity when metrics are wired");
    await Assert.That(dispatched[0].Value).IsEqualTo(1L);
    await Assert.That(dispatched[0].Tags[EventCategoryMetrics.Tags.EVENT_NAMESPACE]).IsEqualTo(string.Empty)
      .Because("a type with no namespace is tagged with the empty string, never a null tag value");
  }

  [Test]
  public async Task DispatchAsync_NoResolverForScopeKind_WithMetrics_CountsResolverMissingErrorAsync() {
    var metrics = _newMetrics();
    var dispatcher = _build(
      [_entryFor<Archive>(typeof(JobModel), typeof(JobHandler))],
      [new StubResolver("workspace")],
      [new StubExecutor(typeof(JobModel), 0)],
      [new JobHandler()],
      metrics);

    await Assert.That(async () => await dispatcher.DispatchAsync(
        new Archive(new TenantScope("t-1")), Guid.NewGuid(), new object(), cancellationToken: default))
      .ThrowsExactly<InvalidOperationException>();

    var errors = _seriesWith(metrics.Errors, EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.RESOLVER_MISSING);
    await Assert.That(errors.Count).IsEqualTo(1)
      .Because("a missing resolver is counted under its own error class before the exception propagates");
    await Assert.That(errors[0].Value).IsEqualTo(1L);
    await Assert.That(_seriesWith(metrics.Dispatched, EventCategoryMetrics.Tags.SCOPE_KIND, "tenant")).IsEmpty()
      .Because("a dispatch that never found its resolver is not counted as dispatched");
  }

  [Test]
  public async Task DispatchAsync_NoExecutorForModelType_WithMetrics_CountsExecutorMissingErrorAndDurationAsync() {
    var metrics = _newMetrics();
    var durations = new List<double>();
    using var durationListener = _listenToHistogram(metrics.DispatchDuration, durations);
    var dispatcher = _build(
      [_entryFor<Archive>(typeof(JobModel), typeof(JobHandler))],
      [new StubResolver("tenant")],
      [new StubExecutor(typeof(ProfileModel), 0)],
      [new JobHandler()],
      metrics);

    await Assert.That(async () => await dispatcher.DispatchAsync(
        new Archive(new TenantScope("t-1")), Guid.NewGuid(), new object(), cancellationToken: default))
      .ThrowsExactly<InvalidOperationException>();

    var errors = _seriesWith(metrics.Errors, EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.EXECUTOR_MISSING);
    await Assert.That(errors.Count).IsEqualTo(1)
      .Because("a missing executor is counted under its own error class before the exception propagates");
    await Assert.That(errors[0].Value).IsEqualTo(1L);
    await Assert.That(_snapshot(durations).Count).IsEqualTo(1)
      .Because("the fan-out duration is recorded in the finally block even when the dispatch fails");
  }

  [Test]
  public async Task DispatchAsync_ApplyThrows_WithMetrics_CountsSqlExceptionErrorAsync() {
    var metrics = _newMetrics();
    var dispatcher = _build(
      [_entryFor<Archive>(typeof(JobModel), typeof(JobHandler))],
      [new StubResolver("tenant")],
      [new ThrowingExecutor(typeof(JobModel))],
      [new JobHandler()],
      metrics);

    await Assert.That(async () => await dispatcher.DispatchAsync(
        new Archive(new TenantScope("t-1")), Guid.NewGuid(), new object(), cancellationToken: default))
      .ThrowsExactly<InvalidTimeZoneException>();

    var errors = _seriesWith(metrics.Errors, EventCategoryMetrics.Tags.ERROR_CLASS, EventCategoryMetrics.ErrorClasses.SQL_EXCEPTION);
    await Assert.That(errors.Count).IsEqualTo(1)
      .Because("a failed apply is counted as a sql_exception error before it propagates");
    await Assert.That(errors[0].Value).IsEqualTo(1L);
  }

  // ── helpers ─────────────────────────────────────────────────────────────

  private static EventCategoryMetrics _newMetrics() =>
    new(new WhizbangMetrics(meterFactory: new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>()));

  /// <summary>Collects a passive counter once and returns the non-zero series carrying the given tag value.</summary>
  private static List<(long Value, Dictionary<string, string?> Tags)> _seriesWith(PassiveCounter<long> counter, string tagKey, string tagValue) {
    var readings = new List<(long Value, Dictionary<string, string?> Tags)>();
    using var listener = new MeterListener();
    listener.InstrumentPublished = (instrument, l) => {
      if (ReferenceEquals(instrument, counter.Instrument)) {
        l.EnableMeasurementEvents(instrument);
      }
    };
    listener.SetMeasurementEventCallback<long>((_, value, tags, _) => {
      var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
      foreach (var tag in tags) {
        dict[tag.Key] = tag.Value?.ToString();
      }
      lock (readings) {
        readings.Add((value, dict));
      }
    });
    listener.Start();
    listener.RecordObservableInstruments();
    lock (readings) {
      return [.. readings.Where(r => r.Value != 0
        && r.Tags.TryGetValue(tagKey, out var v)
        && string.Equals(v, tagValue, StringComparison.Ordinal))];
    }
  }

  private static MeterListener _listenToHistogram(Instrument histogram, List<double> sink) {
    var listener = new MeterListener {
      InstrumentPublished = (instrument, l) => {
        if (ReferenceEquals(instrument, histogram)) {
          l.EnableMeasurementEvents(instrument);
        }
      },
    };
    listener.SetMeasurementEventCallback<double>((_, value, _, _) => {
      lock (sink) {
        sink.Add(value);
      }
    });
    listener.Start();
    return listener;
  }

  private static List<double> _snapshot(List<double> sink) {
    lock (sink) {
      return [.. sink];
    }
  }

  private static CollectiveApplyEntry _entryFor<TEvent>(Type modelType, Type handlerType) =>
    new(
      ModelType: modelType,
      EventType: typeof(TEvent),
      HandlerType: handlerType,
      MethodName: "Apply",
      ScopeHandling: CollectiveScopeHandling.Framework,
      SpecKind: CollectiveSpecKind.Linq,
      Invoker: static (handler, _, _) => handler);

  private static CollectiveDispatcher _build(
      IReadOnlyList<CollectiveApplyEntry> entries,
      IReadOnlyList<ICollectiveScopeResolver> resolvers,
      IReadOnlyList<ICollectiveEventExecutor> executors,
      IReadOnlyList<object> handlers,
      EventCategoryMetrics metrics) {
    var services = new ServiceCollection();
    foreach (var h in handlers) {
      services.AddSingleton(h.GetType(), _ => h);
    }
    return new CollectiveDispatcher(services.BuildServiceProvider(), entries, resolvers, executors, metrics);
  }

  // ── inline test types (mirroring CollectiveDispatcherTests) ─────────────

  private sealed class JobModel {
    public string Status { get; set; } = string.Empty;
  }

  private sealed class ProfileModel {
    public string Name { get; set; } = string.Empty;
  }

  private sealed record TenantScope(string TenantId) : CollectiveScope {
    public override string ScopeKind => "tenant";
    public override string ScopeIdentity => ScopeKind + ":" + TenantId;
  }

  private sealed record Archive(CollectiveScope Scope) : ICollectiveEvent;

  private sealed class JobHandler;

  private sealed class StubResolver(string kind) : ICollectiveScopeResolver {
    public string ScopeKind => kind;
    public bool AcceptsPerspective<TModel>() where TModel : class => true;
    public Expression<Func<PerspectiveRow<TModel>, bool>> ScopeFilter<TModel>(ICollectiveScope scope)
      where TModel : class => _ => true;
    public IDisposable EnterContext(ICollectiveScope scope) => new Disposable();
    private sealed class Disposable : IDisposable { public void Dispose() { } }
  }

  private sealed class StubExecutor(Type modelType, int affectedRows) : ICollectiveEventExecutor {
    public Type ModelType { get; } = modelType;
    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt,
        ICollectiveScopeResolver resolver, object dbContextOrSession, Guid collectiveEventId,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) =>
      Task.FromResult(affectedRows);
  }

  private sealed class ThrowingExecutor(Type modelType) : ICollectiveEventExecutor {
    public Type ModelType { get; } = modelType;
    public Task<int> ApplyAsync(
        CollectiveApplyEntry entry, object handlerInstance, ICollectiveEvent evt,
        ICollectiveScopeResolver resolver, object dbContextOrSession, Guid collectiveEventId,
        Func<CancellationToken, ValueTask>? onBatchApplied = null, CancellationToken cancellationToken = default) =>
      throw new InvalidTimeZoneException("simulated apply failure");
  }
}
