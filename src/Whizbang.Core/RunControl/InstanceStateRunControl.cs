using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Whizbang.Core.Messaging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.RunControl;

/// <summary>
/// The run-control participant that records each lifecycle transition on this instance's own row
/// (<c>record_instance_state</c>), so peers and the status surface can observe it. The standby
/// handshake turns on exactly this: an instance cannot wait for its peers to reach standby unless
/// reaching standby is something a peer can observe.
/// </summary>
/// <remarks>
/// <para>
/// Riding the run-control broadcast means a transition's recording is awaited with the transition
/// itself — a transition is not a tick, and it is the one write that must not be deferred. But it
/// must also never <em>break</em> a transition: early startup phases (Connecting, Migrating) fire
/// before the schema — or the instance's own row — exists, and those failures are expected. The
/// write is therefore bounded by a short timeout and never throws; a missed recording self-heals
/// on the next transition.
/// </para>
/// <para>
/// The library version rides along when the storage driver's generated registration supplied an
/// <see cref="ILibraryVersionProvider"/> — the same version constant the migration ledger records,
/// no reflection involved.
/// </para>
/// </remarks>
/// <docs>proposals/startup-pipeline#capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/RunControl/InstanceStateRunControlTests.cs</tests>
public sealed partial class InstanceStateRunControl : IWhizbangRunControl {
  private static readonly TimeSpan _writeTimeout = TimeSpan.FromSeconds(5);

  private readonly IServiceScopeFactory _scopeFactory;
  private readonly IServiceInstanceProvider? _instanceProvider;
  private readonly ILibraryVersionProvider? _versionProvider;
  private readonly ILogger<InstanceStateRunControl> _logger;

  /// <summary>Creates the participant over the scope factory the coordinator resolves from.
  /// Inert without an instance provider — no identity means no row to record on.</summary>
  public InstanceStateRunControl(
      IServiceScopeFactory scopeFactory,
      IServiceInstanceProvider? instanceProvider = null,
      ILibraryVersionProvider? versionProvider = null,
      ILogger<InstanceStateRunControl>? logger = null) {
    ArgumentNullException.ThrowIfNull(scopeFactory);
    _scopeFactory = scopeFactory;
    _instanceProvider = instanceProvider;
    _versionProvider = versionProvider;
    _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<InstanceStateRunControl>.Instance;
  }

  /// <inheritdoc />
  public string Component => "instance-state";

  /// <inheritdoc />
  public async ValueTask OnPhaseAsync(LifecyclePhase phase, CancellationToken cancellationToken) {
    if (_instanceProvider is null) {
      return;   // no identity in this host — no row to record on
    }
    try {
      using var scope = _scopeFactory.CreateScope();
      var coordinator = scope.ServiceProvider.GetService<IWorkCoordinator>();
      if (coordinator is null) {
        return;   // no storage in this host — nothing observes instance rows either
      }
      using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      timeout.CancelAfter(_writeTimeout);
      var recorded = await coordinator.RecordInstanceStateAsync(
        _instanceProvider.InstanceId, phase.ToString(), _versionProvider?.LibraryVersion,
        timeout.Token).ConfigureAwait(false);
      if (!recorded) {
        // Expected before the first heartbeat registers the row; the next transition lands.
        LogRowNotYetRegistered(_logger, phase);
      }
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
    }
#pragma warning disable CA1031, RCS1075 // a recording failure must never break a lifecycle
    // transition: early phases fire before the schema exists, and those failures are expected.
    catch (Exception ex) {
      LogRecordFailed(_logger, phase, ex);
    }
#pragma warning restore CA1031, RCS1075
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
    Message = "Instance state '{Phase}' not recorded — the instance row does not exist yet (expected before the first heartbeat)")]
  static partial void LogRowNotYetRegistered(ILogger logger, LifecyclePhase phase);

  [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
    Message = "Recording instance state '{Phase}' failed (expected on a cold database; self-heals on the next transition)")]
  static partial void LogRecordFailed(ILogger logger, LifecyclePhase phase, Exception ex);
}
