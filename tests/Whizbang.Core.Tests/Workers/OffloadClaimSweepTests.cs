using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Offloads;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Locks the passive offload-claim sweep: the maintenance cycle drains the expired half of the
/// claim ledger — delete the blob, then remove the row, successes only.
/// </summary>
/// <remarks>
/// <para>
/// The invariant under test everywhere here: <b>the ledger row outlives the blob, never the
/// reverse.</b> A failed blob delete keeps its row and is retried next sweep; a row is removed
/// only after its blob is confirmed gone (missing counts — <c>DeleteAsync</c> is idempotent on
/// not-found). Removing a row whose blob still exists would orphan the blob with no record, which
/// is the exact failure the ledger exists to kill.
/// </para>
/// <para>
/// The sweep is age-based, not consumption-based, which is what makes it fan-out safe: no
/// consumer's action can break a sibling subscriber's download. It is fleet-elected (settings CAS
/// watermark) so N replicas do not issue N delete storms against one container, and it is
/// entirely off by <c>PassiveExpiry = null</c>, in which case the provider-side lifecycle rule is
/// the only cleanup.
/// </para>
/// </remarks>
/// <docs>fundamentals/messaging/body-offload</docs>
public class OffloadClaimSweepTests {

  private sealed class SweepCoordinator : NoOpWorkCoordinator, IWorkCoordinator {
    public List<OffloadClaimRecord> Expired { get; init; } = [];
    public bool ClaimResult { get; init; } = true;
    public int GetExpiredCalls { get; private set; }
    public List<string> Removed { get; } = [];

    public Task<bool> TryClaimOffloadSweepAsync(
      TimeSpan claimWindow, CancellationToken cancellationToken = default) =>
      Task.FromResult(ClaimResult);

    public Task<IReadOnlyList<OffloadClaimRecord>> GetExpiredOffloadClaimsAsync(
        TimeSpan olderThan, int batchSize, CancellationToken cancellationToken = default) {
      GetExpiredCalls++;
      // One batch of work, then empty — the sweep must stop on an empty batch.
      var result = GetExpiredCalls == 1 ? Expired : [];
      return Task.FromResult<IReadOnlyList<OffloadClaimRecord>>(result);
    }

    public Task RemoveOffloadClaimsAsync(
        IReadOnlyCollection<string> storageKeys, CancellationToken cancellationToken = default) {
      Removed.AddRange(storageKeys);
      return Task.CompletedTask;
    }
  }

  private sealed class SweepStore : IMessageBodyStore {
    public string ProviderName => "blob";
    public List<string> Deleted { get; } = [];
    public string? ThrowOnKey { get; init; }

    public Task<MessageBodyClaim> UploadAsync(
      ReadOnlyMemory<byte> body, string contentType,
      MessageBodyUploadOptions? options = null, CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("sweep never uploads");

    public Task<ReadOnlyMemory<byte>> DownloadAsync(
      MessageBodyClaim claim, MessageBodyDownloadOptions? options = null,
      CancellationToken cancellationToken = default) =>
      throw new NotSupportedException("sweep never downloads");

    public Task DeleteAsync(
        MessageBodyClaim claim, MessageBodyDeleteOptions? options = null,
        CancellationToken cancellationToken = default) {
      if (claim.StorageKey == ThrowOnKey) {
        throw new InvalidOperationException("provider outage for this blob");
      }
      Deleted.Add(claim.StorageKey);
      return Task.CompletedTask;
    }
  }

  private static MaintenanceWorker _buildWorker(
      SweepCoordinator coordinator, SweepStore? store, Action<MessageBodyOffloadOptions>? configure) {
    var services = new ServiceCollection();
    services.AddSingleton<IWorkCoordinator>(coordinator);
    if (store is not null) {
      services.AddKeyedSingleton<IMessageBodyStore>("blob", (_, _) => store);
    }
    services.AddOptions<MessageBodyOffloadOptions>().Configure(o => {
      o.ProviderName = "blob";
      configure?.Invoke(o);
    });
    var sp = services.BuildServiceProvider();
    var gate = new SchemaReadyGate();
    gate.MarkReady();
    return new MaintenanceWorker(
      sp.GetRequiredService<IServiceScopeFactory>(),
      gate,
      Options.Create(new MaintenanceWorkerOptions { IntervalMinutes = 1 }),
      NullLogger<MaintenanceWorker>.Instance);
  }

  [Test]
  public async Task Sweep_DeletesExpiredBlobs_AndRemovesOnlyTheirRowsAsync() {
    var coordinator = new SweepCoordinator {
      Expired = [new("blob/a", "blob"), new("blob/b", "blob")]
    };
    var store = new SweepStore();

    await _buildWorker(coordinator, store, null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(store.Deleted).Contains("blob/a");
    await Assert.That(store.Deleted).Contains("blob/b");
    await Assert.That(coordinator.Removed).Contains("blob/a");
    await Assert.That(coordinator.Removed).Contains("blob/b")
      .Because("both blobs deleted cleanly, so both ledger rows are done");
  }

  [Test]
  public async Task Sweep_FailedBlobDelete_LeavesItsLedgerRowAsync() {
    var coordinator = new SweepCoordinator {
      Expired = [new("blob/ok", "blob"), new("blob/broken", "blob")]
    };
    var store = new SweepStore { ThrowOnKey = "blob/broken" };

    await _buildWorker(coordinator, store, null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Removed).Contains("blob/ok");
    await Assert.That(coordinator.Removed).DoesNotContain("blob/broken")
      .Because("the row outlives the blob, never the reverse — removing the row for a blob that "
        + "still exists would orphan it with no record, so a failed delete keeps its row and is "
        + "retried next sweep");
  }

  [Test]
  public async Task Sweep_PassiveExpiryNull_DoesNothingAsync() {
    var coordinator = new SweepCoordinator { Expired = [new("blob/x", "blob")] };
    var store = new SweepStore();

    await _buildWorker(coordinator, store, o => o.PassiveExpiry = null)
      .RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.GetExpiredCalls).IsEqualTo(0)
      .Because("null is the off switch: the provider-side lifecycle rule becomes the only cleanup "
        + "and the framework must not second-guess that choice");
    await Assert.That(store.Deleted).IsEmpty();
  }

  [Test]
  public async Task Sweep_ClaimLost_DoesNothingAsync() {
    var coordinator = new SweepCoordinator {
      Expired = [new("blob/x", "blob")],
      ClaimResult = false
    };
    var store = new SweepStore();

    await _buildWorker(coordinator, store, null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.GetExpiredCalls).IsEqualTo(0)
      .Because("a replica that loses the CAS watermark race must not sweep — N replicas issuing N "
        + "delete storms against one container is the thing the election prevents");
  }

  [Test]
  public async Task Sweep_UnregisteredProvider_LeavesThoseRowsAsync() {
    var coordinator = new SweepCoordinator {
      Expired = [new("blob/known", "blob"), new("ghost/key", "ghost")]
    };
    var store = new SweepStore();

    await _buildWorker(coordinator, store, null).RunMaintenanceOnceAsync(CancellationToken.None);

    await Assert.That(coordinator.Removed).Contains("blob/known");
    await Assert.That(coordinator.Removed).DoesNotContain("ghost/key")
      .Because("a claim whose provider has no registered store cannot be verified deleted — its "
        + "row stays as the operator's signal rather than being silently dropped");
  }
}
