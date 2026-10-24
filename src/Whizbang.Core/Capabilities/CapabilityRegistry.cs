// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Logging;
using Whizbang.Core.Observability;

namespace Whizbang.Core.Capabilities;

/// <summary>
/// Every registered provider's capabilities in every scope: one state per capability and scope.
/// </summary>
/// <remarks>
/// A singleton. The <c>Capabilities</c> startup step probes once after the migration step;
/// <see cref="RefreshAsync"/> may be called again later. Until a scope has been probed, and whenever its
/// provider cannot probe it, its capabilities read <see cref="CapabilityState.Unknown"/>.
/// </remarks>
/// <docs>operations/infrastructure/capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityRegistryTests.cs</tests>
public interface ICapabilityRegistry {
  /// <summary>The registered providers.</summary>
  IReadOnlyList<ICapabilityProvider> Providers { get; }

  /// <summary>The latest status of every capability in every scope, provider by provider.</summary>
  IReadOnlyList<CapabilityStatus> Statuses { get; }

  /// <summary>One capability's state in one scope.</summary>
  /// <param name="id">The capability; provider and name are compared without regard to case.</param>
  /// <param name="scope">The scope, for example a database name.</param>
  /// <returns>The state, or <see cref="CapabilityState.Unknown"/> for anything not registered.</returns>
  CapabilityState GetState(CapabilityId id, string scope);

  /// <summary>Probes every provider's every scope again. Never throws for a scope it cannot probe.</summary>
  /// <param name="cancellationToken">The caller's cancellation, the only thing that propagates.</param>
  /// <returns>A task that completes when every scope has its new answer.</returns>
  Task RefreshAsync(CancellationToken cancellationToken);

  /// <summary>The latest verdict over the required capabilities; <see cref="CapabilityVerdict.Empty"/> until one is published.</summary>
  CapabilityVerdict Verdict { get; }

  /// <summary>Records a new verdict and releases every waiter. The <c>Capabilities</c> startup step publishes one per run.</summary>
  /// <param name="verdict">The verdict.</param>
  void PublishVerdict(CapabilityVerdict verdict);

  /// <summary>Completes with the first verdict published, at once when one already is.</summary>
  /// <param name="cancellationToken">Ends the wait.</param>
  /// <returns>The verdict.</returns>
  Task<CapabilityVerdict> WaitForVerdictAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The default <see cref="ICapabilityRegistry"/>: probes each provider's scopes in turn, reads a probe
/// that throws as Unknown with a warning naming the provider, the scope and the reason, and publishes the
/// <c>whizbang.capability.state</c> gauge when given metrics.
/// </summary>
/// <docs>operations/infrastructure/capabilities</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/CapabilityRegistryTests.cs</tests>
public sealed partial class CapabilityRegistry : ICapabilityRegistry {
#pragma warning disable CA1707
  /// <summary>The reason a capability reads Unknown before its scope's first probe.</summary>
  public const string NOT_PROBED = "not probed yet";
#pragma warning restore CA1707

  private readonly ILogger<CapabilityRegistry> _logger;
  private readonly TaskCompletionSource<CapabilityVerdict> _firstVerdict = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private volatile CapabilityStatus[] _statuses;
  private volatile CapabilityVerdict _verdict = CapabilityVerdict.Empty;

  /// <summary>Creates the registry over the providers.</summary>
  /// <param name="providers">The providers, in registration order.</param>
  /// <param name="logger">Receives a warning for each scope a provider could not probe.</param>
  /// <param name="metrics">When given, the <c>Whizbang.Capabilities</c> gauge is created over this registry.</param>
  public CapabilityRegistry(
      IEnumerable<ICapabilityProvider> providers,
      ILogger<CapabilityRegistry> logger,
      WhizbangMetrics? metrics = null) {
    ArgumentNullException.ThrowIfNull(providers);
    ArgumentNullException.ThrowIfNull(logger);
    Providers = [.. providers];
    _logger = logger;
    _statuses = [.. Providers.SelectMany(p => p.Scopes.SelectMany(scope => CapabilityStatus.Unknown(p.Descriptors, scope, NOT_PROBED)))];
    Metrics = metrics is null ? null : new CapabilityMetrics(metrics, this);
  }

  /// <inheritdoc />
  public IReadOnlyList<ICapabilityProvider> Providers { get; }

  /// <summary>The gauge publishing this registry, or null when none was requested.</summary>
  public CapabilityMetrics? Metrics { get; }

  /// <inheritdoc />
  public IReadOnlyList<CapabilityStatus> Statuses => _statuses;

  /// <inheritdoc />
  public CapabilityState GetState(CapabilityId id, string scope) =>
    _statuses.FirstOrDefault(s =>
      string.Equals(s.Descriptor.Id.Provider, id.Provider, StringComparison.OrdinalIgnoreCase)
      && string.Equals(s.Descriptor.Id.Name, id.Name, StringComparison.OrdinalIgnoreCase)
      && string.Equals(s.Scope, scope, StringComparison.Ordinal))?.State ?? CapabilityState.Unknown;

  /// <inheritdoc />
  public CapabilityVerdict Verdict => _verdict;

  /// <inheritdoc />
  public void PublishVerdict(CapabilityVerdict verdict) {
    ArgumentNullException.ThrowIfNull(verdict);
    _verdict = verdict;
    _firstVerdict.TrySetResult(verdict);
  }

  /// <inheritdoc />
  public Task<CapabilityVerdict> WaitForVerdictAsync(CancellationToken cancellationToken) =>
    _firstVerdict.Task.WaitAsync(cancellationToken);

  /// <inheritdoc />
  public async Task RefreshAsync(CancellationToken cancellationToken) {
    var statuses = new List<CapabilityStatus>(_statuses.Length);
    foreach (var provider in Providers) {
      foreach (var scope in provider.Scopes) {
        statuses.AddRange(await _probeAsync(provider, scope, cancellationToken).ConfigureAwait(false));
      }
    }
    _statuses = [.. statuses];
  }

  private async Task<IReadOnlyList<CapabilityStatus>> _probeAsync(
      ICapabilityProvider provider, string scope, CancellationToken cancellationToken) {
    try {
      return await provider.ProbeAsync(scope, cancellationToken).ConfigureAwait(false);
    } catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested) {
      var reason = $"{ex.GetType().Name}: {ex.Message}";
      LogProbeFailed(_logger, provider.Name, scope, reason);
      return CapabilityStatus.Unknown(provider.Descriptors, scope, reason);
    }
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
    Message = "Capabilities of {Provider} on '{Scope}' could not be probed ({Reason}); each reads as Unknown until the next probe")]
  static partial void LogProbeFailed(ILogger logger, string provider, string scope, string reason);
}
