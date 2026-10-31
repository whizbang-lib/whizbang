// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #1910: a perspective model holding arrays of records (<c>T[]</c>), at the top level and inside a collection
/// element, reads back on the Entity Framework driver exactly as it was written, on both write paths.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
[Category("Shard4")]
public class RecordArrayRoundTripTests {
  private const string TABLE = "wh_per_record_array_document";
  private const string PRIMITIVE_TABLE = "wh_per_primitive_array_document";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("record_arrays");
    _databaseName = database.Name;
    _connectionString = database.ConnectionString;
    await using var context = _context();
    await context.EnsureWhizbangDatabaseInitializedAsync();
  }

  [After(Test)]
  public async Task TeardownAsync() {
    if (_databaseName is not null) {
      await PerTestDatabaseFactory.DropAsync(_databaseName);
    }
  }

  private RecordArraysDbContext _context() {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(RecordArraysDbContext), new PostgresUpsertStrategy());
    return new RecordArraysDbContext(new DbContextOptionsBuilder<RecordArraysDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  private static RecordArrayDocument _model(Guid id) => new() {
    Id = id,
    Title = "arrays",
    Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001910"), "top-a"), new(Guid.Parse("01890000-0000-7000-8000-000000001911"), "top-b")],
    Groups = [
      new() { Name = "g1", Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001912"), "nested-a")] },
      new() { Name = "g2", Tags = [] },
    ],
    Sections = [new() { Name = "s1", Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001913"), "in-list")] }],
  };

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ArraysOfRecords_TopLevelAndNested_ReadBackAsWrittenAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await using (var writing = _context()) {
      var store = new EFCorePostgresPerspectiveStore<RecordArrayDocument>(writing, TABLE, UpsertWritePath.Strategy(atomic));
      await store.UpsertAsync(id, _model(id));
    }

    await using var reading = _context();
    var read = await new EFCorePostgresPerspectiveStore<RecordArrayDocument>(reading, TABLE).GetByStreamIdAsync(id);

    var expected = _model(id);
    await Assert.That(read).IsNotNull();
    await Assert.That(read!.Tags).IsEquivalentTo(expected.Tags)
      .Because("a top-level array of records reads back element for element");
    await Assert.That(read.Groups.Select(g => g.Name)).IsEquivalentTo(["g1", "g2"]);
    await Assert.That(read.Groups[0].Tags).IsEquivalentTo(expected.Groups[0].Tags)
      .Because("an array of records inside an array element reads back too");
    await Assert.That(read.Groups[1].Tags).IsEmpty();
    await Assert.That(read.Sections[0].Tags).IsEquivalentTo(expected.Sections[0].Tags)
      .Because("an array of records inside a list element reads back too");
  }

  [Test]
  public async Task ArraysOfRecords_ReadThroughTheContextQuery_ReadBackAsWrittenAsync() {
    Guid id = TrackedGuid.New();
    await using (var writing = _context()) {
      await new EFCorePostgresPerspectiveStore<RecordArrayDocument>(writing, TABLE, new PostgresUpsertStrategy()).UpsertAsync(id, _model(id));
    }

    await using var reading = _context();
    var row = await reading.Set<Whizbang.Core.Lenses.PerspectiveRow<RecordArrayDocument>>().SingleAsync(r => r.Id == id);

    await Assert.That(row.Data.Tags).IsEquivalentTo(_model(id).Tags);
    await Assert.That(row.Data.Groups[0].Tags).IsEquivalentTo(_model(id).Groups[0].Tags);
  }

  /// <summary>
  /// A row an earlier release wrote, before the model was stored opaquely: the document as the atomic upsert wrote it
  /// on the mapped path. It still reads.
  /// </summary>
  [Test]
  public async Task ARowWrittenBeforeTheChange_StillReadsAsync() {
    Guid id = TrackedGuid.New();
    await using (var writing = _context()) {
      await new EFCorePostgresPerspectiveStore<RecordArrayDocument>(writing, TABLE, new PostgresUpsertStrategy())
        .UpsertAsync(id, new RecordArrayDocument { Id = id });
    }
    await using (var conn = new Npgsql.NpgsqlConnection(_connectionString)) {
      await conn.OpenAsync();
      await using var cmd = conn.CreateCommand();
      cmd.CommandText = $"UPDATE {TABLE} SET data = @data::jsonb WHERE id = @id";
      cmd.Parameters.AddWithValue("id", id);
      cmd.Parameters.AddWithValue("data", $$"""
        {"Id":"{{id}}","Title":"stored","Tags":[{"TagId":"01890000-0000-7000-8000-000000001910","Label":"top-a"}],
         "Groups":[{"Name":"g1","Tags":[{"TagId":"01890000-0000-7000-8000-000000001912","Label":"nested-a"}]}],
         "Sections":[]}
        """);
      await Assert.That(await cmd.ExecuteNonQueryAsync()).IsEqualTo(1);
    }

    await using var reading = _context();
    var read = await new EFCorePostgresPerspectiveStore<RecordArrayDocument>(reading, TABLE).GetByStreamIdAsync(id);

    await Assert.That(read).IsNotNull();
    await Assert.That(read!.Title).IsEqualTo("stored");
    await Assert.That(read.Tags.Single().Label).IsEqualTo("top-a");
    await Assert.That(read.Groups.Single().Tags.Single().Label).IsEqualTo("nested-a");
  }

  /// <summary>Arrays of primitives are values on the mapped path, and read back as written.</summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ArraysOfPrimitives_ReadBackAsWrittenAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    Guid owner = TrackedGuid.New();
    await using (var writing = _context()) {
      var store = new EFCorePostgresPerspectiveStore<PrimitiveArrayDocument>(writing, PRIMITIVE_TABLE, UpsertWritePath.Strategy(atomic));
      await store.UpsertAsync(id, new PrimitiveArrayDocument { Id = id, Labels = ["a", "b"], Owners = [owner] });
    }

    await using var reading = _context();
    var read = await new EFCorePostgresPerspectiveStore<PrimitiveArrayDocument>(reading, PRIMITIVE_TABLE).GetByStreamIdAsync(id);

    await Assert.That(read!.Labels).IsEquivalentTo(["a", "b"]);
    await Assert.That(read.Owners).IsEquivalentTo([owner]);
  }
}
