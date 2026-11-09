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
/// Issue #2114: a perspective model holding an <c>ICollection</c>, <c>IReadOnlyCollection</c> or <c>IEnumerable</c> of
/// records is stored as one opaque document, and is written and read back on the Entity Framework driver, on both write
/// paths.
/// </summary>
/// <remarks>
/// The mapped path cannot fill these interfaces, so the model is written whole by the generated message context, which
/// had no metadata for them: the first write failed with <c>NotSupportedException</c> ("JsonTypeInfo metadata ... was not
/// provided").
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("Integration")]
[Category("Shard4")]
public class InterfaceCollectionRoundTripTests {
  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("iface_coll");
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

  private InterfaceCollectionsDbContext _context() {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(InterfaceCollectionsDbContext), new PostgresUpsertStrategy());
    return new InterfaceCollectionsDbContext(new DbContextOptionsBuilder<InterfaceCollectionsDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  private static readonly InterfacedTag[] _tags = [
    new(Guid.Parse("01890000-0000-7000-8000-000000002114"), "first"),
    new(Guid.Parse("01890000-0000-7000-8000-000000002115"), "second"),
  ];

  private async Task<TModel?> _roundTripAsync<TModel>(string table, Guid id, TModel model, bool atomic) where TModel : class {
    await using (var writing = _context()) {
      await new EFCorePostgresPerspectiveStore<TModel>(writing, table, UpsertWritePath.Strategy(atomic)).UpsertAsync(id, model);
    }
    await using var reading = _context();
    return await new EFCorePostgresPerspectiveStore<TModel>(reading, table).GetByStreamIdAsync(id);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnICollectionOfRecords_ReadsBackAsWrittenAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    var read = await _roundTripAsync("wh_per_collection_interface_document", id,
      new CollectionInterfaceDocument { Id = id, Tags = [.. _tags] }, atomic);

    await Assert.That(read!.Tags).IsEquivalentTo(_tags);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnIReadOnlyCollectionOfRecords_ReadsBackAsWrittenAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    var read = await _roundTripAsync("wh_per_read_only_collection_interface_document", id,
      new ReadOnlyCollectionInterfaceDocument { Id = id, Tags = [.. _tags] }, atomic);

    await Assert.That(read!.Tags).IsEquivalentTo(_tags);
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnIEnumerableOfRecords_ReadsBackAsWrittenAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    var read = await _roundTripAsync("wh_per_enumerable_interface_document", id,
      new EnumerableInterfaceDocument { Id = id, Tags = [.. _tags] }, atomic);

    await Assert.That(read!.Tags).IsEquivalentTo(_tags);
  }
}
