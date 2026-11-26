// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Functions;
using Whizbang.Data.EFCore.Postgres.Perspectives;
using Whizbang.Data.Postgres;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// A save writes a strongly-typed id as its UUID string only while the gate of every schema it writes into is open, so a
/// context that maps tables in two schemas never writes one schema's form into the other's rows (#2518): what
/// <c>FlushAsync</c> and the upsert's SaveChanges path target.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form-switch</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/StrongIdDocumentTarget.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresPerspectiveStore.cs</code-under-test>
[Category("Integration")]
[Category("Shard2")]
public class StrongIdTrackedSaveFormTests {
  private const string HELD_SCHEMA = "strong_id_held";

  private string? _databaseName;
  private NpgsqlDataSource? _dataSource;

  public sealed class TwoSchemaDocument {
    public TestOrderId OrderId { get; set; }
  }

  public sealed class OpenSchemaRow {
    public Guid Id { get; set; }
    public TwoSchemaDocument Data { get; set; } = new();
  }

  public sealed class HeldSchemaRow {
    public Guid Id { get; set; }
    public TwoSchemaDocument Data { get; set; } = new();
  }

  private sealed class TwoSchemaContext(DbContextOptions options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
      modelBuilder.Entity<OpenSchemaRow>(entity => {
        entity.ToTable("wh_per_open_probe", "public");
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
      modelBuilder.Entity<HeldSchemaRow>(entity => {
        entity.ToTable("wh_per_held_probe", HELD_SCHEMA);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Id).HasColumnName("id");
        entity.ComplexProperty(e => e.Data, d => d.ToJson("data"));
      });
    }
  }

  [Test]
  public async Task ASaveIntoAnOpenAndAHeldSchema_WritesTheObjectFormIntoBoth_AndAloneTheOpenOneWritesTheUuidStringAsync() {
    var database = _databaseName!;
    StrongIdDocumentForms.Open(database, "public");
    StrongIdDocumentForms.Hold(database, HELD_SCHEMA);
    var (alone, together, held) = (TestOrderId.New(), TestOrderId.New(), TestOrderId.New());
    await using var context = _context();

    var aloneRow = new OpenSchemaRow { Id = alone.Value, Data = new() { OrderId = alone } };
    context.Add(aloneRow);
    await StrongIdDocumentTarget.ResolveAsync(context, CancellationToken.None);
    using (StrongIdDocumentTarget.OfTracked(context)) {
      await context.SaveChangesAsync();
    }
    context.Add(new OpenSchemaRow { Id = together.Value, Data = new() { OrderId = together } });
    context.Add(new HeldSchemaRow { Id = held.Value, Data = new() { OrderId = held } });
    await StrongIdDocumentTarget.ResolveAsync(context, CancellationToken.None);
    using (StrongIdDocumentTarget.OfTracked(context)) {
      await context.SaveChangesAsync();
    }

    await Assert.That(await _storedAsync("public.wh_per_open_probe", alone.Value)).Contains($"\"OrderId\": \"{alone.Value}\"")
      .Because("the save writes into the open schema alone");
    await Assert.That(await _storedAsync("public.wh_per_open_probe", together.Value))
      .Contains($"\"OrderId\": {{\"Value\": \"{together.Value}\"}}")
      .Because("the same save writes into a held schema, so it takes the form every release reads");
    await Assert.That(await _storedAsync($"{HELD_SCHEMA}.wh_per_held_probe", held.Value))
      .Contains($"\"OrderId\": {{\"Value\": \"{held.Value}\"}}");
  }

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    _databaseName = (await PerTestDatabaseFactory.CreateAsync("strong_id_saved")).Name;
    _dataSource = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(SharedPostgresContainer.ConnectionString) {
      Database = _databaseName,
    }.ConnectionString).Build();
    await using var connection = await _dataSource.OpenConnectionAsync();
    await using var create = new NpgsqlCommand(
      "CREATE TABLE public.wh_per_open_probe (id uuid PRIMARY KEY, data jsonb NOT NULL); "
      + $"CREATE SCHEMA {HELD_SCHEMA}; CREATE TABLE {HELD_SCHEMA}.wh_per_held_probe (id uuid PRIMARY KEY, data jsonb NOT NULL)",
      connection);
    await create.ExecuteNonQueryAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_dataSource is not null) {
      await _dataSource.DisposeAsync();
      _dataSource = null;
    }
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
      _databaseName = null;
    }
  }

  private TwoSchemaContext _context() =>
    new(new DbContextOptionsBuilder()
      .UseNpgsql(_dataSource!, npgsql => npgsql.UseWhizbangFunctions())
      .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);

  private async Task<string> _storedAsync(string table, Guid row) {
    await using var connection = await _dataSource!.OpenConnectionAsync();
#pragma warning disable S2077
    await using var select = new NpgsqlCommand($"SELECT data::text FROM {table} WHERE id = @row", connection);
#pragma warning restore S2077
    select.Parameters.AddWithValue(nameof(row), row);
    return (string)(await select.ExecuteScalarAsync())!;
  }
}
