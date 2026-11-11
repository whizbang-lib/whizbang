// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Commands.System;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper registration handles <see cref="RebuildPerspectiveCommand"/>, as the EF Core one does (#1959): the
/// command reaches a receptor that rebuilds the requested perspectives, and a whole-perspective blue-green rebuild is
/// handed to the background rebuild worker.
/// </summary>
/// <remarks>
/// The receptor, its registrar and the background worker lived in the EF Core driver, so a Dapper host that sent the
/// command rebuilt nothing; it could rebuild only by calling <see cref="IPerspectiveRebuilder"/> itself. Both drivers now
/// call the shared registration. The parity case is
/// <c>StartupParityTests.EachDriver_HandlesTheRebuildCommand_AndRunsItsBackgroundRebuildsAsync</c>.
/// </remarks>
/// <docs>fundamentals/perspectives/rebuild</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/RebuildCommandReceptorRegistrar.cs</code-under-test>
[Category("Integration")]
public class DapperRebuildCommandTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  /// <summary>Keeps the receptor a registrar registers, so the test can hand it a command.</summary>
  private sealed class CapturingRegistry : IReceptorRegistry {
    public List<(IReceptor<RebuildPerspectiveCommand> Receptor, LifecycleStage Stage)> Registered { get; } = [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      if (receptor is IReceptor<RebuildPerspectiveCommand> rebuild) {
        Registered.Add((rebuild, stage));
      }
    }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  /// <summary>One registered perspective, so the command's fan-out has something to rebuild.</summary>
  private sealed class OnePerspective : IPerspectiveRunnerRegistry {
    public const string NAME = "Whizbang.Tests.RebuildProbe";

    public IPerspectiveRunner? GetRunner(string perspectiveName, IServiceProvider serviceProvider) => null;
    public IReadOnlyList<PerspectiveRegistrationInfo> GetRegisteredPerspectives() =>
      [new PerspectiveRegistrationInfo(NAME, NAME, "RebuildProbeModel", [])];
    public IReadOnlyList<Type> GetEventTypes() => [];
    public IReadOnlySet<LifecycleStage> LifecycleStagesWithReceptors { get; } = new HashSet<LifecycleStage>();
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheRebuildCommandHandling_ThroughBothOverloadsAsync() {
    var perspectiveEntries = new ServiceCollection();
    perspectiveEntries.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives);
    var schemaSql = new ServiceCollection();
    schemaSql.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);

    foreach (var services in new[] { perspectiveEntries, schemaSql }) {
      await Assert.That(_hostedCount<RebuildCommandReceptorRegistrar>(services)).IsEqualTo(1)
        .Because("both registration overloads handle the rebuild command");
      await Assert.That(_hostedCount<RebuildBackgroundWorker>(services)).IsEqualTo(1);
    }
  }

  [Test]
  public async Task ABlueGreenRebuildCommand_OnADapperHost_IsHandedToTheBackgroundWorkerAsync() {
    var registry = new CapturingRegistry();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbangPostgres(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      initializeSchema: false, _noPerspectives);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IPerspectiveRunnerRegistry>(new OnePerspective());
    services.AddSingleton<IPerspectiveRebuilder>(new ThrowingRebuilder());
    await using var provider = services.BuildServiceProvider();

    var registrar = provider.GetServices<IHostedService>().OfType<RebuildCommandReceptorRegistrar>().Single();
    await registrar.StartAsync(CancellationToken.None);
    await Assert.That(registry.Registered.Select(r => r.Stage)).IsEquivalentTo(
      [LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline]);

    await registry.Registered[0].Receptor.HandleAsync(new RebuildPerspectiveCommand(
      [OnePerspective.NAME], RebuildMode.BlueGreen));

    await Assert.That(provider.GetRequiredService<RebuildBackgroundQueue>().Pending).IsEqualTo(1)
      .Because("the command reaches the receptor on this driver too, and a whole-perspective blue-green rebuild is queued "
             + "to the background worker rather than run on the handler");
  }

  private static int _hostedCount<T>(IServiceCollection services) where T : IHostedService =>
    services.Count(d => d.ServiceType == typeof(IHostedService)
      && (d.ImplementationType == typeof(T) || d.ImplementationFactory is Func<IServiceProvider, T>));

  /// <summary>A rebuilder the handler must not call itself: a queued blue-green rebuild runs on the worker.</summary>
  private sealed class ThrowingRebuilder : IPerspectiveRebuilder {
    private static InvalidOperationException _called() => new("the handler rebuilt in place instead of queueing");
    public Task<RebuildResult> RebuildBlueGreenAsync(string perspectiveName, CancellationToken ct = default) => throw _called();
    public Task<RebuildResult> RebuildInPlaceAsync(string perspectiveName, CancellationToken ct = default) => throw _called();
    public Task<RebuildResult> RebuildStreamsAsync(string perspectiveName, IEnumerable<Guid> streamIds, CancellationToken ct = default) => throw _called();
    public Task<RebuildStatus?> GetRebuildStatusAsync(string perspectiveName, CancellationToken ct = default) => throw _called();
  }
}
