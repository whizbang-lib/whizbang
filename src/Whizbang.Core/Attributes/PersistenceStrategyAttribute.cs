using Whizbang.Core.Persistence;

namespace Whizbang.Core.Attributes;

/// <summary>
/// Specifies the persistence strategy for a receptor.
/// Overrides the global default configured in appsettings.json.
/// </summary>
/// <remarks>
/// <para>
/// Use this attribute to configure per-receptor persistence behavior when
/// different receptors have different requirements:
/// </para>
/// <list type="bullet">
///   <item><b>Immediate</b>: Critical operations requiring instant consistency</item>
///   <item><b>Batched</b>: High-throughput ingestion scenarios</item>
///   <item><b>Outbox</b>: Cross-service coordination with guaranteed delivery</item>
/// </list>
/// <para>
/// For custom strategies, use the string constructor with a strategy name
/// that matches a configured strategy in appsettings.json.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Use built-in batched mode
/// [PersistenceStrategy(PersistenceMode.Batched)]
/// public class EventIngestionReceptor : IReceptor&lt;IngestEvent, EventIngested&gt; { }
///
/// // Use custom named strategy from appsettings.json
/// [PersistenceStrategy("high-throughput-batch")]
/// public class BulkImportReceptor : IReceptor&lt;ImportData, DataImported&gt; { }
///
/// // No attribute = uses global default from Persistence.DefaultMode
/// public class OrderReceptor : IReceptor&lt;CreateOrder, OrderCreated&gt; { }
/// </code>
/// </example>
/// <docs>core-concepts/persistence#per-receptor-strategy</docs>
/// <tests>Whizbang.Core.Tests/Persistence/PersistenceStrategyTests.cs</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class PersistenceStrategyAttribute : Attribute {
  /// <summary>
  /// Creates a persistence strategy attribute with the specified mode.
  /// </summary>
  /// <param name="mode">The persistence mode to use for this receptor.</param>
  public PersistenceStrategyAttribute(PersistenceMode mode) {
    Mode = mode;
  }

  /// <summary>
  /// Creates a persistence strategy attribute with a named custom strategy.
  /// The strategy name must match a configured strategy in appsettings.json.
  /// </summary>
  /// <param name="strategyName">The name of the custom strategy (e.g., "high-throughput-batch").</param>
  /// <example>
  /// <code>
  /// // appsettings.json:
  /// // "Whizbang": {
  /// //   "Persistence": {
  /// //     "Strategies": {
  /// //       "high-throughput-batch": {
  /// //         "Mode": "Batched",
  /// //         "BatchSize": 100,
  /// //         "FlushIntervalMs": 500
  /// //       }
  /// //     }
  /// //   }
  /// // }
  ///
  /// [PersistenceStrategy("high-throughput-batch")]
  /// public class BulkImportReceptor : IReceptor&lt;ImportData, DataImported&gt; { }
  /// </code>
  /// </example>
  public PersistenceStrategyAttribute(string strategyName) {
    StrategyName = strategyName ?? throw new ArgumentNullException(nameof(strategyName));
  }

  /// <summary>
  /// The persistence mode when using a built-in strategy.
  /// Null when using a named custom strategy.
  /// </summary>
  public PersistenceMode? Mode { get; }

  /// <summary>
  /// The name of a custom strategy configured in appsettings.json.
  /// Null when using a built-in persistence mode.
  /// </summary>
  public string? StrategyName { get; }
}
