// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whizbang.Core.Startup;
using Whizbang.Core.Workers;
using Whizbang.Testing.Workers;

namespace Whizbang.Testing.Tests.Workers;

/// <summary>
/// Locks <see cref="HostedWorkers"/>: a worker hosted with its start point is found as the worker itself, a service
/// hosted with the host is found as it is, and a worker the host does not host is not found.
/// </summary>
/// <remarks>
/// The wrappers are the framework's own (a stage wrapper and a schema-ready wrapper), so a lookup that read only one
/// kind of wrapper, or read the wrapper as the worker, fails here.
/// </remarks>
public class HostedWorkersTests {
  [Test]
  public async Task GetHostedWorkers_LooksThroughEveryStageWrapper_AndKeepsAPlainServiceAsync() {
    var staged = new StagedProbe();
    var atSchemaReady = new SchemaReadyProbe();
    var plain = new PlainProbe();
    await using var provider = _provider(staged, atSchemaReady, plain);

    var hosted = provider.GetHostedWorkers().ToList();

    await Assert.That(hosted).Count().IsEqualTo(3);
    await Assert.That(hosted[0]).IsSameReferenceAs(staged);
    await Assert.That(hosted[1]).IsSameReferenceAs(atSchemaReady);
    await Assert.That(hosted[2]).IsSameReferenceAs(plain);
  }

  [Test]
  public async Task GetHostedWorker_FindsAStagedWorker_AndAPlainServiceByTypeAsync() {
    var staged = new StagedProbe();
    var plain = new PlainProbe();
    await using var provider = _provider(staged, new SchemaReadyProbe(), plain);

    await Assert.That(provider.GetHostedWorker<StagedProbe>()).IsSameReferenceAs(staged);
    await Assert.That(provider.GetHostedWorker<PlainProbe>()).IsSameReferenceAs(plain);
  }

  [Test]
  public async Task GetHostedWorker_ForAWorkerTheHostDoesNotHost_IsNullAsync() {
    await using var provider = _provider(new StagedProbe(), new SchemaReadyProbe(), new PlainProbe());

    await Assert.That(provider.GetHostedWorker<UnhostedProbe>()).IsNull();
  }

  private static ServiceProvider _provider(StagedProbe staged, SchemaReadyProbe atSchemaReady, PlainProbe plain) {
    var services = new ServiceCollection();
    services.AddSingleton<IHostedService>(new StartupStageGatedService<StagedProbe>(staged, StartupStage.AcceptingCommands, gate: null));
    services.AddSingleton<IHostedService>(new SchemaReadyGatedService<SchemaReadyProbe>(atSchemaReady, new SchemaReadyGate()));
    services.AddSingleton<IHostedService>(plain);
    return services.BuildServiceProvider();
  }

  private sealed class StagedProbe : IdleProbe;

  private sealed class SchemaReadyProbe : IdleProbe;

  private sealed class PlainProbe : IdleProbe;

  private sealed class UnhostedProbe : IdleProbe;

  private abstract class IdleProbe : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
  }
}
