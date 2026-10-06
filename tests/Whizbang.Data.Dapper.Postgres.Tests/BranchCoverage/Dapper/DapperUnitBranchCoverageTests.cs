// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Data;
using Whizbang.Core.Dispatch;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.Dapper.Postgres.Collective;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Contracts;

namespace Whizbang.Data.Dapper.Postgres.Tests.BranchCoverage.DapperDriver;

/// <summary>
/// Branch outcomes in the Dapper driver that need no database: the event envelope adapter's
/// handling of ids, missing hops and a null hop list, the collective SET compiler's refusal of
/// selectors that are not a plain top-level property and its encoding of null values, the
/// collective apply options resolved with and without configured Postgres options, and the
/// registry populator's empty-catalog short-circuit with a logger.
/// </summary>
[Category("Unit")]
public class DapperUnitBranchCoverageTests {
  private static readonly JsonSerializerOptions _collectiveJsonOptions = new() { PropertyNamingPolicy = null };

  // ========================================
  // EVENT ENVELOPE ADAPTER
  // ========================================

  [Test]
  public async Task EventEnvelopeJsonbAdapter_NullOptions_ThrowsNamingThemAsync() {
    await Assert.That(() => new EventEnvelopeJsonbAdapter(null!))
      .Throws<ArgumentNullException>().WithParameterName("jsonOptions");
  }

  /// <summary>
  /// An envelope whose first hop carries a correlation and a causation id persists both in the
  /// metadata column, so a stored event can still be traced to the request and message that caused it.
  /// </summary>
  [Test]
  public async Task ToJsonb_CorrelationAndCausationPresent_PersistsBothIdsAsync() {
    var correlationId = CorrelationId.NewRootAligned();
    var causationId = MessageId.New();
    var envelope = _envelope([
      new MessageHop {
        Type = HopType.Current,
        ServiceInstance = ServiceInstanceInfo.Unknown,
        Timestamp = DateTimeOffset.UtcNow,
        CorrelationId = correlationId,
        CausationId = causationId
      }
    ]);

    var model = new EventEnvelopeJsonbAdapter(JsonOptionsHelper.CreateOptions()).ToJsonb(envelope);

    using var metadata = JsonDocument.Parse(model.MetadataJson);
    await Assert.That(metadata.RootElement.GetProperty("correlation_id").GetString())
      .IsEqualTo(correlationId.Value.ToString());
    await Assert.That(metadata.RootElement.GetProperty("causation_id").GetString())
      .IsEqualTo(causationId.Value.ToString());
  }

  /// <summary>An envelope with no hop list at all persists an empty hop array, not a failure.</summary>
  [Test]
  public async Task ToJsonb_NullHopList_PersistsAnEmptyHopArrayAsync() {
    var envelope = _envelope(null!);

    var model = new EventEnvelopeJsonbAdapter(JsonOptionsHelper.CreateOptions()).ToJsonb(envelope);

    using var metadata = JsonDocument.Parse(model.MetadataJson);
    await Assert.That(metadata.RootElement.GetProperty("hops").ValueKind).IsEqualTo(JsonValueKind.Array);
    await Assert.That(metadata.RootElement.GetProperty("hops").GetArrayLength()).IsEqualTo(0);
    await Assert.That(metadata.RootElement.GetProperty("correlation_id").GetString()).IsEqualTo(string.Empty);
  }

  /// <summary>
  /// A stored row whose hops value is JSON null (written by an older or foreign writer) reads back
  /// as an envelope with no hops rather than failing the read.
  /// </summary>
  [Test]
  public async Task FromJsonb_HopsStoredAsJsonNull_ReadsBackWithNoHopsAsync() {
    var adapter = new EventEnvelopeJsonbAdapter(JsonOptionsHelper.CreateOptions());
    var written = adapter.ToJsonb(_envelope([]));
    var messageId = Guid.CreateVersion7();
    var row = new JsonbPersistenceModel {
      DataJson = written.DataJson,
      MetadataJson = $"{{\"message_id\":\"{messageId}\",\"hops\":null}}",
      ScopeJson = null
    };

    var envelope = adapter.FromJsonb<TestEvent>(row);

    await Assert.That(envelope.MessageId.Value).IsEqualTo(messageId);
    await Assert.That(envelope.Hops).IsEmpty();
    await Assert.That(envelope.Payload.Payload).IsEqualTo("coverage");
  }

  // ========================================
  // COLLECTIVE SET COMPILER
  // ========================================

  /// <summary>A selector over a field rather than a property is refused: only properties are settable columns.</summary>
  [Test]
  public async Task SpecCompiler_FieldSelector_IsRefusedAsync() {
    var spec = new CoverageSpec(s => s.SetProperty(m => m.Counter, 5));

    await Assert.That(() => DapperCollectiveSpecCompiler<CoverageModel>.Compile(spec, _collectiveJsonOptions))
      .ThrowsExactly<NotSupportedException>();
  }

  /// <summary>A computed selector (a method call) is refused rather than misread as a property path.</summary>
  [Test]
  public async Task SpecCompiler_MethodCallSelector_IsRefusedAsync() {
    var spec = new CoverageSpec(s => s.SetProperty(m => m.Status.Trim(), "x"));

    await Assert.That(() => DapperCollectiveSpecCompiler<CoverageModel>.Compile(spec, _collectiveJsonOptions))
      .ThrowsExactly<NotSupportedException>();
  }

  /// <summary>A selector that does not start at the row (a static member) is refused.</summary>
  [Test]
  public async Task SpecCompiler_SelectorNotRootedAtTheRow_IsRefusedAsync() {
    var spec = new CoverageSpec(s => s.SetProperty(m => string.Empty, "x"));

    await Assert.That(() => DapperCollectiveSpecCompiler<CoverageModel>.Compile(spec, _collectiveJsonOptions))
      .ThrowsExactly<NotSupportedException>();
  }

  /// <summary>Setting a property to null binds the JSON null literal, which clears the value in the document.</summary>
  [Test]
  public async Task SpecCompiler_NullConstant_BindsJsonNullAsync() {
    var spec = new CoverageSpec(s => s.SetProperty(m => m.Note, (string?)null));

    var compiled = DapperCollectiveSpecCompiler<CoverageModel>.Compile(spec, _collectiveJsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("'{Note}'");
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("null");
  }

  /// <summary>Comparing a property against null binds the JSON null literal on the right-hand side.</summary>
  [Test]
  public async Task SpecCompiler_ComparisonAgainstNull_BindsJsonNullAsync() {
    var spec = new CoverageSpec(s => s.SetProperty(m => m.IsFlagged, m => m.Note == null));

    var compiled = DapperCollectiveSpecCompiler<CoverageModel>.Compile(spec, _collectiveJsonOptions);

    await Assert.That(compiled.SqlFragment).Contains("data->'Note'");
    await Assert.That(compiled.Parameters.Values.Single()).IsEqualTo("null");
  }

  // ========================================
  // COLLECTIVE APPLY OPTIONS
  // ========================================

  /// <summary>Without Postgres options in the container, collective applies use the framework defaults.</summary>
  [Test]
  public async Task AddCollectiveEventsDapper_NoPostgresOptions_ResolvesDefaultApplyOptionsAsync() {
    var services = new ServiceCollection();
    services.AddCollectiveEventsDapper([]);
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<CollectiveApplyOptions>();

    await Assert.That(options).IsEqualTo(CollectiveApplyOptions.Default);
  }

  /// <summary>Configured Postgres options carry their batch size and statement timeout into collective applies.</summary>
  [Test]
  public async Task AddCollectiveEventsDapper_ConfiguredPostgresOptions_CarryIntoApplyOptionsAsync() {
    var services = new ServiceCollection();
    services.Configure<PostgresOptions>(o => {
      o.CollectiveApplyBatchSize = 77;
      o.CollectiveApplyStatementTimeoutSeconds = 12;
    });
    services.AddCollectiveEventsDapper([]);
    await using var provider = services.BuildServiceProvider();

    var options = provider.GetRequiredService<CollectiveApplyOptions>();

    await Assert.That(options.BatchSize).IsEqualTo(77);
    await Assert.That(options.StatementTimeoutSeconds).IsEqualTo(12);
  }

  // ========================================
  // REGISTRY POPULATOR
  // ========================================

  /// <summary>
  /// An empty catalog is reported and skipped without opening a connection, so a service with no
  /// message types never touches the registry.
  /// </summary>
  [Test]
  public async Task PopulateAsync_EmptyCatalogWithLogger_LogsAndOpensNoConnectionAsync() {
    var logger = new CapturingLogger<DapperMessageTypeRegistryPopulator>();
    var populator = new DapperMessageTypeRegistryPopulator(new EmptyCatalog(), new RefusingConnectionFactory(), logger);

    await populator.PopulateAsync();

    await Assert.That(logger.Messages.Any(m => m.Contains("catalog is empty", StringComparison.Ordinal))).IsTrue();
  }

  private static MessageEnvelope<TestEvent> _envelope(List<MessageHop> hops) => new() {
    MessageId = MessageId.New(),
    Payload = new TestEvent { StreamId = Guid.CreateVersion7(), Payload = "coverage" },
    Hops = hops,
    DispatchContext = new MessageDispatchContext { Mode = DispatchModes.Local, Source = MessageSource.Local }
  };
}

/// <summary>A model with a settable property of each shape the compiler tests need, and one field.</summary>
sealed file class CoverageModel {
  internal int Counter = 1;

  public string Status { get; set; } = string.Empty;

  public string? Note { get; set; }

  public bool IsFlagged { get; set; }
}

sealed file class CoverageSpec(Expression<Action<ICollectiveSetters<CoverageModel>>> setters) : ICollectiveSpec<CoverageModel> {
  public Expression<Action<ICollectiveSetters<CoverageModel>>> Setters { get; } = setters;
}

sealed file class EmptyCatalog : IMessageTypeCatalog {
  public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => [];
}

sealed file class RefusingConnectionFactory : IDbConnectionFactory {
  public Task<System.Data.IDbConnection> CreateConnectionAsync(CancellationToken cancellationToken = default) =>
    throw new InvalidOperationException("an empty catalog must not open a connection");
}

sealed file class CapturingLogger<T> : ILogger<T> {
  public List<string> Messages { get; } = [];

  public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

  public bool IsEnabled(LogLevel logLevel) => true;

  public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
    Messages.Add(formatter(state, exception));
}
