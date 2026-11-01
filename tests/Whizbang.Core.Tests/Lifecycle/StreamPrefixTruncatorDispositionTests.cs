// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Core.Tests.Workers;

namespace Whizbang.Core.Tests.Lifecycle;

/// <summary>
/// #1735 (WS-3 S6): the prefix truncate runs the disposition its caller declares, or the one its PreDestruction hook
/// returns. <c>Delete</c> discards through the coordinator; <c>Archive</c> writes a prefix batch through the archive
/// store; <c>Compact</c> runs the registered compaction handler and then discards, and with no handler keeps the data and
/// logs why; <c>CryptoShred</c> is refused and logged. A hook that proceeds without naming a disposition keeps the declared
/// one.
/// </summary>
/// <docs>fundamentals/events/stream-end-of-life#dispositions</docs>
public class StreamPrefixTruncatorDispositionTests {
  private static readonly Guid _stream = new("0192f1a0-0000-7000-8000-0000000000bb");

  private sealed class RecordingStore : IArchiveStore {
    public List<ArchiveBatch> Written { get; } = [];

    public Task<ArchiveWriteResult> WriteAsync(ArchiveBatch batch, CancellationToken cancellationToken = default) {
      Written.Add(batch);
      return Task.FromResult(new ArchiveWriteResult(ArchiveStatus.ARCHIVED, 3, null, null, 1));
    }

    public async IAsyncEnumerable<ArchivedEvent> ReadAsync(
        Guid streamId, long fromVersion = 0, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
      await Task.CompletedTask;
      yield break;
    }

    public Task<ArchiveRestoreResult> RestoreAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();

    public Task<long> DeleteAsync(Guid streamId, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException();
  }

  private sealed class CountingCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<(long Through, bool Archive)> Truncates { get; } = [];
    public IReadOnlyList<string> Consumers { get; init; } = [];

    Task<StreamTruncateResult> IWorkCoordinator.TruncateStreamPrefixAsync(
        Guid streamId, long throughVersion, bool archive, CancellationToken cancellationToken) {
      Truncates.Add((throughVersion, archive));
      return Task.FromResult(new StreamTruncateResult("truncated", 5));
    }

    Task<IReadOnlyList<string>> IWorkCoordinator.GetConsumingPerspectiveNamesAsync(
        Guid streamId, long throughVersion, CancellationToken cancellationToken) => Task.FromResult(Consumers);
  }

  private sealed class DecidingHook(DestructionResult decision) : IDestructionHook {
    public DestructionContext? Seen { get; private set; }

    public ValueTask<DestructionResult> OnBeforeDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Seen = context;
      return ValueTask.FromResult(decision);
    }

    public ValueTask OnAfterDestructionAsync(DestructionContext context, CancellationToken cancellationToken = default) =>
      ValueTask.CompletedTask;
  }

  private sealed class RecordingCompactor(List<string> log) : ICompactionHandler {
    public DestructionContext? Seen { get; private set; }

    public ValueTask CompactAsync(DestructionContext context, CancellationToken cancellationToken = default) {
      Seen = context;
      log.Add("compact");
      return ValueTask.CompletedTask;
    }
  }

  private sealed class ListLogger : ILogger<StreamPrefixTruncator> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Entries.Add((logLevel, formatter(state, exception)));
  }

  private static IServiceScopeFactory _services(ICompactionHandler? compactor = null) {
    var services = new ServiceCollection();
    if (compactor is not null) {
      services.AddSingleton(compactor);
    }
    return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
  }

  private static StreamPrefixTruncator _truncator(CountingCoordinator coordinator, IDestructionHook hook, IServiceScopeFactory? services = null,
      IArchiveStore? store = null, ListLogger? logger = null) =>
    new(coordinator, (ILogger<StreamPrefixTruncator>?)logger ?? new ListLogger(), hook, services ?? _services(), store);

  [Test]
  public async Task Declared_Archive_WritesThroughTheStore_AndTheHookSeesItAsTheDeclaredDefaultAsync() {
    var store = new RecordingStore();
    var coordinator = new CountingCoordinator();
    var hook = new DecidingHook(DestructionResult.Proceed());

    var result = await _truncator(coordinator, hook, store: store).TruncateAsync(_stream, 9, Disposition.Archive);

    await Assert.That(store.Written).IsEquivalentTo([ArchiveBatch.Prefix(_stream, 9)]);
    await Assert.That(coordinator.Truncates).IsEmpty();
    await Assert.That(result.Status).IsEqualTo("truncated");
    await Assert.That(hook.Seen!.DeclaredDefault).IsEqualTo(Disposition.Archive);
    await Assert.That(hook.Seen.ThroughVersion).IsEqualTo(9L).Because("a stream-granularity hook is told how far the truncate reaches");
  }

  [Test]
  public async Task AHookThatProceedsWithoutNamingADisposition_KeepsTheDeclaredOne_NeverDeletesAnArchiveAsync() {
    // Proceed() used to mean Delete; a hook written that way must not turn a declared archive into a discard.
    var store = new RecordingStore();
    var coordinator = new CountingCoordinator();

    _ = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed()), store: store)
      .TruncateAsync(_stream, 9, Disposition.Archive);

    await Assert.That(DestructionResult.Proceed().Disposition).IsNull();
    await Assert.That(store.Written).Count().IsEqualTo(1);
    await Assert.That(coordinator.Truncates).IsEmpty();
  }

  [Test]
  public async Task AHookReturningArchive_OverridesADeclaredDelete_AndOneReturningDelete_OverridesADeclaredArchiveAsync() {
    var toArchive = new RecordingStore();
    var deleted = new CountingCoordinator();
    var archived = new CountingCoordinator();
    var toDelete = new RecordingStore();

    _ = await _truncator(archived, new DecidingHook(DestructionResult.Proceed(Disposition.Archive)), store: toArchive)
      .TruncateAsync(_stream, 9, Disposition.Delete);
    _ = await _truncator(deleted, new DecidingHook(DestructionResult.Proceed(Disposition.Delete)), store: toDelete)
      .TruncateAsync(_stream, 9, Disposition.Archive);

    await Assert.That(toArchive.Written).Count().IsEqualTo(1);
    await Assert.That(archived.Truncates).IsEmpty();
    await Assert.That(toDelete.Written).IsEmpty();
    await Assert.That(deleted.Truncates).IsEquivalentTo([(9L, false)]);
  }

  [Test]
  public async Task AHookReturningDelete_IsStillRefused_WhenAFullHistoryProjectionConsumesTheRangeAsync() {
    // The full-history guard runs on the disposition that will actually run, so a hook cannot strand a projection that
    // must replay from the start by switching a declared archive to a discard.
    var name = "FullHistoryPerspective_" + Guid.NewGuid().ToString("N");
    Whizbang.Core.Perspectives.FullHistoryPerspectiveRegistry.Register(name);
    var coordinator = new CountingCoordinator { Consumers = [name] };
    var store = new RecordingStore();

    var refused = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed(Disposition.Delete)), store: store)
      .TruncateAsync(_stream, 9, Disposition.Archive);
    var archived = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed()), store: store)
      .TruncateAsync(_stream, 9, Disposition.Archive);

    await Assert.That(refused.Status).IsEqualTo("full_history_blocked");
    await Assert.That(archived.Status).IsEqualTo("truncated").Because("the control: archiving keeps the detail, so it is allowed");
    await Assert.That(coordinator.Truncates).IsEmpty();
  }

  [Test]
  public async Task Compact_RunsTheRegisteredHandler_ThenDiscardsTheDetailAsync() {
    var log = new List<string>();
    var compactor = new RecordingCompactor(log);
    var coordinator = new CountingCoordinator();

    var result = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed()), _services(compactor))
      .TruncateAsync(_stream, 9, Disposition.Compact);

    await Assert.That(log).IsEquivalentTo(["compact"]);
    await Assert.That(compactor.Seen!.StreamId).IsEqualTo(_stream);
    await Assert.That(compactor.Seen.ThroughVersion).IsEqualTo(9L);
    await Assert.That(coordinator.Truncates).IsEquivalentTo([(9L, false)])
      .Because("the handler folds the detail into its summary; the detail is then discarded");
    await Assert.That(result.Status).IsEqualTo("truncated");
  }

  [Test]
  public async Task Compact_WithNoHandler_KeepsTheData_AndLogsWhyAsync() {
    var coordinator = new CountingCoordinator();
    var logger = new ListLogger();

    var result = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed(Disposition.Compact)), logger: logger)
      .TruncateAsync(_stream, 9);

    await Assert.That(result).IsEqualTo(new StreamTruncateResult(StreamTruncateStatus.COMPACTION_UNAVAILABLE, 0));
    await Assert.That(coordinator.Truncates).IsEmpty().Because("without a handler nothing could fold the detail, so none is discarded");
    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains(_stream.ToString(), StringComparison.Ordinal)
      && e.Message.Contains(nameof(ICompactionHandler), StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task CryptoShred_IsRefused_KeepsTheData_AndLogsWhyAsync() {
    var coordinator = new CountingCoordinator();
    var store = new RecordingStore();
    var logger = new ListLogger();

    var result = await _truncator(coordinator, new DecidingHook(DestructionResult.Proceed()), store: store, logger: logger)
      .TruncateAsync(_stream, 9, Disposition.CryptoShred);

    await Assert.That(result).IsEqualTo(new StreamTruncateResult(StreamTruncateStatus.CRYPTO_SHRED_UNAVAILABLE, 0));
    await Assert.That(coordinator.Truncates).IsEmpty();
    await Assert.That(store.Written).IsEmpty();
    await Assert.That(logger.Entries.Exists(e => e.Level == LogLevel.Warning && e.Message.Contains("CryptoShred", StringComparison.Ordinal))).IsTrue();
  }
}
