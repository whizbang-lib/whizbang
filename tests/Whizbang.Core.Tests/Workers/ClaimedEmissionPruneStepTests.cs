using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// The general expiry prune of the claimed-emission store: a claim is deleted once a day past its expiry,
/// unless the package that owns its key convention registered the prefix to keep it.
/// </summary>
/// <remarks>
/// The docs and migration 060 said expired claims were pruned, and nothing pruned them, so the table grew
/// for good (#999). The saga framework's abandonment claims must never be pruned, or the stranded-saga
/// sweep re-arms an abandoned saga once the claim goes; the retained prefixes are how they are kept.
/// </remarks>
/// <code-under-test>src/Whizbang.Core/Workers/ClaimedEmissionPruneStep.cs</code-under-test>
[Category("Unit")]
[Category("Dispatcher")]
public class ClaimedEmissionPruneStepTests {
  private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

  private static ClaimedEmissionPruneStep _step() =>
    new(NullLogger<ClaimedEmissionPruneStep>.Instance, new FixedClock(_now));

  private static ServiceProvider _services(IClaimedEmissionStore? store, DutyHolderWorker? holder = null, params string[] retained) {
    var services = new ServiceCollection();
    if (store is not null) {
      services.AddSingleton(store);
    }
    if (holder is not null) {
      services.AddSingleton(holder);
    }
    foreach (var prefix in retained) {
      services.AddRetainedClaimKeyPrefix(prefix);
    }
    return services.BuildServiceProvider();
  }

  [Test]
  public async Task Run_PrunesClaimsADayPastTheirExpiry_InOneBoundedBatchAsync() {
    var store = new RecordingStore();
    await using var services = _services(store);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls).Count().IsEqualTo(1)
      .Because("a delete that removes fewer claims than the batch size has drained the table");
    await Assert.That(store.Calls[0].ExpiredBefore).IsEqualTo(_now - TimeSpan.FromDays(1));
    await Assert.That(store.Calls[0].MaxClaims).IsEqualTo(ClaimedEmissionPruneStep.BATCH_SIZE);
    await Assert.That(store.Calls[0].Retained).IsEmpty();
  }

  [Test]
  public async Task Run_KeepsEveryRegisteredPrefix_OnceEachAsync() {
    var store = new RecordingStore();
    await using var services = _services(store, holder: null, "saga-abandoned:", "owner-b:", "saga-abandoned:");

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls[0].Retained).IsEquivalentTo(["saga-abandoned:", "owner-b:"]);
  }

  [Test]
  public async Task Run_AFullBatch_DeletesAgain_UpToTheRunsLimitAsync() {
    var store = new RecordingStore { Deletes = ClaimedEmissionPruneStep.BATCH_SIZE };
    await using var services = _services(store);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls).Count().IsEqualTo(ClaimedEmissionPruneStep.MAX_BATCHES_PER_RUN)
      .Because("a full batch may have left more behind, but one run is bounded so a long backlog drains over several cycles");
  }

  [Test]
  public async Task Run_NothingExpired_DeletesOnceAndStopsAsync() {
    var store = new RecordingStore { Deletes = 0 };
    await using var services = _services(store);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls).Count().IsEqualTo(1);
  }

  [Test]
  public async Task Run_NoClaimStore_DoesNothingAsync() {
    await using var services = _services(store: null);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(_step().Name).IsEqualTo("claimed-emission-prune");
  }

  [Test]
  public async Task Run_DefaultClock_PrunesAgainstTheSystemTimeAsync() {
    var store = new RecordingStore();
    await using var services = _services(store);
    var before = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);

    await new ClaimedEmissionPruneStep(NullLogger<ClaimedEmissionPruneStep>.Instance).RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls[0].ExpiredBefore).IsGreaterThanOrEqualTo(before);
  }

  /// <summary>With the maintainer duty assigned, only its holder prunes.</summary>
  /// <param name="holds">Whether this instance holds the maintainer duty.</param>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task Run_WithTheMaintainerDutyAssigned_PrunesOnlyOnItsHolderAsync(bool holds) {
    var store = new RecordingStore();
    var holder = _holder(grants: holds);
    await holder.RunOnceAsync(CancellationToken.None);
    await using var services = _services(store, holder);

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(holder.Holds(StartupDuties.MAINTAINER)).IsEqualTo(holds);
    await Assert.That(store.Calls.Count > 0).IsEqualTo(holds);
  }

  [Test]
  public async Task Run_HolderLoopWithoutTheMaintainerDuty_PrunesOnEveryInstanceAsync() {
    var store = new RecordingStore();
    await using var services = _services(store, _holder(grants: false, role: StartupDuties.MIGRATOR));

    await _step().RunAsync(services, CancellationToken.None);

    await Assert.That(store.Calls).IsNotEmpty();
  }

  [Test]
  public async Task Run_NullServices_ThrowsAsync() {
    await Assert.That(() => _step().RunAsync(null!, CancellationToken.None)).ThrowsExactly<ArgumentNullException>();
  }

  [Test]
  public async Task AddRetainedClaimKeyPrefix_RejectsABlankPrefixAsync() {
    await Assert.That(() => new ServiceCollection().AddRetainedClaimKeyPrefix(" ")).ThrowsExactly<ArgumentException>();
    await Assert.That(() => ((IServiceCollection)null!).AddRetainedClaimKeyPrefix("p:")).ThrowsExactly<ArgumentNullException>();
  }

  private static DutyHolderWorker _holder(bool grants, string role = StartupDuties.MAINTAINER) =>
    new(new Elector(grants), new NoOwedWork(), [new Handler(role)],
      Options.Create(new RoleAssignmentOptions { Roles = { StartupDuties.MIGRATOR } }),
      NullLogger<DutyHolderWorker>.Instance, notify: null);

  private sealed record PruneCall(DateTimeOffset ExpiredBefore, IReadOnlyCollection<string> Retained, int MaxClaims);

  private sealed class RecordingStore : IClaimedEmissionStore {
    public List<PruneCall> Calls { get; } = [];
    public int Deletes { get; init; } = 3;
    public Task<bool> TryClaimAsync(string claimKey, Guid claimedByEventId, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<int> PruneExpiredAsync(DateTimeOffset expiredBefore, IReadOnlyCollection<string> retainedKeyPrefixes, int maxClaims, CancellationToken cancellationToken) {
      Calls.Add(new PruneCall(expiredBefore, retainedKeyPrefixes, maxClaims));
      return Task.FromResult(Deletes);
    }
  }

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider {
    public override DateTimeOffset GetUtcNow() => now;
  }

  private sealed class Grant : IDutyGrant {
    public string Duty => StartupDuties.MAINTAINER;
    public DateTimeOffset AcquiredAt => DateTimeOffset.UnixEpoch;
    public Task<bool> VerifyStillHeldAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
  }

  private sealed class Elector(bool grants) : IDutyElector {
    public Task<DutyAttempt> TryAcquireAsync(string duty, CancellationToken cancellationToken) =>
      Task.FromResult(grants ? DutyAttempt.Granted(new Grant()) : DutyAttempt.Lost(DutyRefusal.Contended, "held elsewhere"));
  }

  private sealed class NoOwedWork : IPendingDutyWorkStore {
    public Task OweAsync(string role, string workKey, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<PendingDutyWork>> ListOwedAsync(string role, CancellationToken cancellationToken) =>
      Task.FromResult<IReadOnlyList<PendingDutyWork>>([]);
    public Task<DutyWorkCompletion> CompleteAsync(PendingDutyWork work, IDutyGrant grant, CancellationToken cancellationToken) =>
      Task.FromResult(DutyWorkCompletion.Completed);
    public Task<bool> RecordFailureAsync(PendingDutyWork work, IDutyGrant grant, string failure, CancellationToken cancellationToken) =>
      Task.FromResult(true);
  }

  private sealed class Handler(string role) : IDutyWorkHandler {
    public string Role => role;
    public string WorkKey => "ClaimedEmissionPruneTest";
    public ValueTask<DutyWorkResult> RunAsync(IDutyGrant grant, CancellationToken cancellationToken) =>
      ValueTask.FromResult(DutyWorkResult.Done());
  }
}
