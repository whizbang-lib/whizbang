// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Whizbang.Core;
using Whizbang.Core.Serialization;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// The <c>StoredVectorReaders</c> startup step (#2625): before an instance becomes ready, it checks that this build
/// still reads, as a vector, every path that stored bodies of the event types it reads hold packed. A path is recorded in
/// <c>wh_packed_vector_paths</c> once it is registered while the packing gate is open, and a later registration never
/// drops the record. A property opted out with <c>[StoredAsJsonArray]</c>, retyped, unmarked or removed has no packed-form
/// reader in this build, so a body packed there would fail to read; the step fails instead, naming each type and
/// property, and the instance never becomes ready (<see cref="PackedVectorsNoLongerReadException"/>).
/// </summary>
/// <remarks>
/// The types this service reads are the events of its <see cref="IMessageTypeCatalog"/> (so a type whose every vector
/// was removed is still checked) and every type that registers a vector path. Runs on every instance after
/// <c>Migrate</c>, blocking: reading starts only after it. One query; nothing is written.
/// </remarks>
/// <docs>data/event-store#packed-vectors-no-longer-read</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorReaderGuardSqlTests.cs</tests>
/// <tests>tests/Whizbang.Data.Dapper.Postgres.Tests/DapperPackedVectorTests.cs</tests>
public sealed partial class StoredVectorReadersStartupStep : IStartupStep {
  /// <summary>The step's name.</summary>
  public const string STEP_NAME = "StoredVectorReaders";

  private readonly IServiceProvider _services;
  private readonly IMessageTypeCatalog? _catalog;
  private readonly ILogger<StoredVectorReadersStartupStep> _logger;

  /// <summary>Creates the step.</summary>
  /// <param name="services">The host's services; the database is reached through <see cref="PostgresArchiveConnection"/>.</param>
  /// <param name="catalog">The service's message type catalog, or null when the host registers none.</param>
  /// <param name="logger">Logs a refusal at Error.</param>
  public StoredVectorReadersStartupStep(
      IServiceProvider services, IMessageTypeCatalog? catalog, ILogger<StoredVectorReadersStartupStep> logger) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(logger);
    _services = services;
    _catalog = catalog;
    _logger = logger;
  }

  /// <inheritdoc />
  public StartupStepDescriptor Descriptor { get; } = new() {
    Name = STEP_NAME,
    DependsOn = [FrameworkStartupSteps.MIGRATE],
    Blocking = true,
  };

  /// <inheritdoc />
  /// <exception cref="PackedVectorsNoLongerReadException">A packed path of a type this service reads is no longer a vector.</exception>
  public async ValueTask<StartupStepReport> ExecuteAsync(CancellationToken cancellationToken) {
    var database = _services.GetRequiredService<PostgresArchiveConnection>();
    var prefix = database.Schema is null ? string.Empty : SqlText.Identifier(database.Schema) + ".";
    await using var connection = await database.Open(cancellationToken).ConfigureAwait(false);
    await using var command = new NpgsqlCommand(
      $"SELECT event_type, path::text FROM {prefix}wh_packed_vector_paths_unread($1, $2::jsonb)", connection);
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = _readTypes() });
    command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = VectorPackingGateStep.PathsJson() });

    var unread = new List<string>();
    await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)) {
      while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
        unread.Add($"{reader.GetString(0)}: {_dotted(reader.GetString(1))}");
      }
    }
    if (unread.Count == 0) {
      return new StartupStepReport(StartupStepOutcome.Completed);
    }
    var refusal = new PackedVectorsNoLongerReadException(unread);
    LogRefused(_logger, refusal.Message);
    throw refusal;
  }

  /// <summary>The stored type names of every event this service reads.</summary>
  private string[] _readTypes() {
    var types = new HashSet<string>(StringComparer.Ordinal);
    if (_catalog is { IsAvailable: true }) {
      foreach (var entry in _catalog.GetAll()) {
        if (entry.Kind == "event") {
          types.Add(TypeNameFormatter.Format(entry.Type));
        }
      }
    }
    foreach (var type in StoredVectorPaths.EventTypes()) {
      types.Add(TypeNameFormatter.Format(type));
    }
    return [.. types];
  }

  /// <summary>A path as the property's JSON names joined by dots.</summary>
  private static string _dotted(string pathJson) {
    using var path = JsonDocument.Parse(pathJson);
    var text = new StringBuilder();
    foreach (var segment in path.RootElement.EnumerateArray()) {
      if (text.Length > 0) {
        text.Append('.');
      }
      text.Append(segment.GetString());
    }
    return text.ToString();
  }

  [LoggerMessage(Level = LogLevel.Error, Message = "{Refusal}")]
  static partial void LogRefused(ILogger logger, string refusal);
}

/// <summary>
/// Stored event bodies hold vectors packed at paths this build no longer reads as vectors (#2625), so reading them would
/// fail; the instance does not become ready.
/// </summary>
/// <docs>data/event-store#packed-vectors-no-longer-read</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/PackedVectorReaderGuardSqlTests.cs:Startup_APackedPathThisBuildNoLongerReads_FailsNamingTheTypeAndThePropertyAsync</tests>
public sealed class PackedVectorsNoLongerReadException : InvalidOperationException {
  /// <summary>Creates the refusal with no path named.</summary>
  public PackedVectorsNoLongerReadException() : this("Stored event bodies hold vectors packed at properties this build no longer reads as vectors") { }

  /// <summary>Creates the refusal with a message of its own.</summary>
  /// <param name="message">The message.</param>
  public PackedVectorsNoLongerReadException(string message) : base(message) => Paths = [];

  /// <summary>Creates the refusal with a message and its cause.</summary>
  /// <param name="message">The message.</param>
  /// <param name="innerException">The cause.</param>
  public PackedVectorsNoLongerReadException(string message, Exception innerException) : base(message, innerException) => Paths = [];

  /// <summary>Creates the refusal.</summary>
  /// <param name="paths">Each unread path, as <c>event type: property path</c>.</param>
  public PackedVectorsNoLongerReadException(IReadOnlyList<string> paths)
    : base(_message(paths)) {
    Paths = paths;
  }

  /// <summary>Each packed path this build no longer reads, as <c>event type: property path</c> (JSON names, dotted).</summary>
  public IReadOnlyList<string> Paths { get; }

  private static string _message(IReadOnlyList<string> paths) {
    ArgumentNullException.ThrowIfNull(paths);
    return "Stored event bodies hold vectors packed at properties this build no longer reads as vectors, so they "
      + $"cannot be read: {string.Join("; ", paths)}. Startup stops here rather than fail on each read. Restore each "
      + "property as a vector (float[] or IReadOnlyList<float>, or a List<float> or ReadOnlyMemory<float> marked "
      + "[VectorField], without [StoredAsJsonArray]), or unpack the stored bodies first (the unpack command, #2627), "
      + "then start again.";
  }
}
