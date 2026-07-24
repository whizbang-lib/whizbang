#pragma warning disable CA1707

using System.Data.Common;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Unit-testable surface of the pinned worker connection pool: the no-op
/// implementation, the AsyncLocal context accessor, and the per-worker
/// eligibility registry. The PostgreSQL-bound real pool implementation
/// lives in <c>Whizbang.Data.Postgres</c> and is covered by integration
/// tests against a real container.
/// </summary>
/// <docs>fundamentals/workers/pinned-connection-pool</docs>
public class PinnedConnectionPoolPrimitivesTests {

  // ============================================================
  // NoOpPinnedConnectionPool
  // ============================================================

  [Test]
  public async Task NoOp_TryPin_ReturnsBorrowWithNullConnectionAsync() {
    var pool = NoOpPinnedConnectionPool.Instance;

    await using var borrow = await pool.TryPinForAsync(typeof(_fakeWorker), CancellationToken.None);

    await Assert.That(borrow).IsNotNull()
      .Because("The contract says TryPinForAsync ALWAYS returns a non-null borrow so callers don't need null guards around the borrow itself.");
    await Assert.That(borrow.Connection).IsNull()
      .Because("No-op borrow signals 'pool not active' via a null Connection so callers can branch on the connection rather than wrap the borrow site in conditionals.");
  }

  [Test]
  public async Task NoOp_TryPin_AnyWorkerType_ReturnsNoOpBorrowAsync() {
    var pool = NoOpPinnedConnectionPool.Instance;

    await using var b1 = await pool.TryPinForAsync(typeof(_fakeWorker), CancellationToken.None);
    await using var b2 = await pool.TryPinForAsync(typeof(_anotherFakeWorker), CancellationToken.None);

    await Assert.That(b1.Connection).IsNull();
    await Assert.That(b2.Connection).IsNull()
      .Because("NoOp pool MUST ignore the worker type — eligibility is moot when the pool is disabled entirely.");
  }

  [Test]
  public async Task NoOp_Instance_IsProcessWideSingletonAsync() {
    var a = NoOpPinnedConnectionPool.Instance;
    var b = NoOpPinnedConnectionPool.Instance;

    await Assert.That(a).IsSameReferenceAs(b)
      .Because("The no-op is a hot-path sentinel; a per-resolution allocation would defeat its purpose.");
  }

  [Test]
  public async Task NoOp_TryPin_NullWorkerType_ThrowsArgumentNullExceptionAsync() {
    var pool = NoOpPinnedConnectionPool.Instance;

    await Assert.That(async () => await pool.TryPinForAsync(workerType: null!, CancellationToken.None))
      .Throws<ArgumentNullException>();
  }

  [Test]
  public async Task NoOp_TryPin_CancelledToken_ThrowsOperationCanceledAsync() {
    var pool = NoOpPinnedConnectionPool.Instance;
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.That(async () => await pool.TryPinForAsync(typeof(_fakeWorker), cts.Token))
      .Throws<OperationCanceledException>()
      .Because("Even on the no-op path, an already-cancelled CT MUST be honoured — workers rely on CT propagation for graceful shutdown.");
  }

  // ============================================================
  // PinnedConnectionContext (AsyncLocal accessor)
  // ============================================================

  [Test]
  public async Task Context_Current_IsNullByDefaultAsync() {
    await Assert.That(PinnedConnectionContext.Current).IsNull()
      .Because("In a fresh async flow, no pinned connection is in scope.");
  }

  [Test]
  public async Task Context_Push_SetsCurrentAndResetScopeRestoresAsync() {
    var fake = new _stubDbConnection("first");

    using (var scope = PinnedConnectionContext.Push(fake)) {
      await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(fake)
        .Because("Push MUST set Current to the supplied connection inside the using scope.");
    }

    await Assert.That(PinnedConnectionContext.Current).IsNull()
      .Because("Disposing the ResetScope MUST restore the prior value (null in this test).");
  }

  [Test]
  public async Task Context_PushNestedScopes_RestoresPreviousLayerAsync() {
    var outer = new _stubDbConnection("outer");
    var inner = new _stubDbConnection("inner");

    using (PinnedConnectionContext.Push(outer)) {
      await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(outer);

      using (PinnedConnectionContext.Push(inner)) {
        await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(inner);
      }

      await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(outer)
        .Because("Inner scope dispose MUST restore the outer pin so a worker that dispatches inner work doesn't lose its own pin.");
    }
    await Assert.That(PinnedConnectionContext.Current).IsNull();
  }

  [Test]
  public async Task Context_PushNull_ExplicitlyClearsAsync() {
    var initial = new _stubDbConnection("initial");

    using (PinnedConnectionContext.Push(initial)) {
      await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(initial);

      using (PinnedConnectionContext.Push(null)) {
        await Assert.That(PinnedConnectionContext.Current).IsNull()
          .Because("Push(null) is the documented way to explicitly shadow an outer pin — e.g. when the no-op borrow returns and the worker shouldn't see the outer's pin.");
      }

      await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(initial);
    }
  }

  [Test]
  public async Task Context_Current_PropagatesAcrossAwaitWithinSameFlowAsync() {
    var fake = new _stubDbConnection("flowing");
    using var scope = PinnedConnectionContext.Push(fake);

    await Task.Yield();   // forces an async hop

    await Assert.That(PinnedConnectionContext.Current).IsSameReferenceAs(fake)
      .Because("AsyncLocal MUST flow across await boundaries within the same logical control-flow — that's why we picked AsyncLocal over [ThreadStatic].");
  }

  // ============================================================
  // PinnedWorkerRegistry (eligibility)
  // ============================================================

  [Test]
  public async Task Registry_Tier1Worker_IsEligibleAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();

    var eligible = registry.IsEligible(typeof(ClaimWorker), opts);

    await Assert.That(eligible).IsTrue()
      .Because("ClaimWorker is the headline tier-1 worker — must always pin when the feature is on.");
  }

  [Test]
  public async Task Registry_Tier2Worker_DefaultsEligibleWhenIncludeFlushAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();   // IncludeFlushWorkers defaults true

    await Assert.That(registry.IsEligible(typeof(InboxHandlerWorker), opts)).IsTrue();
    await Assert.That(registry.IsEligible(typeof(FailureFlushWorker), opts)).IsTrue();
  }

  [Test]
  public async Task Registry_Tier2Worker_OptedOutWhenIncludeFlushFalseAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();
    opts.IncludeFlushWorkers = false;

    await Assert.That(registry.IsEligible(typeof(InboxHandlerWorker), opts)).IsFalse()
      .Because("Setting IncludeFlushWorkers=false MUST exclude every tier-2 flush worker from the pool — the option's whole purpose.");
  }

  [Test]
  public async Task Registry_ExcludeWorkers_OverridesTierAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();
    opts.ExcludeWorkers.Add("ClaimWorker");

    var eligible = registry.IsEligible(typeof(ClaimWorker), opts);

    await Assert.That(eligible).IsFalse()
      .Because("ExcludeWorkers is the surgical escape hatch — naming a tier-1 worker MUST remove it even though the default would include it.");
  }

  [Test]
  public async Task Registry_UnknownWorker_NotEligibleWithoutOptInAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();

    var eligible = registry.IsEligible(typeof(_anonymousCustomWorker), opts);

    await Assert.That(eligible).IsFalse()
      .Because("Whizbang doesn't pin third-party workers by default — they must opt in via AddPinnedWorker<T>.");
  }

  [Test]
  public async Task Registry_AddOptIn_MakesCustomWorkerEligibleAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();
    registry.AddOptIn(typeof(_anonymousCustomWorker));

    await Assert.That(registry.IsEligible(typeof(_anonymousCustomWorker), opts)).IsTrue()
      .Because("AddOptIn IS the consumer-side mechanism for opting custom workers in; if it doesn't take, the API is broken.");
  }

  [Test]
  public async Task Registry_AddOptInTwice_IsIdempotentAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();
    registry.AddOptIn(typeof(_anonymousCustomWorker));
    registry.AddOptIn(typeof(_anonymousCustomWorker));

    await Assert.That(registry.IsEligible(typeof(_anonymousCustomWorker), opts)).IsTrue();
  }

  [Test]
  public async Task Registry_AddOptIn_NullWorkerType_ThrowsAsync() {
    var registry = new PinnedWorkerRegistry();

    Action act = () => registry.AddOptIn(workerType: null!);

    await Assert.That(act).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Registry_IsEligible_NullArgs_ThrowAsync() {
    var registry = new PinnedWorkerRegistry();
    var opts = _newOptions();

    await Assert.That(() => registry.IsEligible(workerType: null!, opts)).Throws<ArgumentNullException>();
    await Assert.That(() => registry.IsEligible(typeof(ClaimWorker), options: null!)).Throws<ArgumentNullException>();
  }

  // ============================================================
  // Helpers
  // ============================================================

  private static WhizbangPinnedPoolOptions _newOptions() => new() {
    Enabled = true,
    ConnectionString = "Host=sentinel;Database=sentinel;Username=sentinel;Password=sentinel",
    Size = 1,
  };

  private sealed class _fakeWorker { }
  private sealed class _anotherFakeWorker { }
  private sealed class _anonymousCustomWorker { }

  // Stand-ins named exactly the same as the Whizbang internal workers so the
  // tier check (which uses CLR short names) matches by string.
  private sealed class ClaimWorker { }
  private sealed class InboxHandlerWorker { }
  private sealed class FailureFlushWorker { }

  /// <summary>
  /// Minimal <see cref="DbConnection"/> subclass used by AsyncLocal tests. Only
  /// constructed — never opened — so the abstract members can throw.
  /// </summary>
  private sealed class _stubDbConnection : DbConnection {
    public _stubDbConnection(string id) {
      _id = id;
    }
    private readonly string _id;
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
    public override string Database => _id;
    public override string DataSource => _id;
    public override string ServerVersion => "0";
    public override System.Data.ConnectionState State => System.Data.ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
    public override void Close() { }
    public override void Open() => throw new NotSupportedException();
    protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override System.Data.Common.DbCommand CreateDbCommand() => throw new NotSupportedException();
  }
}
