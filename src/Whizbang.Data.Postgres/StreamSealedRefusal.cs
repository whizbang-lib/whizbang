// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

/// <summary>
/// Turns the store's sealed-stream refusal (SQLSTATE <c>WHS01</c>, raised by <c>wh_append_event</c> for a direct append
/// to a sealed stream) into <see cref="StreamSealedException"/>, so the caller sees a typed error naming the stream, when
/// it was sealed and why.
/// </summary>
/// <remarks>
/// The store's message is kept as the exception's message: it is the same text a dead-lettered refusal records,
/// <c>Stream {id} was sealed at {sealed_at} and refuses appends. Reason: {reason}</c>. The three fields are read back from
/// it (the reason last, so it may hold any text); the message detail is not used, because Npgsql redacts it unless the
/// connection string opts in.
/// </remarks>
/// <docs>fundamentals/events/stream-end-of-life#a-sealed-stream-refuses-appends</docs>
/// <tests>tests/Whizbang.Core.Tests/Messaging/StreamSealedRefusalTests.cs</tests>
internal static class StreamSealedRefusal {
  /// <summary>The SQLSTATE <c>wh_append_event</c> raises for a sealed stream (migration 266).</summary>
  internal const string SQL_STATE = "WHS01";

  /// <summary>Runs a store, rethrowing the sealed-stream refusal as <see cref="StreamSealedException"/>.</summary>
  /// <param name="store">The store call.</param>
  /// <returns>A task that completes when the store does.</returns>
  internal static Task RunAsync(Func<Task> store) => throw new NotImplementedException();

  /// <summary>Builds the typed exception from the store's refusal.</summary>
  /// <param name="refusal">The refusal the store raised.</param>
  /// <returns>The exception.</returns>
  internal static StreamSealedException ToException(PostgresException refusal) => throw new NotImplementedException();
}
