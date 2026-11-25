// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whizbang.Core.Observability;
using Whizbang.Core.Workers;

namespace Whizbang.Core.Startup;

/// <summary>One read of this instance's own assignment (#2595).</summary>
/// <param name="Assignment">This instance's row; null when the read failed or the instance is not in the fleet.</param>
/// <param name="ObservedAt">When it was read, by this instance's clock.</param>
/// <param name="AwaitingSince">Since when the instance has awaited its share, while it does.</param>
/// <param name="Failure">Why the read failed, when it did.</param>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
public sealed record InstanceAssignmentObservation(
  InstanceAssignment? Assignment, DateTimeOffset ObservedAt, DateTimeOffset? AwaitingSince, string? Failure);

/// <summary>
/// Reads this instance's assignment on an interval from the Coordinate stage on (#2595), logs it when it first appears
/// and on every change ("Assigned: standard worker, 2500 partitions, map 12"), and keeps the latest read for the
/// <c>assignment</c> health component, including since when the instance has awaited its share.
/// </summary>
/// <remarks>
/// A read that fails is logged and reported, never thrown: the database's own component reports reachability. The
/// awaiting-share clock survives a failed read, so one bad read never restarts the grace.
/// </remarks>
/// <docs>operations/startup/startup-status#instance-assignments</docs>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Startup/InstanceAssignmentMonitorTests.cs</tests>
/// <tests>tests/Whizbang.Core.Component.Tests/Startup/InstanceAssignmentMonitorLoopTests.cs</tests>
public sealed partial class InstanceAssignmentMonitor : BackgroundService {
  private readonly IInstanceAssignmentReader? _reader;
  private readonly Guid _instanceId;
  private readonly InstanceAssignmentOptions _options;
  private readonly ILogger<InstanceAssignmentMonitor> _logger;
  private readonly TimeProvider _time;
  private InstanceAssignmentObservation? _latest;
  private InstanceAssignment? _logged;
  private DateTimeOffset? _awaitingSince;

  /// <summary>Creates the monitor.</summary>
  /// <param name="reader">Reads the fleet's assignments; null when no driver registered one, which every read reports.</param>
  /// <param name="instance">This instance's identity.</param>
  /// <param name="options">The interval and the grace; validated here.</param>
  /// <param name="assigner">The partition assigner's options, whose settle window the derived grace reads.</param>
  /// <param name="roles">The role options, whose renew interval the derived grace reads.</param>
  /// <param name="logger">Logger.</param>
  /// <param name="timeProvider">Optional clock; the system clock when null.</param>
  /// <exception cref="ArgumentNullException">A required argument is null.</exception>
#pragma warning disable S107 // DI-injection constructor: every parameter is a registered service or an optional seam
  public InstanceAssignmentMonitor(
      IInstanceAssignmentReader? reader,
      IServiceInstanceProvider instance,
      IOptions<InstanceAssignmentOptions> options,
      IOptions<PartitionAssignerOptions> assigner,
      IOptions<RoleAssignmentOptions> roles,
      ILogger<InstanceAssignmentMonitor> logger,
      TimeProvider? timeProvider = null) {
#pragma warning restore S107
    ArgumentNullException.ThrowIfNull(instance);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(assigner);
    ArgumentNullException.ThrowIfNull(roles);
    ArgumentNullException.ThrowIfNull(logger);
    _reader = reader;
    _instanceId = instance.InstanceId;
    _options = options.Value;
    _options.Validate();
    _logger = logger;
    _time = timeProvider ?? TimeProvider.System;
    AwaitingShareGrace = _options.GraceFor(assigner.Value.SettleWindow, roles.Value.RenewInterval);
  }

  /// <summary>How long this instance may await its share before the health component reports it Degraded.</summary>
  public TimeSpan AwaitingShareGrace { get; }

  /// <summary>RED stub.</summary>
  public DateTimeOffset? StartedAt => null;

  /// <summary>RED stub.</summary>
  internal void MarkStarted() => _ = _time;

  /// <summary>The latest read; null before the first.</summary>
  public InstanceAssignmentObservation? Latest => Volatile.Read(ref _latest);

  /// <summary>Raised after each read with what it found: the seam a test waits on. Must not block.</summary>
  public event Action<InstanceAssignmentObservation>? Observed;

  /// <summary>Reads this instance's assignment once, logs it when it changed, and records the read.</summary>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>The read.</returns>
  public async Task<InstanceAssignmentObservation> ObserveAsync(CancellationToken cancellationToken) {
    var observation = await _readAsync(cancellationToken).ConfigureAwait(false);
    Volatile.Write(ref _latest, observation);
    Observed?.Invoke(observation);
    return observation;
  }

  /// <inheritdoc />
  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    while (!stoppingToken.IsCancellationRequested) {
      try {
        _ = await ObserveAsync(stoppingToken).ConfigureAwait(false);
      } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
        break;
      }
      await Task.Delay(_options.ObserveInterval, _time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
  }

  private async Task<InstanceAssignmentObservation> _readAsync(CancellationToken cancellationToken) {
    var now = _time.GetUtcNow();
    if (_reader is null) {
      return new(null, now, _awaitingSince, "no instance-assignment reader is registered (no Postgres driver)");
    }
    System.Collections.Generic.IReadOnlyList<InstanceAssignment> rows;
    try {
      rows = await _reader.ReadAssignmentsAsync(cancellationToken).ConfigureAwait(false);
    } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
      throw;
#pragma warning disable CA1031 // reported, not swallowed: logged here, and the failure is the health detail
    } catch (Exception ex) {
#pragma warning restore CA1031
      LogReadFailed(_logger, ex);
      return new(null, now, _awaitingSince, $"the assignment could not be read: {TypeNameFormatter.ForLog(ex.GetType())}");
    }
    var own = rows.FirstOrDefault(r => r.InstanceId == _instanceId);
    _awaitingSince = own?.Kind == InstanceAssignmentKind.AwaitingShare ? _awaitingSince ?? now : null;
    if (own is not null && !_same(_logged, own)) {
      LogAssigned(_logger, new AssignmentText(own));
      _logged = own;
    }
    return new(own, now, _awaitingSince, null);
  }

  /// <summary>Whether two reads give the instance the same assignment: kind, roles, share and map.</summary>
  private static bool _same(InstanceAssignment? a, InstanceAssignment b) =>
    a is not null && a.Kind == b.Kind && a.OwnedPartitions == b.OwnedPartitions && a.MapRevision == b.MapRevision
    && a.Roles.SequenceEqual(b.Roles, StringComparer.Ordinal);

  /// <summary>What an assignment says, as the log line and the health detail write it after "Assigned: ".</summary>
  /// <param name="assignment">The assignment.</param>
  /// <returns>The description.</returns>
  public static string Describe(InstanceAssignment assignment) {
    ArgumentNullException.ThrowIfNull(assignment);
    var map = assignment.MapRevision is { } revision
      ? string.Create(CultureInfo.InvariantCulture, $"map {revision}")
      : "no map published";
    var share = string.Create(CultureInfo.InvariantCulture, $"{assignment.OwnedPartitions} partitions, {map}");
    return assignment.Kind switch {
      InstanceAssignmentKind.StandardWorker => $"standard worker, {share}",
      InstanceAssignmentKind.RoleHolder => $"role holder ({string.Join(", ", assignment.Roles)}), {share}",
      InstanceAssignmentKind.AwaitingShare => $"awaiting its share, {map}",
      InstanceAssignmentKind.Starting => "starting, not yet eligible for work",
      InstanceAssignmentKind.Draining => "draining",
      InstanceAssignmentKind.Ineligible => "ineligible: an older release in a service that claims by the partition map",
      _ => "evicted",
    };
  }

  /// <summary>An assignment as the log line writes it, described only when the line is written.</summary>
  private readonly record struct AssignmentText(InstanceAssignment Assignment) {
    public override string ToString() => Describe(Assignment);
  }

  [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Assigned: {Assignment}")]
  static partial void LogAssigned(ILogger logger, AssignmentText assignment);

  [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
    Message = "InstanceAssignmentMonitor: this instance's assignment could not be read; reading again on the next interval")]
  static partial void LogReadFailed(ILogger logger, Exception ex);
}
