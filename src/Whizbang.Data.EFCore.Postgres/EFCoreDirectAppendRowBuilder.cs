// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using Whizbang.Core;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres;

/// <summary>
/// How the EF Core driver's direct append (<see cref="EFCoreEventStore{TDbContext}.AppendAsync{TMessage}(Guid, TMessage, CancellationToken)"/>)
/// stores an event, for the book closer (#1741): the same minimal envelope, the payload serialized as its declared type,
/// the same envelope metadata, no scope column, and the same kind bits. The metadata is written as JSON text with the
/// options the driver's data source maps it with.
/// </summary>
/// <param name="jsonOptions">The options the driver serializes with (<c>JsonContextRegistry.CreateCombinedOptions()</c>).</param>
/// <param name="ephemeralModeResolver">Resolves an event type's ephemeral mode, as the direct append does; null for none.</param>
/// <docs>fundamentals/events/closing-the-books</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/CloseBooksSqlTests.cs</tests>
public sealed class EFCoreDirectAppendRowBuilder(JsonSerializerOptions jsonOptions, IEphemeralModeResolver? ephemeralModeResolver)
    : IDirectAppendRowBuilder {
  private readonly JsonSerializerOptions _jsonOptions = jsonOptions ?? throw new ArgumentNullException(nameof(jsonOptions));

  /// <inheritdoc />
  public ValueTask<DirectAppendRow> BuildAsync<TMessage>(TMessage payload, CancellationToken cancellationToken = default) where TMessage : notnull =>
    throw new NotImplementedException($"{_jsonOptions}{ephemeralModeResolver}{payload}");
}
