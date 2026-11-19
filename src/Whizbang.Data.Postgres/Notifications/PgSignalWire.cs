// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.Core.Signals;

namespace Whizbang.Data.Postgres.Notifications;

/// <summary>
/// The signal bus's Postgres wire for the coordination signals (#2046): which channel each one travels on, and how a
/// payload read off the wire (or out of the durable log) becomes the typed signal again. Only the bus's transport and its
/// durable tail read this; everything else publishes and subscribes typed signals on the bus.
/// </summary>
/// <remarks>
/// <para>
/// The channel names are the ones an older release LISTENs on and its SQL NOTIFYs, and each payload is the one it sends
/// (the role, <c>epoch:revision</c>, the run id, nothing). Keeping them is what lets a mixed fleet hear each other during
/// a rolling deploy, in both directions, with no deprecation window: the wire did not change, only who owns it.
/// </para>
/// <para>
/// A doorbell binding ignores the payload; a payload binding rebuilds the signal through its own factory
/// (<see cref="IPayloadSignal{TSelf}.FromPayload"/>), so nothing here needs reflection.
/// </para>
/// </remarks>
/// <docs>fundamentals/signal-bus/signal-bus#coordination-signals</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresSignalTransportUnitTests.cs:StartAsync_ListensOnEachCoordinationChannel_ByTheNameAnOlderReleaseUsesAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PostgresSignalTransportUnitTests.cs:EveryPayloadSignal_HasAWireBindingAsync</tests>
internal static class PgSignalWire {
  /// <summary>The commit-order stamper's doorbell, rung by the store procedures after commit.</summary>
  internal const string COMMITTED_CHANNEL = "wh_committed";

  /// <summary>A released role; the payload is the role.</summary>
  internal const string ROLE_RELEASED_CHANNEL = "wh_role_released";

  /// <summary>A published partition assignment; the payload is <c>epoch:revision</c>.</summary>
  internal const string PARTITION_ASSIGNMENT_CHANNEL = "wh_partition_assignment";

  /// <summary>A recorded multi-stream run; the payload is the run id.</summary>
  internal const string MULTI_STREAM_WORK_CHANNEL = "wh_multi_stream_work";

  /// <summary>One signal type's channel and its rebuild from a payload.</summary>
  /// <param name="SignalType">The signal type.</param>
  /// <param name="Channel">The channel it travels on.</param>
  /// <param name="Dispatch">Rebuilds the signal from a payload and hands it to the sink.</param>
  internal sealed record Binding(Type SignalType, string Channel, Func<ISignalSink, string, CancellationToken, ValueTask> Dispatch);

  /// <summary>Every coordination signal bound to a channel of its own.</summary>
  internal static IReadOnlyList<Binding> Bindings { get; } = [
    _doorbell<RowsCommittedSignal>(COMMITTED_CHANNEL),
    _payload<RoleReleasedSignal>(ROLE_RELEASED_CHANNEL),
    _payload<PartitionAssignmentPublishedSignal>(PARTITION_ASSIGNMENT_CHANNEL),
    _doorbell<MultiStreamWorkSignal>(MULTI_STREAM_WORK_CHANNEL),
  ];

  /// <summary>The binding for <paramref name="signalType"/>, or null when it travels on the bus's own channels.</summary>
  internal static Binding? ForType(Type signalType) => Bindings.FirstOrDefault(b => b.SignalType == signalType);

  private static Binding _doorbell<TSignal>(string channel) where TSignal : ISignal =>
    new(typeof(TSignal), channel, static (sink, _, ct) => sink.ReceiveAsync<TSignal>(default!, ct));

  private static Binding _payload<TSignal>(string channel) where TSignal : IPayloadSignal<TSignal> =>
    new(typeof(TSignal), channel, static (sink, payload, ct) => sink.ReceiveAsync(TSignal.FromPayload(payload), ct));
}
