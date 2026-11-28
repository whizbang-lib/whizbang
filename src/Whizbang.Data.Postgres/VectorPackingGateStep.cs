// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Serialization;
using Whizbang.Core.Workers;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The maintenance step behind packed vectors in stored event bodies (#2625). Each cycle it writes this service's
/// vector paths (<see cref="StoredVectorPaths"/>, registered by the generated message JSON context) to
/// <c>wh_event_vector_paths</c>, then asks <c>wh_open_vector_packing_gate</c>, which journals the packed-vectors
/// conversion once at least one instance is live and every live instance's heartbeat says stored form level 2 or
/// higher (<see cref="Whizbang.Core.Messaging.HeartbeatRequest.STORED_FORM_LEVEL"/>). The journal row is the switch the
/// write paths read, and it records this instance's library version, so the rollback gate stands an older library down
/// once packing has begun. The latch is one-way.
/// </summary>
/// <remarks>
/// The paths are written before the gate is asked, so the cycle that opens the gate packs every type this service
/// declares. A service writes only its own rows, and replaces the ones it no longer declares; the latest registration
/// of a type wins, which every service derives from the same declaration. The fleet is read here, once a cycle, never on
/// a write path.
/// </remarks>
/// <docs>data/event-store#packing-gate</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Step_IsNamed_AndRunsTheGateAsync</tests>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Paths_TheStepWritesThisServicesGeneratedPaths_AndReplacesOnlyItsOwnAsync</tests>
public sealed partial class VectorPackingGateStep : IMaintenanceStep {
  /// <summary>The status the gate returns from the call that latched it.</summary>
  public const string OPENED = "opened";

  private readonly ILogger _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="logger">Logs the cycle that opens the gate, and a closed gate at debug level.</param>
  public VectorPackingGateStep(ILogger<VectorPackingGateStep> logger) {
    ArgumentNullException.ThrowIfNull(logger);
    _logger = logger;
  }

  /// <summary>Builds the step from a host's services, logging to the host's logger when it has one.</summary>
  /// <param name="services">The host's services.</param>
  /// <returns>The step.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Step_IsNamed_AndRunsTheGateAsync</tests>
  public static VectorPackingGateStep FromServices(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    return new VectorPackingGateStep(
      services.GetService<ILogger<VectorPackingGateStep>>() ?? NullLogger<VectorPackingGateStep>.Instance);
  }

  /// <inheritdoc />
  public string Name => "vector-packing-gate";

  /// <inheritdoc />
  public async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken) =>
    _ = await OpenAsync(services, cancellationToken).ConfigureAwait(false);

  /// <summary>Writes this service's vector paths, asks the gate to open, and says what it answered.</summary>
  /// <param name="services">The maintenance cycle's services; the database is reached through
  /// <see cref="PostgresArchiveConnection"/>, the service name through <see cref="IServiceInstanceProvider"/> and the
  /// library version through <see cref="ILibraryVersionProvider"/>.</param>
  /// <param name="cancellationToken">Cancellation; set at shutdown.</param>
  /// <returns><c>open</c>, <c>opened</c>, or <c>closed (...)</c> with the reason.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_StaysClosedWhileALiveInstanceReadsNoPackedVectors_AndOpensOnceItIsGoneStaleOrRaisedAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Paths_WithoutAServiceName_AreNotWritten_AndTheGateStillAnswersAsync</tests>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Gate_WithoutALibraryVersion_StaysClosed_SoNothingPacksUnjournaledAsync</tests>
  public async Task<string> OpenAsync(IServiceProvider services, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(services);
    var database = services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);

    if (services.GetService<IServiceInstanceProvider>()?.ServiceName is { Length: > 0 } serviceName) {
      await using var register = new NpgsqlCommand($"SELECT {prefix}wh_register_event_vector_paths($1, $2::jsonb)", connection);
      register.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = serviceName });
      register.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = PathsJson() });
      _ = await register.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    await using var command = new NpgsqlCommand($"SELECT {prefix}wh_open_vector_packing_gate($1)", connection);
    command.Parameters.Add(new NpgsqlParameter {
      NpgsqlDbType = NpgsqlDbType.Text,
      Value = (object?)services.GetService<ILibraryVersionProvider>()?.LibraryVersion ?? DBNull.Value,
    });
    var status = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    if (status == OPENED) {
      LogOpened(_logger);
    } else {
      LogStatus(_logger, status);
    }
    return status;
  }

  /// <summary>
  /// Every registered event type's vector paths, as <c>wh_register_event_vector_paths</c> reads them: an array of
  /// <c>{"t": stored event type, "p": [[segment, ...], ...]}</c>, the type named as <c>wh_event_store.event_type</c>
  /// names it (<see cref="TypeNameFormatter.Format(Type)"/>).
  /// </summary>
  /// <returns>The JSON text.</returns>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorGateSqlTests.cs:Paths_TheStepWritesThisServicesGeneratedPaths_AndReplacesOnlyItsOwnAsync</tests>
  internal static string PathsJson() {
    using var buffer = new MemoryStream();
    using (var writer = new Utf8JsonWriter(buffer)) {
      writer.WriteStartArray();
      foreach (var type in StoredVectorPaths.EventTypes()) {
        writer.WriteStartObject();
        writer.WriteString("t", TypeNameFormatter.Format(type));
        writer.WriteStartArray("p");
        foreach (var path in StoredVectorPaths.For(type)) {
          writer.WriteStartArray();
          foreach (var segment in path) {
            writer.WriteStringValue(segment);
          }
          writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
      }
      writer.WriteEndArray();
    }
    return Encoding.UTF8.GetString(buffer.ToArray());
  }

  [LoggerMessage(Level = LogLevel.Information,
    Message = "Every live instance reads packed vectors: the write paths now store event vectors packed binary32, and the conversion is journaled, so an older library stands down at startup. This is one-way")]
  static partial void LogOpened(ILogger logger);

  [LoggerMessage(Level = LogLevel.Debug, Message = "Vector packing gate: {Status}")]
  static partial void LogStatus(ILogger logger, string status);
}
