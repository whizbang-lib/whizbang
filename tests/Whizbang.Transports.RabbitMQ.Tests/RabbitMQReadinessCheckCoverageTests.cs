using RabbitMQ.Client;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

#pragma warning disable CA1707 // Identifiers should not contain underscores (test method names use underscores by convention)

namespace Whizbang.Transports.RabbitMQ.Tests;

/// <summary>
/// Coverage gap left by <c>RabbitMQReadinessCheckTests</c>: the recovery branch, only reachable
/// when a connection that was previously reported closed transitions back to open.
/// </summary>
/// <code-under-test>src/Whizbang.Transports.RabbitMQ/RabbitMQReadinessCheck.cs</code-under-test>
public class RabbitMQReadinessCheckCoverageTests {
  /// <summary>
  /// Production risk if this regresses in either direction: a probe that never sees the
  /// "recovered" log after an outage leaves an operator unable to tell when publishing resumed;
  /// and if the once-per-transition flag is not reset on recovery, a SUBSEQUENT genuine closure
  /// goes unlogged too — a real outage hides behind one that already resolved.
  /// </summary>
  [Test]
  public async Task IsReadyAsync_ConnectionRecoversAfterClosing_LogsRecoveryAndResetsTheClosedFlagAsync() {
    var connection = new FakeConnection(() => Task.FromResult<IChannel>(new FakeChannel()), isOpen: false);
    var logger = new CapturingLogger<RabbitMQReadinessCheck>();
    var readinessCheck = new RabbitMQReadinessCheck(connection, logger);

    var closedResult = await readinessCheck.IsReadyAsync();

    connection.IsOpen = true;
    var recoveredResult = await readinessCheck.IsReadyAsync();

    // If the recovery branch did not reset the once-per-transition flag, this second closure
    // would never log — proving the reset, not just that the recovery branch ran once.
    connection.IsOpen = false;
    await readinessCheck.IsReadyAsync();

    await Assert.That(closedResult).IsFalse();
    await Assert.That(recoveredResult).IsTrue();
    await Assert.That(logger.Entries.Count(e => e.Message.Contains("recovered", StringComparison.Ordinal)))
      .IsEqualTo(1)
      .Because("the recovery log must fire exactly once, on the closed-to-open transition");
    await Assert.That(logger.Entries.Count(e => e.Message.Contains("NOT open", StringComparison.Ordinal)))
      .IsEqualTo(2)
      .Because("the closed-connection log must fire again on the second closure — proof the "
             + "once-per-transition flag was reset by the recovery branch rather than left stuck");
  }
}
