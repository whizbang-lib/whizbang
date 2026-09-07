using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Tests.Workers;

/// <summary>
/// Tail-of-round coverage for <see cref="WhizbangShutdownService.StartAsync"/>: it must be a true
/// no-op, since all deregistration work belongs on the stop path (registration order relies on
/// this service being stopped LAST, after every worker has already released its own leases).
/// </summary>
/// <code-under-test>src/Whizbang.Core/Workers/WhizbangShutdownService.cs</code-under-test>
public class WhizbangShutdownServiceCoverageTests {

  private sealed class _StubInstanceProvider : IServiceInstanceProvider {
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string ServiceName => "test-service";
    public string HostName => "test-host";
    public int ProcessId => 1234;
    public ServiceInstanceInfo ToInfo() => new() {
      ServiceName = ServiceName,
      InstanceId = InstanceId,
      HostName = HostName,
      ProcessId = ProcessId
    };
  }

  /// <summary>
  /// A shutdown service that did any work on start — however small — would run it again on every
  /// host start, ahead of the workers whose leases it is meant to release only once, at the end.
  /// </summary>
  [Test]
  public async Task StartAsync_IsANoOpThatCompletesSynchronouslyAsync() {
    var service = new WhizbangShutdownService(
      new ServiceCollection().BuildServiceProvider(),
      new _StubInstanceProvider(),
      new WhizbangCoreOptions(),
      NullLogger<WhizbangShutdownService>.Instance);

    var task = service.StartAsync(CancellationToken.None);

    await Assert.That(task.IsCompletedSuccessfully).IsTrue()
      .Because("StartAsync must do nothing observable — every deregistration side effect belongs "
             + "exclusively on the stop path");
  }
}
