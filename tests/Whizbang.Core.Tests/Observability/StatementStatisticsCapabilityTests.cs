// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Capabilities;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Data.Dapper.Postgres;
using Whizbang.Data.Postgres;
using Whizbang.Data.Postgres.Observability;

namespace Whizbang.Core.Tests.Observability;

/// <summary>
/// Issue #1754: statement statistics are read only while <c>pg_stat_statements</c> is Ready in the capability
/// registry, instead of reading <c>pg_stat_statements</c> and catching whatever the server refuses with. Every other
/// state answers empty without opening a connection, so a server that does not collect the statistics is never
/// asked for them.
/// </summary>
/// <docs>operations/infrastructure/capabilities#readers</docs>
/// <code-under-test>src/Whizbang.Data.Postgres/Observability/PostgresTableStatisticsProvider.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/ServiceCollectionExtensions.cs</code-under-test>
[Category("Core")]
[Category("Observability")]
public class StatementStatisticsCapabilityTests {
  private const string SCOPE = "orders";
  private static readonly CapabilityId _statementStatistics = new(PostgresExtensionCatalog.PROVIDER, "pg_stat_statements");

  /// <summary>A registry that answers one state for everything and records what it was asked.</summary>
  private sealed class FixedStateRegistry(CapabilityState state) : ICapabilityRegistry {
    public List<(CapabilityId Id, string Scope)> Asked { get; } = [];
    public IReadOnlyList<ICapabilityProvider> Providers => [];
    public IReadOnlyList<CapabilityStatus> Statuses => [];
    public CapabilityVerdict Verdict => CapabilityVerdict.Empty;

    public CapabilityState GetState(CapabilityId id, string scope) {
      Asked.Add((id, scope));
      return state;
    }

    public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public void Record(IReadOnlyList<CapabilityStatus> statuses) {
      // Nothing to record: the state is fixed.
    }
    public void PublishVerdict(CapabilityVerdict verdict) {
      // Nothing to publish: the verdict is not under test.
    }
    public Task<CapabilityVerdict> WaitForVerdictAsync(CancellationToken cancellationToken) =>
      Task.FromResult(CapabilityVerdict.Empty);
  }

  /// <summary>Counts the connections the provider asks for and refuses each, so no test needs a server.</summary>
  private sealed class CountingOpener {
    public int Opened { get; private set; }

    public Func<CancellationToken, ValueTask<NpgsqlConnection>> OpenAsync => _ => {
      Opened++;
      throw new InvalidOperationException("the provider asked for a connection");
    };
  }

  [Test]
  [Arguments(CapabilityState.NeedsServerChange)]
  [Arguments(CapabilityState.Unknown)]
  [Arguments(CapabilityState.Provisionable)]
  [Arguments(CapabilityState.NotPermitted)]
  [Arguments(CapabilityState.NoPrivilege)]
  [Arguments(CapabilityState.Missing)]
  public async Task NotReady_AnswersEmpty_WithoutOpeningAConnectionAsync(CapabilityState state) {
    var registry = new FixedStateRegistry(state);
    var opener = new CountingOpener();
    var provider = new PostgresTableStatisticsProvider(opener.OpenAsync, capabilities: registry, capabilityScope: SCOPE);

    var predicates = await provider.GetExpensivePredicatesAsync();

    await Assert.That(predicates).IsEmpty();
    await Assert.That(opener.Opened).IsEqualTo(0)
      .Because("a server whose statement statistics are not Ready is never asked for them");
    await Assert.That(registry.Asked).IsEquivalentTo([(_statementStatistics, SCOPE)])
      .Because("the provider asks about pg_stat_statements in its own database's scope");
  }

  [Test]
  public async Task Ready_ReadsTheStatementStatisticsAsync() {
    var opener = new CountingOpener();
    var provider = new PostgresTableStatisticsProvider(
      opener.OpenAsync, capabilities: new FixedStateRegistry(CapabilityState.Ready), capabilityScope: SCOPE);

    await Assert.That(async () => await provider.GetExpensivePredicatesAsync()).Throws<InvalidOperationException>();
    await Assert.That(opener.Opened).IsEqualTo(1)
      .Because("Ready is the one state in which the statistics are read");
  }

  [Test]
  public async Task WithoutARegistry_AnswersEmpty_WithoutOpeningAConnectionAsync() {
    var opener = new CountingOpener();
    var provider = new PostgresTableStatisticsProvider(opener.OpenAsync);

    var predicates = await provider.GetExpensivePredicatesAsync();

    await Assert.That(predicates).IsEmpty();
    await Assert.That(opener.Opened).IsEqualTo(0)
      .Because("with no registry nothing says the statistics are collected, so they are not read");
  }

  [Test]
  [Arguments(null)]
  [Arguments(" ")]
  public async Task ARegistryWithoutAScope_IsRefusedAsync(string? scope) {
    var opener = new CountingOpener();

    await Assert.That(() => new PostgresTableStatisticsProvider(
        opener.OpenAsync, capabilities: new FixedStateRegistry(CapabilityState.Ready), capabilityScope: scope))
      .Throws<ArgumentException>();
  }

  /// <summary>
  /// The Dapper driver hands its provider the registry and the name its database is probed under, so the provider
  /// reads the state the probe recorded for that database.
  /// </summary>
  [Test]
  public async Task ADapperHost_AsksAboutItsOwnDatabaseAsync() {
    var registry = new FixedStateRegistry(CapabilityState.NeedsServerChange);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<ICapabilityRegistry>(registry);
    services.AddWhizbangPostgres(
      $"Host=localhost;Database={SCOPE}", JsonContextRegistry.CreateCombinedOptions(),
      initializeSchema: false, perspectiveEntries: [], managedObjects: null);
    await using var provider = services.BuildServiceProvider();

    var predicates = await provider.GetRequiredService<ITableStatisticsProvider>().GetExpensivePredicatesAsync();

    await Assert.That(predicates).IsEmpty();
    await Assert.That(registry.Asked).IsEquivalentTo([(_statementStatistics, SCOPE)])
      .Because("the scope is the name the driver registered its database under for the probe");
  }
}
