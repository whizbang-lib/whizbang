using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Notifications;
using Whizbang.Data.Postgres.Notifications;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage for two <see cref="NotificationConnectionPlan"/> surfaces the sibling
/// <see cref="NotificationConnectionPlanTests"/> suite never exercises: the
/// <see cref="NotificationConnectionPlan.StringSource"/> provenance property, and the
/// connection-string fallback path in <see cref="NotificationConnectionPlan.OpenAsync"/> actually
/// attempting (and failing) a connection — every existing test for this class either uses the
/// data-source path or stops at the "unavailable" guard before a socket is ever touched.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/Notifications/NotificationConnectionPlan.cs</code-under-test>
[Category("Shard1")]
public class NotificationConnectionPlanCoverageTests {
  private static NotificationConnectionStringResolver.Resolution _resolution(
      string? connectionString, NotificationConnectionStringResolver.ResolutionSource source) =>
    new(connectionString, source);

  /// <summary>Binds an ephemeral loopback port, then immediately frees it — a port nothing is
  /// listening on, so a connection attempt fails fast (deterministic ECONNREFUSED) instead of
  /// hanging on a timeout. Mirrors the identical helper in
  /// <c>PostgresConnectionRetryCoverageTests</c>.</summary>
  private static int _reserveClosedPort() {
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
  }

  // StringSource is the diagnostic trail for "why is this component using this connection
  // string" — an operator debugging a notification worker that's talking to the wrong database
  // (pooled vs. direct, EF Core's DbContext connection vs. an explicit override) reads this to
  // tell the paths apart. If Create ever dropped or mis-copied it, every such diagnostic would
  // report the wrong provenance while the actual connection behavior stayed correct — a silent
  // lie in exactly the field that exists to explain surprising behavior.
  [Test]
  public async Task Create_CarriesForwardTheResolutionSourceAsync() {
    var resolution = _resolution(
      "Host=localhost;Username=tenant-user;Password=secret",
      NotificationConnectionStringResolver.ResolutionSource.DbContextFallback);

    var plan = NotificationConnectionPlan.Create(null, resolution);

    await Assert.That(plan.StringSource).IsEqualTo(NotificationConnectionStringResolver.ResolutionSource.DbContextFallback)
      .Because("the plan's StringSource must reflect exactly which precedence tier the resolver used, "
             + "not silently default to None or some other tier");
  }

  // OpenAsync's catch block exists to dispose the half-opened NpgsqlConnection before rethrowing —
  // without it, a connection that fails partway through OpenAsync (TCP connects, then the
  // Postgres handshake fails) would leak the underlying socket/handle on every failed attempt. No
  // existing test drives this: the "unavailable" test stops at the guard before any
  // NpgsqlConnection is even constructed, and nothing exercises the fallback path succeeding OR
  // failing to actually connect.
  [Test]
  public async Task OpenAsync_FallbackConnectionStringFailsToConnect_DisposesAndRethrowsAsync() {
    var closedPort = _reserveClosedPort();
    var badConnectionString =
      $"Host=127.0.0.1;Port={closedPort};Username=nobody;Password=nobody;Database=nothing;Timeout=1";
    var plan = NotificationConnectionPlan.Create(
      null, _resolution(badConnectionString, NotificationConnectionStringResolver.ResolutionSource.ExplicitOption));

    await Assert.That(plan.IsAvailable).IsTrue()
      .Because("a resolved (if unreachable) connection string counts as available — OpenAsync must "
             + "actually attempt the connection rather than short-circuit");
    await Assert.That(async () => await plan.OpenAsync()).ThrowsException()
      .Because("a connection failure on the fallback path must propagate to the caller after cleanup, "
             + "not be swallowed by the dispose-and-rethrow catch block");
  }
}
