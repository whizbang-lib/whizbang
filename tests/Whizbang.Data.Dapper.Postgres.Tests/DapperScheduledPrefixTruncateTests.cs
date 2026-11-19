// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Archival;
using Whizbang.Core.Lifecycle;
using Whizbang.Core.Messaging;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// The Dapper registration wires the scheduled prefix truncate, as the EF Core one does: a fired
/// <see cref="ScheduledPrefixTruncate"/> occurrence reaches a receptor that truncates the stream (#2043).
/// </summary>
/// <remarks>
/// The receptor and its registrar lived in the EF Core driver, so on a Dapper host a scheduled prefix truncate fired and
/// nothing handled it. Both drivers now call the shared <c>AddScheduledPrefixTruncate</c>. Design choice documented at
/// fundamentals/events/event-streams#prefix-truncate-on-every-data-layer.
/// </remarks>
/// <docs>fundamentals/events/event-streams#prefix-truncate-on-every-data-layer</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Postgres/ScheduledPrefixTruncateReceptorRegistrar.cs</code-under-test>
[Category("Integration")]
public class DapperScheduledPrefixTruncateTests : PostgresTestBase {
  private static readonly KeyValuePair<string, string>[] _noPerspectives = [];

  /// <summary>Keeps the receptor a registrar registers, so the test can hand it an occurrence.</summary>
  private sealed class CapturingRegistry : IReceptorRegistry {
    public List<(IReceptor<ScheduledPrefixTruncate> Receptor, LifecycleStage Stage)> Registered { get; } = [];

    public void Register<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage {
      if (receptor is IReceptor<ScheduledPrefixTruncate> truncate) {
        Registered.Add((truncate, stage));
      }
    }

    public void Register<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage { }
    public IReadOnlyList<ReceptorInfo> GetReceptorsFor(Type messageType, LifecycleStage stage) => [];
    public bool Unregister<TMessage>(IReceptor<TMessage> receptor, LifecycleStage stage) where TMessage : IMessage => false;
    public bool Unregister<TMessage, TResponse>(IReceptor<TMessage, TResponse> receptor, LifecycleStage stage) where TMessage : IMessage => false;
  }

  [Test]
  public async Task AddWhizbangPostgres_RegistersTheScheduledPrefixTruncate_ThroughBothOverloadsAsync() {
    var perspectiveEntries = new ServiceCollection();
    perspectiveEntries.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, _noPerspectives);
    var schemaSql = new ServiceCollection();
    schemaSql.AddWhizbangPostgres(ConnectionString, new JsonSerializerOptions(), initializeSchema: false, perspectiveSchemaSql: null, configureOptions: null);

    await Assert.That(_registrarCount(perspectiveEntries)).IsEqualTo(1);
    await Assert.That(_registrarCount(schemaSql)).IsEqualTo(1)
      .Because("both registration overloads wire the scheduled prefix truncate");
  }

  [Test]
  public async Task AFiredOccurrence_OnADapperHost_TruncatesTheStreamAsync() {
    var stream = Guid.NewGuid();
    await using (var connection = new NpgsqlConnection(ConnectionString)) {
      foreach (var type in new[] { "Whizbang.Tests.LedgerEntry", "Whizbang.Tests.LedgerEntry", "Whizbang.Tests.MonthClosed" }) {
        await connection.ExecuteAsync("SELECT commit_handler_result(@Req::jsonb)",
          new { Req = DapperStreamPrefixTruncateTests.Commit(Guid.NewGuid(), stream, type) });
      }
    }
    var registry = new CapturingRegistry();
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    services.AddWhizbangPostgres(ConnectionString, Whizbang.Core.Serialization.JsonContextRegistry.CreateCombinedOptions(),
      initializeSchema: false, _noPerspectives);
    services.AddSingleton<IReceptorRegistry>(registry);
    services.AddSingleton<IStreamPrefixTruncator>(sp => new StreamPrefixTruncator(
      sp.GetRequiredService<IWorkCoordinator>(), NullLogger<StreamPrefixTruncator>.Instance, NoOpDestructionHook.Instance,
      sp.GetRequiredService<IServiceScopeFactory>(), archiveStore: null));
    await using var provider = services.BuildServiceProvider();

    var registrar = provider.GetServices<IHostedService>().OfType<ScheduledPrefixTruncateReceptorRegistrar>().Single();
    await registrar.StartAsync(CancellationToken.None);
    await Assert.That(registry.Registered.Select(r => r.Stage)).IsEquivalentTo(
      [LifecycleStage.LocalImmediateInline, LifecycleStage.PreOutboxInline, LifecycleStage.PostInboxInline]);

    await registry.Registered[0].Receptor.HandleAsync(new ScheduledPrefixTruncate(stream, ThroughVersion: 2, Archive: false));

    await using var check = new NpgsqlConnection(ConnectionString);
    await Assert.That(await check.ExecuteScalarAsync<long>("SELECT count(*) FROM wh_event_store WHERE stream_id = @stream", new { stream }))
      .IsEqualTo(1L)
      .Because("the scheduled occurrence drives the prefix truncate on this driver too; only the carry-forward event remains");
  }

  private static int _registrarCount(IServiceCollection services) =>
    services.Count(d => d.ServiceType == typeof(IHostedService)
      && d.ImplementationFactory is Func<IServiceProvider, ScheduledPrefixTruncateReceptorRegistrar>);
}
