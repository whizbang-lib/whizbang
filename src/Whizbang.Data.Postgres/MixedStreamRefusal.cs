// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Npgsql;
using Whizbang.Core.Messaging;

namespace Whizbang.Data.Postgres;

// RED stub: the translation is not implemented yet.
internal static class MixedStreamRefusal {
  internal const string SQL_STATE = "WH133";

  internal static async Task RunAsync(Func<Task> store) => await store().ConfigureAwait(false);

  internal static MixedStreamEventException ToException(PostgresException refusal) =>
    new(refusal.MessageText, Guid.Empty, Guid.Empty);
}
