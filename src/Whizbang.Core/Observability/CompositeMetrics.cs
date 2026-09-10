using System.Diagnostics.Metrics;

namespace Whizbang.Core.Observability;

/// <summary>
/// Passive meters for composite and collective events. A composite disappears once it is expanded (the
/// row is completed and only its children remain), so without these counters nothing shows how many
/// composites a consumer received, how many times each was expanded, how many child rows the expansions
/// created, or how many children were dropped because the consumer never subscribed to their type. The
/// ratios are the amplification model read directly: expansions per composite (one, unless a composite is
/// being re-expanded), children per composite, and the unsubscribed share.
/// </summary>
/// <docs>operations/observability/metrics#composites-and-collectives</docs>
/// <tests>tests/Whizbang.Core.Tests/Observability/CompositeMetricsTests.cs</tests>
public sealed class CompositeMetrics {
#pragma warning disable CA1707 // Repo style: public const fields are ALL_CAPS_SNAKE per editorconfig.
  /// <summary>Meter name, listed in <see cref="WhizbangMeters"/>.</summary>
  public const string METER_NAME = "Whizbang.Composites";
#pragma warning restore CA1707

  /// <summary>Initializes the composite meters on the shared Whizbang meter factory.</summary>
  /// <param name="whizbangMetrics">The shared meter factory holder.</param>
  /// <exception cref="ArgumentNullException">Thrown when the holder is null.</exception>
  public CompositeMetrics(WhizbangMetrics whizbangMetrics) {
    ArgumentNullException.ThrowIfNull(whizbangMetrics);
    var meter = whizbangMetrics.MeterFactory?.Create(METER_NAME) ?? new Meter(METER_NAME);

    Received = meter.CreatePassiveCounter<long>("whizbang.composites.received",
      description: "Composite inbox rows the dispatcher took up");
    Expansions = meter.CreatePassiveCounter<long>("whizbang.composites.expansions",
      description: "Times a composite was expanded into children; above received means a composite was expanded more than once");
    ChildrenCreated = meter.CreatePassiveCounter<long>("whizbang.composites.children_created",
      description: "Child inbox rows produced by expansions");
    ChildrenUnsubscribed = meter.CreatePassiveCounter<long>("whizbang.composites.children_unsubscribed",
      description: "Children dropped at expansion because this consumer has no subscription for their type");
    ChildrenRefused = meter.CreatePassiveCounter<long>("whizbang.composites.children_refused",
      description: "Children refused by the consumer's expansion budget");
    DeadLettered = meter.CreatePassiveCounter<long>("whizbang.composites.dead_lettered",
      description: "Composite rows moved to the dead-letter store instead of being expanded");
    CommitFailures = meter.CreatePassiveCounter<long>("whizbang.composites.commit_failures",
      description: "Expansions whose commit failed; the row stays leased and is retried on re-offer");
    CollectivesReceived = meter.CreatePassiveCounter<long>("whizbang.collectives.received",
      description: "Collective events that reached this consumer's inbox");
    CollectivesApplied = meter.CreatePassiveCounter<long>("whizbang.collectives.applied",
      description: "Collective events applied to the collective sink");
    CollectivesSkipped = meter.CreatePassiveCounter<long>("whizbang.collectives.skipped",
      description: "Collective events the sink skipped (already applied or filtered)");
  }

  /// <summary>Composite inbox rows the dispatcher took up.</summary>
  public PassiveCounter<long> Received { get; }

  /// <summary>Expansions performed; above <see cref="Received"/> means repeats.</summary>
  public PassiveCounter<long> Expansions { get; }

  /// <summary>Child rows produced by expansions.</summary>
  public PassiveCounter<long> ChildrenCreated { get; }

  /// <summary>Children dropped at expansion as unsubscribed.</summary>
  public PassiveCounter<long> ChildrenUnsubscribed { get; }

  /// <summary>Children refused by the expansion budget.</summary>
  public PassiveCounter<long> ChildrenRefused { get; }

  /// <summary>Composite rows dead-lettered.</summary>
  public PassiveCounter<long> DeadLettered { get; }

  /// <summary>Expansion commits that failed.</summary>
  public PassiveCounter<long> CommitFailures { get; }

  /// <summary>Collective events received.</summary>
  public PassiveCounter<long> CollectivesReceived { get; }

  /// <summary>Collective events applied to the sink.</summary>
  public PassiveCounter<long> CollectivesApplied { get; }

  /// <summary>Collective events the sink skipped.</summary>
  public PassiveCounter<long> CollectivesSkipped { get; }
}
