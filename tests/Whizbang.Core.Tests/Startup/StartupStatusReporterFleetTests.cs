using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// How the status surface reports a fleet it cannot read.
/// <para>
/// The interface tells implementations to throw on failure, because the surface turns that into
/// an unavailable section rather than an error response — during an incident, "no other
/// instances" and "cannot see the other instances" mean opposite things, and an empty list would
/// say the first when the truth is the second.
/// </para>
/// <para>
/// Cancellation is the exception. A status read interrupted by shutdown has nothing to state: the
/// caller is going away, and reporting the fleet as unavailable would put a reason in a response
/// nobody receives — or worse, in one that is cached and read later as evidence of an outage.
/// </para>
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/StartupStatusReport.cs</code-under-test>
public class StartupStatusReporterFleetTests {

  private sealed class ThrowingFleetSource(Exception toThrow) : IStartupFleetStatusSource {
    public Task<IReadOnlyList<FleetInstanceStatus>> GetFleetAsync(CancellationToken cancellationToken)
      => Task.FromException<IReadOnlyList<FleetInstanceStatus>>(toThrow);
  }

  [Test]
  public async Task AFleetReadThatFails_IsReportedAsUnavailableRatherThanEmptyAsync() {
    var report = await StartupStatusReporter.BuildAsync(
      state: null, readySignal: null, instanceProvider: null,
      fleetSource: new ThrowingFleetSource(new InvalidOperationException("wh_service_instances unreachable")),
      includeReasons: true,
      cancellationToken: CancellationToken.None);

    await Assert.That(report.Fleet.Available).IsFalse()
      .Because("an empty list would say there are no other instances, which is the opposite of "
             + "what a failed read means, and the difference matters most during an incident");
    await Assert.That(report.Fleet.Reason).IsNotNull()
      .Because("the stated condition is the whole point of failing this way instead of throwing");
  }

  [Test]
  public async Task AFleetReadCancelledByShutdown_PropagatesRatherThanStatingAnOutageAsync() {
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();

    await Assert.That(async () => await StartupStatusReporter.BuildAsync(
        state: null, readySignal: null, instanceProvider: null,
        fleetSource: new ThrowingFleetSource(new OperationCanceledException()),
        includeReasons: true,
        cancellationToken: stopping.Token))
      .Throws<OperationCanceledException>()
      .Because("the caller is going away, so an unavailable-fleet reason would land in a response "
             + "nobody reads — or in a cached one that is later taken for evidence of an outage");
  }

  [Test]
  public async Task AFleetReadCancelledWithNoShutdown_IsStillJustAFailedReadAsync() {
    // The catch is filtered on the caller's token: a source that throws this type for its own
    // reasons — an internal timeout, a linked token of its own — has not been asked to stop, and
    // the surface owes its caller a stated condition rather than an exception.
    var report = await StartupStatusReporter.BuildAsync(
      state: null, readySignal: null, instanceProvider: null,
      fleetSource: new ThrowingFleetSource(new OperationCanceledException()),
      includeReasons: true,
      cancellationToken: CancellationToken.None);

    await Assert.That(report.Fleet.Available).IsFalse()
      .Because("no shutdown means the caller is still waiting for an answer, and unavailable is "
             + "the honest one");
  }
}
