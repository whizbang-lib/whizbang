using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;

namespace Whizbang.Data.Dapper.Postgres.Tests;

/// <summary>
/// Unit coverage for <c>MessageTypeRegistryReconciliationHostedService</c> — the
/// IHostedService that replaced the prior <c>using var populatorProvider =
/// services.BuildServiceProvider();</c> pattern at startup (see Bijan Camp's
/// 2026-06-12 report). Locks the four branches of the contract:
///
/// <list type="bullet">
///   <item><description>Both catalog + populator present → <c>PopulateAsync</c> runs once with the host's <c>CancellationToken</c>.</description></item>
///   <item><description>Catalog null → log-and-skip; <c>PopulateAsync</c> NOT called.</description></item>
///   <item><description>Populator null → log-and-skip; <c>PopulateAsync</c> NOT called.</description></item>
///   <item><description><c>StopAsync</c> returns a completed task (the populator owns no shutdown state).</description></item>
/// </list>
/// </summary>
public class MessageTypeRegistryReconciliationHostedServiceTests {

  [Test]
  public async Task StartAsync_CatalogAndPopulatorPresent_InvokesPopulateAsyncWithTokenAsync() {
    var catalog = new StubMessageTypeCatalog();
    var populator = new SpyPopulator();
    var sut = new MessageTypeRegistryReconciliationHostedService(
      NullLogger<MessageTypeRegistryReconciliationHostedService>.Instance,
      catalog,
      populator);

    using var cts = new CancellationTokenSource();
    await sut.StartAsync(cts.Token);

    await Assert.That(populator.PopulateAsyncCallCount).IsEqualTo(1)
      .Because("Happy path — the hosted service MUST call PopulateAsync exactly once when both the catalog and populator are registered.");
    await Assert.That(populator.LastCancellationToken).IsEqualTo(cts.Token)
      .Because("The hosted service must forward the host's CancellationToken so PopulateAsync can cooperate with shutdown.");
  }

  [Test]
  public async Task StartAsync_NullCatalog_LogsSkipAndDoesNotInvokePopulateAsyncAsync() {
    var populator = new SpyPopulator();
    var logger = new ListLogger<MessageTypeRegistryReconciliationHostedService>();
    var sut = new MessageTypeRegistryReconciliationHostedService(
      logger,
      catalog: null,
      populator: populator);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(populator.PopulateAsyncCallCount).IsEqualTo(0)
      .Because("Null catalog means AddWhizbang() wasn't called before AddWhizbangPostgres(). The hosted service must NOT call PopulateAsync — there's nothing to reconcile against.");
    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("Skipping message type registry reconciliation", StringComparison.Ordinal))
      .Because("The hosted service must log a single skip line so an operator can see the registry-population path is being declined.");
  }

  [Test]
  public async Task StartAsync_NullPopulator_LogsSkipAndDoesNotInvokePopulateAsyncAsync() {
    var catalog = new StubMessageTypeCatalog();
    var logger = new ListLogger<MessageTypeRegistryReconciliationHostedService>();
    var sut = new MessageTypeRegistryReconciliationHostedService(
      logger,
      catalog: catalog,
      populator: null);

    await sut.StartAsync(CancellationToken.None);

    await Assert.That(logger.Entries).Contains(e => e.Level == LogLevel.Information && e.Message.Contains("Skipping message type registry reconciliation", StringComparison.Ordinal))
      .Because("Symmetric to the null-catalog case — if the populator isn't registered there's nothing for the hosted service to do, and it must log so the operator can diagnose the missing registration.");
  }

  [Test]
  public async Task StopAsync_ReturnsCompletedTaskAsync() {
    var sut = new MessageTypeRegistryReconciliationHostedService(
      NullLogger<MessageTypeRegistryReconciliationHostedService>.Instance);

    var stopTask = sut.StopAsync(CancellationToken.None);

    await Assert.That(stopTask.IsCompleted).IsTrue()
      .Because("StopAsync owns no state, so it must return immediately — the populator's work is one-shot at StartAsync.");
  }

  // ============================================================================
  // helpers
  // ============================================================================

  private sealed class StubMessageTypeCatalog : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
  }

  private sealed class SpyPopulator : IMessageTypeRegistryPopulator {
    public int PopulateAsyncCallCount { get; private set; }
    public CancellationToken LastCancellationToken { get; private set; }

    public Task PopulateAsync(CancellationToken cancellationToken = default) {
      PopulateAsyncCallCount++;
      LastCancellationToken = cancellationToken;
      return Task.CompletedTask;
    }
  }

  private sealed class ListLogger<T> : ILogger<T> {
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => new NullScope();
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
      Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class NullScope : IDisposable {
      public void Dispose() { }
    }
  }
}
