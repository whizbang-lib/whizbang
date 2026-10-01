using Microsoft.Extensions.Logging.Abstractions;
using Rocks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;

[assembly: Rock(typeof(IReleasesDutiesOnShutdown), BuildType.Create)]

namespace Whizbang.Core.Tests.Startup;

/// <summary>
/// Requirement 6 of #966, the host half: on a graceful stop every elector that holds roles gives
/// them up, so a rolling deploy hands off at once instead of waiting for leases to lapse. One
/// elector failing to release must not keep the others from releasing.
/// </summary>
/// <code-under-test>src/Whizbang.Core/Startup/DutyShutdownReleaseService.cs</code-under-test>
[Category("Startup")]
public class DutyShutdownReleaseServiceTests {

  [Test]
  public async Task StartAsync_ReleasesNothingAsync() {
    var releaser = new IReleasesDutiesOnShutdownCreateExpectations();
    var service = new DutyShutdownReleaseService([releaser.Instance()], NullLogger<DutyShutdownReleaseService>.Instance);

    await service.StartAsync(CancellationToken.None);

    releaser.Verify();
  }

  [Test]
  public async Task StopAsync_ReleasesEveryElectorsDuties_EvenWhenOneFailsAsync() {
    using var stopping = new CancellationTokenSource();
    var failing = new IReleasesDutiesOnShutdownCreateExpectations();
    failing.Setups.ReleaseAllAsync(Arg.Any<CancellationToken>())
      .ReturnValue(Task.FromException(new InvalidOperationException("database unreachable")));
    var healthy = new IReleasesDutiesOnShutdownCreateExpectations();
    healthy.Setups.ReleaseAllAsync(Arg.Is(stopping.Token)).ReturnValue(Task.CompletedTask);
    var service = new DutyShutdownReleaseService(
      [failing.Instance(), healthy.Instance()], NullLogger<DutyShutdownReleaseService>.Instance);

    await Assert.That(async () => await service.StopAsync(stopping.Token)).ThrowsNothing()
      .Because("a release that cannot reach the database leaves a lease to lapse, which is the crash path; it is not a shutdown error");

    failing.Verify();
    healthy.Verify();
  }

  [Test]
  public async Task StopAsync_PropagatesCancellationOfTheStopItselfAsync() {
    using var stopping = new CancellationTokenSource();
    await stopping.CancelAsync();
    var releaser = new IReleasesDutiesOnShutdownCreateExpectations();
    releaser.Setups.ReleaseAllAsync(Arg.Any<CancellationToken>())
      .ReturnValue(Task.FromCanceled(stopping.Token));
    var service = new DutyShutdownReleaseService([releaser.Instance()], NullLogger<DutyShutdownReleaseService>.Instance);

    await Assert.That(async () => await service.StopAsync(stopping.Token)).Throws<OperationCanceledException>();
    releaser.Verify();
  }

  [Test]
  public async Task Constructor_RefusesNullArgumentsAsync() {
    await Assert.That(() => new DutyShutdownReleaseService(null!, NullLogger<DutyShutdownReleaseService>.Instance))
      .Throws<ArgumentNullException>();
    await Assert.That(() => new DutyShutdownReleaseService([], null!)).Throws<ArgumentNullException>();
  }
}
