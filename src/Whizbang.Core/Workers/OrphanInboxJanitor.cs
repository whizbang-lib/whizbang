using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Perspectives;

namespace Whizbang.Core.Workers;

/// <summary>
/// Snapshot of locally-handled CLR message type names captured at DI-registration time. Slice
/// 3 of the resilient-transport plan uses this to let the orphan-inbox janitor know which
/// types are handled in this service without needing the source generator to emit an
/// enumeration of receptor target types.
/// </summary>
/// <remarks>
/// Built once during <c>AddOrphanInboxJanitor</c> by walking the
/// <see cref="IServiceCollection"/> for <c>IReceptor&lt;TMessage&gt;</c> /
/// <c>IReceptor&lt;TMessage,TResponse&gt;</c> registrations and collecting their generic
/// message-type arguments. Perspective Apply target types are unioned at janitor run time
/// from <see cref="IPerspectiveRunnerRegistry.GetEventTypes"/>.
/// </remarks>
public sealed class HandledReceptorTypeSnapshot {
  /// <summary>The set of CLR types known to be handled by an <c>IReceptor</c> in this service.</summary>
  public IReadOnlyCollection<Type> ReceptorMessageTypes { get; }

  /// <summary>Constructs a snapshot from the supplied set.</summary>
  public HandledReceptorTypeSnapshot(IReadOnlyCollection<Type> receptorMessageTypes) {
    ArgumentNullException.ThrowIfNull(receptorMessageTypes);
    ReceptorMessageTypes = receptorMessageTypes;
  }
}

/// <summary>
/// Background service that runs <see cref="IWorkCoordinator.PurgeOrphanInboxAsync"/> once at
/// host startup, deleting <c>wh_inbox</c> rows whose <c>message_type</c> is not handled by
/// any local receptor or perspective. Companion to slice 2's receive-time filter.
/// </summary>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates",
  Justification = "Startup-once path with infrequent log calls; LoggerMessage overhead not justified.")]
[SuppressMessage("Usage", "CA1873:Evaluation may be expensive",
  Justification = "Startup-once path; argument evaluation cost is negligible.")]
public sealed class OrphanInboxJanitor : BackgroundService {
  private readonly IServiceProvider _services;
  private readonly HandledReceptorTypeSnapshot _receptorSnapshot;
  private readonly ILogger<OrphanInboxJanitor>? _logger;

  /// <summary>Constructs the janitor with required services and snapshot.</summary>
  public OrphanInboxJanitor(
      IServiceProvider services,
      HandledReceptorTypeSnapshot receptorSnapshot,
      ILogger<OrphanInboxJanitor>? logger = null) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(receptorSnapshot);
    _services = services;
    _receptorSnapshot = receptorSnapshot;
    _logger = logger;
  }

  /// <inheritdoc />
  public override async Task StartAsync(CancellationToken cancellationToken) {
    try {
      using var scope = _services.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        _logger?.LogDebug("OrphanInboxJanitor skipped: no IWorkCoordinator registered");
        return;
      }

      var handledTypeNames = _collectHandledTypeNames(scope.ServiceProvider);
      if (handledTypeNames.Count == 0) {
        _logger?.LogInformation("OrphanInboxJanitor skipped: no locally-handled types — refusing to purge to avoid emptying the inbox during cold start");
        return;
      }

      var purged = await coordinator.PurgeOrphanInboxAsync(handledTypeNames, cancellationToken);
      if (purged.Count == 0) {
        _logger?.LogInformation("OrphanInboxJanitor: no orphan inbox rows to purge ({HandledTypeCount} handled types)", handledTypeNames.Count);
      } else {
        _logger?.LogInformation("OrphanInboxJanitor: purged {PurgedCount} orphan inbox rows ({HandledTypeCount} handled types)",
          purged.Count, handledTypeNames.Count);
        foreach (var group in purged.GroupBy(p => p.MessageType)) {
          _logger?.LogInformation("  → {Count}× {MessageType}", group.Count(), group.Key);
        }
      }
    } catch (Exception ex) {
      _logger?.LogError(ex, "OrphanInboxJanitor startup sweep failed; service continues");
    }

    await base.StartAsync(cancellationToken);
  }

  /// <inheritdoc />
  protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;

  private List<string> _collectHandledTypeNames(IServiceProvider scopedProvider) {
    var names = new HashSet<string>(StringComparer.Ordinal);

    foreach (var t in _receptorSnapshot.ReceptorMessageTypes) {
      var qualified = t.AssemblyQualifiedName;
      if (!string.IsNullOrEmpty(qualified)) {
        names.Add(EventTypeMatchingHelper.NormalizeTypeName(qualified));
      }
    }

    var perspectiveRegistry = scopedProvider.GetService<IPerspectiveRunnerRegistry>();
    if (perspectiveRegistry != null) {
      foreach (var t in perspectiveRegistry.GetEventTypes()) {
        var qualified = t.AssemblyQualifiedName;
        if (!string.IsNullOrEmpty(qualified)) {
          names.Add(EventTypeMatchingHelper.NormalizeTypeName(qualified));
        }
      }
    }

    var rawRegistry = scopedProvider.GetService<IRawReceptorRegistry>();
    if (rawRegistry != null) {
      foreach (var name in rawRegistry.RegisteredTypeNames) {
        names.Add(name);
      }
    }

    return [.. names];
  }
}
