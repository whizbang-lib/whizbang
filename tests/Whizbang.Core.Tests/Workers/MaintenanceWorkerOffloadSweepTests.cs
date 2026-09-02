using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Offloads;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Covers the passive offload-claim sweep inside the maintenance cycle — the pass that
/// deletes blob bodies whose claims have aged out.
/// </summary>
/// <remarks>
/// The sweep deletes data in an external store, so its failure handling is what keeps a
/// single bad blob, or a provider that is no longer registered, from stalling the whole
/// cycle. None of those paths had tests.
/// </remarks>
[Category("Core")]
[Category("Workers")]
public class MaintenanceWorkerOffloadSweepTests {

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class CapturingLogger : ILogger<MaintenanceWorker> {
    public List<LogEntry> Entries { get; } = [];
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      lock (Entries) {
        Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
      }
    }
    private sealed class NullScope : IDisposable {
      public static readonly NullScope Instance = new();
      public void Dispose() { }
    }
  }

  private sealed class SweepCoordinator : IWorkCoordinator {
    public bool GrantSweep { get; init; } = true;
    public Exception? ScanThrows { get; init; }
    /// <summary>Fails the claim-removal step, which sits outside the per-blob try.</summary>
    public Exception? RemoveThrows { get; init; }
    public List<OffloadClaimRecord> Expired { get; init; } = [];
    public List<string> Removed { get; } = [];
    private int _batches;

    public Task<bool> TryClaimOffloadSweepAsync(TimeSpan claimWindow, CancellationToken ct = default)
      => Task.FromResult(GrantSweep);

    public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
        TimeSpan olderThan, int batchSize, CancellationToken ct = default) {
      if (ScanThrows is not null) {
        return Task.FromException<IReadOnlyList<OffloadClaimRecord>>(ScanThrows);
      }
      // One batch of claims, then nothing — otherwise the sweep loops to its batch ceiling.
      return Task.FromResult<IReadOnlyList<OffloadClaimRecord>>(
        Interlocked.Increment(ref _batches) == 1 ? Expired : []);
    }

    public Task RemoveOffloadClaimsAsync(
        IReadOnlyCollection<string> storageKeys, CancellationToken ct = default) {
      if (RemoveThrows is not null) {
        return Task.FromException(RemoveThrows);
      }
      lock (Removed) { Removed.AddRange(storageKeys); }
      return Task.CompletedTask;
    }

    public Task DeregisterInstanceAsync(Guid instanceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<WorkCoordinatorStatistics> GatherStatisticsAsync(CancellationToken ct = default)
      => Task.FromResult(new WorkCoordinatorStatistics());
    public Task<PerspectiveCursorInfo?> GetPerspectiveCursorAsync(
        Guid streamId, string perspectiveName, CancellationToken ct = default)
      => Task.FromResult<PerspectiveCursorInfo?>(null);
    public Task ReportPerspectiveCompletionAsync(PerspectiveCursorCompletion completion, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task ReportPerspectiveFailureAsync(PerspectiveCursorFailure failure, CancellationToken ct = default)
      => Task.CompletedTask;
    public Task StoreInboxMessagesAsync(InboxMessage[] messages, int partitionCount, CancellationToken ct = default)
      => Task.CompletedTask;
  }

  private sealed class RecordingStore(string providerName, Exception? deleteThrows = null) : IMessageBodyStore {
    public string ProviderName => providerName;
    public List<string> Deleted { get; } = [];

    public Task DeleteAsync(MessageBodyClaim claim, MessageBodyDeleteOptions? options = null, CancellationToken ct = default) {
      if (deleteThrows is not null) {
        return Task.FromException(deleteThrows);
      }
      lock (Deleted) { Deleted.Add(claim.StorageKey); }
      return Task.CompletedTask;
    }

    public Task<MessageBodyClaim> UploadAsync(
        ReadOnlyMemory<byte> body, string contentType, MessageBodyUploadOptions? options = null,
        CancellationToken ct = default) => throw new NotImplementedException();
    public Task<ReadOnlyMemory<byte>> DownloadAsync(
        MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
        CancellationToken ct = default) => throw new NotImplementedException();
  }

  private static (MaintenanceWorker Worker, CapturingLogger Logger) _build(
      SweepCoordinator coord,
      TimeSpan? passiveExpiry,
      params (string Provider, IMessageBodyStore Store)[] stores) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coord);
    services.AddSingleton<IOptionsMonitor<MessageBodyOffloadOptions>>(
      new StaticOptionsMonitor(new MessageBodyOffloadOptions {
        PassiveExpiry = passiveExpiry,
        PassiveSweepBatchSize = 100,
        PassiveSweepMaxBatchesPerCycle = 2,
      }));
    foreach (var (provider, store) in stores) {
      services.AddKeyedSingleton(provider, store);
    }
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    var logger = new CapturingLogger();
    var worker = new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      logger);
    return (worker, logger);
  }

  private sealed class StaticOptionsMonitor(MessageBodyOffloadOptions value)
      : IOptionsMonitor<MessageBodyOffloadOptions> {
    public MessageBodyOffloadOptions CurrentValue => value;
    public MessageBodyOffloadOptions Get(string? name) => value;
    public IDisposable? OnChange(Action<MessageBodyOffloadOptions, string?> listener) => null;
  }

  [Test]
  public async Task NoPassiveExpiry_SkipsTheSweepEntirelyAsync() {
    // Null expiry is the off switch: the provider-side lifecycle rule is the only cleanup,
    // so the sweep must not claim a window or read a batch.
    var coord = new SweepCoordinator { Expired = { new OffloadClaimRecord("k1", "blob") } };
    var store = new RecordingStore("blob");
    var (worker, _) = _build(coord, passiveExpiry: null, ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Deleted).IsEmpty();
    await Assert.That(coord.Removed).IsEmpty();
  }

  [Test]
  public async Task AnotherReplicaHoldsTheClaim_SkipsTheSweepAsync() {
    var coord = new SweepCoordinator {
      GrantSweep = false,
      Expired = { new OffloadClaimRecord("k1", "blob") },
    };
    var store = new RecordingStore("blob");
    var (worker, _) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Deleted).IsEmpty();
  }

  [Test]
  public async Task ExpiredClaims_AreDeletedFromTheStoreAndForgottenAsync() {
    var coord = new SweepCoordinator {
      Expired = { new OffloadClaimRecord("k1", "blob"), new OffloadClaimRecord("k2", "blob") },
    };
    var store = new RecordingStore("blob");
    var (worker, _) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Deleted).Contains("k1");
    await Assert.That(store.Deleted).Contains("k2");
    await Assert.That(coord.Removed).Contains("k1");
    await Assert.That(coord.Removed).Contains("k2");
  }

  [Test]
  public async Task UnregisteredProvider_IsLoggedAndTheClaimIsLeftAloneAsync() {
    // The claim names a provider this host has no store for. Deleting the claim row would
    // orphan the blob with nothing left pointing at it, so the sweep leaves both.
    var coord = new SweepCoordinator {
      Expired = { new OffloadClaimRecord("k1", "provider-not-here") },
    };
    var (worker, logger) = _build(coord, TimeSpan.FromHours(1));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Removed).IsEmpty();

    int logged;
    lock (logger.Entries) {
      logged = logger.Entries.Count;
    }

    await Assert.That(logged).IsGreaterThan(0)
      .Because("a claim naming a provider this host cannot serve is surfaced, not silently skipped");
  }

  [Test]
  public async Task OneBlobDeleteFails_TheRestOfTheBatchStillSweepsAsync() {
    // A single unreachable blob must not strand the others: the claim for the failed one
    // stays so a later cycle retries it.
    var coord = new SweepCoordinator {
      Expired = { new OffloadClaimRecord("bad", "blob") },
    };
    var store = new RecordingStore("blob", new InvalidOperationException("blob gone"));
    var (worker, logger) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coord.Removed).IsEmpty()
      .Because("the delete failed, so the claim stays for a later cycle to retry");

    List<LogEntry> failures;
    lock (logger.Entries) {
      failures = logger.Entries.Where(e => e.Exception is InvalidOperationException).ToList();
    }
    await Assert.That(failures).IsNotEmpty();
  }

  [Test]
  public async Task NothingExpired_EndsTheSweepWithoutTouchingTheStoreAsync() {
    var coord = new SweepCoordinator();   // no expired claims at all
    var store = new RecordingStore("blob");
    var (worker, _) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Deleted).IsEmpty();
    await Assert.That(coord.Removed).IsEmpty();
  }

  [Test]
  public async Task ScanFails_TheMaintenanceCycleStillCompletesAsync() {
    // Reclaiming blob storage is housekeeping. A failed scan must not take down the cycle
    // that also reclaims rows — losing that to an offload query would be a poor trade.
    var coord = new SweepCoordinator { ScanThrows = new InvalidOperationException("scan failed") };
    var store = new RecordingStore("blob");
    var (worker, logger) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await worker.RunMaintenanceOnceAsync(CancellationToken.None);

    List<LogEntry> failures;
    lock (logger.Entries) {
      failures = logger.Entries.Where(e => e.Exception is InvalidOperationException).ToList();
    }

    await Assert.That(failures).IsNotEmpty();
    await Assert.That(store.Deleted).IsEmpty();
  }

  // ============================================================
  // Cancellation, which each of these steps must let through
  // ============================================================

  [Test]
  public async Task ABlobDeleteCancelled_StopsTheSweepInsteadOfMovingToTheNextBlobAsync() {
    // The companion to OneBlobDeleteFails_TheRestOfTheBatchStillSweeps, and the opposite answer.
    // One unreachable blob must not strand the others; a cancelled delete is a stopping host, and
    // continuing means more provider round trips while shutdown waits on them. The claims stay
    // either way, so nothing is lost by ending the pass here.
    var coord = new SweepCoordinator {
      Expired = { new OffloadClaimRecord("first", "blob"), new OffloadClaimRecord("second", "blob") },
    };
    var store = new RecordingStore("blob", new OperationCanceledException());
    var (worker, _) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>()
      .Because("a sweep that keeps calling the blob provider after shutdown is what makes a host "
             + "hang on exit; the claims are still there for the next cycle");
    await Assert.That(coord.Removed).IsEmpty()
      .Because("nothing was deleted, so nothing may be forgotten — a claim removed without its "
             + "blob gone is a leak the passive sweep can no longer find");
  }

  [Test]
  public async Task ClaimRemovalCancelled_StopsTheCycleRatherThanBeingLoggedAsync() {
    // The step after the deletes, and the one that matters most to get right: the blobs are gone
    // and the claims are the only record that they were. Its failure is logged and swallowed like
    // any sweep failure, but cancellation has to travel — the rest of the cycle reaps rows.
    var coord = new SweepCoordinator {
      Expired = { new OffloadClaimRecord("gone", "blob") },
      RemoveThrows = new OperationCanceledException(),
    };
    var store = new RecordingStore("blob");
    var (worker, logger) = _build(coord, TimeSpan.FromHours(1), ("blob", store));

    await Assert.That(async () => await worker.RunMaintenanceOnceAsync(CancellationToken.None))
      .Throws<OperationCanceledException>();
    await Assert.That(store.Deleted).Contains("gone")
      .Because("the delete happened before the cancellation — the claim it left behind is exactly "
             + "the record a later cycle needs to finish the job");
    List<LogEntry> failures;
    lock (logger.Entries) {
      failures = logger.Entries.Where(e => e.Exception is OperationCanceledException).ToList();
    }
    await Assert.That(failures).IsEmpty()
      .Because("a shutdown logged as a sweep failure is noise on every deploy");
  }
}
