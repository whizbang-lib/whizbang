// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Serialization;
using Whizbang.Data.Dapper.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests.StartupParity;

/// <summary>
/// One Postgres driver as the startup parity suite composes it: how it is registered, and which perspective table
/// and Whizbang-named index the scenarios retire. Everything else a scenario does is the same for both.
/// </summary>
public abstract class StartupDriver {
  /// <summary>The driver's name, as the parity scenarios are labeled.</summary>
  public abstract string Name { get; }

  /// <summary>A perspective table the driver's schema builds.</summary>
  public abstract string Table { get; }

  /// <summary>A Whizbang-named index on <see cref="Table"/> that the driver does not declare.</summary>
  public abstract string Retired { get; }

  /// <summary>Registers the driver the way an application does.</summary>
  public abstract void Register(IServiceCollection services, string connectionString);

  /// <summary>The driver for a scenario's argument.</summary>
  public static StartupDriver For(string name) => name switch {
    "efcore" => new EFCoreDriver(),
    "dapper" => new DapperDriver(),
    _ => throw new ArgumentOutOfRangeException(nameof(name), name, "the drivers are efcore and dapper"),
  };

  public override string ToString() => Name;

  /// <summary>
  /// The EF Core driver, with a DbContext the application registered itself: the shape that used to miss the
  /// periodic re-run, because only the generated registration emitted the context's manifest.
  /// </summary>
  private sealed class EFCoreDriver : StartupDriver {
    public override string Name => "efcore";
    public override string Table => "wh_per_document_index_opted_out";
    public override string Retired => "idx_document_index_opted_out_data_gin";

    public override void Register(IServiceCollection services, string connectionString) {
      services.AddDbContext<DocumentIndexesDbContext>(o => o
        .UseNpgsql(connectionString)
        .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning)));
      _ = new WhizbangPerspectiveBuilder(services).WithEFCore<DocumentIndexesDbContext>().WithDriver.Postgres;
    }
  }

  /// <summary>The Dapper driver, with the per-perspective entries and declarations the generator emits.</summary>
  private sealed class DapperDriver : StartupDriver {
    private const string TABLE = "wh_per_parity_probe";

    public override string Name => "dapper";
    public override string Table => TABLE;
    public override string Retired => $"idx_{TABLE}_data_gin";

    public override void Register(IServiceCollection services, string connectionString) =>
      services.AddWhizbangPostgres(
        connectionString, JsonContextRegistry.CreateCombinedOptions(), initializeSchema: true,
        [new KeyValuePair<string, string>("ParityProbe", $"""
          CREATE TABLE IF NOT EXISTS {TABLE} (id uuid PRIMARY KEY, data jsonb NOT NULL);
          CREATE INDEX IF NOT EXISTS idx_{TABLE}_status ON {TABLE} ((data ->> 'Status'));
          """)],
        [(TABLE, "index", $"idx_{TABLE}_status", "ParityProbeModel")]);
  }
}
