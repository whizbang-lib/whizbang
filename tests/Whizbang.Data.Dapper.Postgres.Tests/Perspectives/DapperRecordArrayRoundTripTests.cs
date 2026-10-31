// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests.Perspectives;

/// <summary>A record held in an array.</summary>
public sealed record DapperArrayedTag(Guid TagId, string Label);

/// <summary>A collection element that itself holds an array of records.</summary>
public sealed record DapperArrayedGroup {
  public string Name { get; init; } = "";
  public DapperArrayedTag[] Tags { get; init; } = [];
}

/// <summary>A model whose collections are arrays of records.</summary>
public class DapperRecordArrayDocument {
  [StreamId]
  public Guid Id { get; set; }

  public DapperArrayedTag[] Tags { get; set; } = [];

  public DapperArrayedGroup[] Groups { get; set; } = [];

  public List<DapperArrayedGroup> Sections { get; set; } = [];
}

[JsonSerializable(typeof(DapperRecordArrayDocument))]
[JsonSerializable(typeof(PerspectiveScope))]
internal sealed partial class DapperRecordArrayJsonContext : JsonSerializerContext;

/// <summary>
/// Issue #1910 on the Dapper driver: a perspective model holding arrays of records (<c>T[]</c>), at the top level and
/// inside a collection element, reads back exactly as it was written.
/// </summary>
/// <docs>fundamentals/perspectives/perspectives</docs>
[NotInParallel("PostgreSQL")]
public class DapperRecordArrayRoundTripTests : PostgresTestBase {
  private const string TABLE = "wh_per_dapper_record_array";

  private static readonly JsonSerializerOptions _jsonOptions = new() {
    TypeInfoResolver = JsonTypeInfoResolver.Combine(
      DapperRecordArrayJsonContext.Default,
      global::Whizbang.Core.Generated.InfrastructureJsonContext.Default),
  };

  [Before(Test)]
  public async Task CreateTableAsync() {
    await using var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand($"""
      CREATE TABLE {TABLE} (
        id UUID PRIMARY KEY, data JSONB NOT NULL, metadata JSONB NOT NULL, scope JSONB NOT NULL,
        created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL, version INTEGER NOT NULL);
      """, connection);
    await command.ExecuteNonQueryAsync();
  }

  [Test]
  public async Task ArraysOfRecords_TopLevelAndNested_ReadBackAsWrittenAsync() {
    Guid id = TrackedGuid.New();
    var written = new DapperRecordArrayDocument {
      Id = id,
      Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001910"), "top-a"), new(Guid.Parse("01890000-0000-7000-8000-000000001911"), "top-b")],
      Groups = [
        new() { Name = "g1", Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001912"), "nested-a")] },
        new() { Name = "g2", Tags = [] },
      ],
      Sections = [new() { Name = "s1", Tags = [new(Guid.Parse("01890000-0000-7000-8000-000000001913"), "in-list")] }],
    };
    var store = new DapperPostgresPerspectiveStore<DapperRecordArrayDocument>(ConnectionString, TABLE, _jsonOptions);

    await store.UpsertAsync(id, written);
    var read = await new DapperPostgresPerspectiveStore<DapperRecordArrayDocument>(ConnectionString, TABLE, _jsonOptions)
      .GetByStreamIdAsync(id);

    await Assert.That(read).IsNotNull();
    await Assert.That(read!.Tags).IsEquivalentTo(written.Tags);
    await Assert.That(read.Groups.Select(g => g.Name)).IsEquivalentTo(["g1", "g2"]);
    await Assert.That(read.Groups[0].Tags).IsEquivalentTo(written.Groups[0].Tags)
      .Because("an array of records inside an array element reads back too");
    await Assert.That(read.Groups[1].Tags).IsEmpty();
    await Assert.That(read.Sections[0].Tags).IsEquivalentTo(written.Sections[0].Tags);
  }
}
