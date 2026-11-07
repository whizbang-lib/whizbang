// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.ValueObjects;
using Whizbang.Data.EFCore.Postgres.QueryTranslation;
using Whizbang.Data.EFCore.Postgres.Tests.Generated;
using Whizbang.Testing.Containers;

namespace Whizbang.Data.EFCore.Postgres.Tests.Perspectives;

/// <summary>
/// Issue #2112: a perspective model holding a recursive type (a tree node with a collection of itself, or two types
/// holding each other) reads back on the Entity Framework driver at every depth, on both write paths.
/// </summary>
/// <remarks>
/// Mapped as a complex property, such a tree kept its first level and dropped every level below it on write, with no
/// error anywhere. Each case writes a four-level tree <c>a &gt; b &gt; c &gt; d</c> and reads every level back.
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
[Category("Integration")]
[Category("Shard4")]
public class RecursiveModelRoundTripTests {
  private const string LIST_TABLE = "wh_per_list_tree_document";
  private const string INTERFACE_TABLE = "wh_per_interface_tree_document";
  private const string IMMUTABLE_TABLE = "wh_per_immutable_tree_document";
  private const string ARRAY_TABLE = "wh_per_array_tree_document";
  private const string MUTUAL_TABLE = "wh_per_mutual_tree_document";

  private string _databaseName = null!;
  private string _connectionString = null!;

  [Before(Test)]
  public async Task SetupAsync() {
    await SharedPostgresContainer.InitializeAsync();
    var database = await PerTestDatabaseFactory.CreateAsync("recursive_tree");
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

  private RecursiveModelsDbContext _context() {
    ModelRegistrationRegistry.InvokeRegistration(new ServiceCollection(), typeof(RecursiveModelsDbContext), new PostgresUpsertStrategy());
    return new RecursiveModelsDbContext(new DbContextOptionsBuilder<RecursiveModelsDbContext>()
      .UseNpgsql(_connectionString)
      .UseWhizbangPhysicalFields()
      .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
      .Options);
  }

  private static readonly string[] _labels = ["a", "b", "c", "d"];

  private async Task _writeAsync<TModel>(string table, Guid id, TModel model, bool atomic) where TModel : class {
    await using var writing = _context();
    await new EFCorePostgresPerspectiveStore<TModel>(writing, table, UpsertWritePath.Strategy(atomic)).UpsertAsync(id, model);
  }

  private async Task<TModel?> _readAsync<TModel>(string table, Guid id) where TModel : class {
    await using var reading = _context();
    return await new EFCorePostgresPerspectiveStore<TModel>(reading, table).GetByStreamIdAsync(id);
  }

  private static ListTreeNode _listTree(int depth) => new() {
    Label = _labels[depth],
    Children = depth + 1 < _labels.Length ? [_listTree(depth + 1)] : null,
  };

  private static InterfaceTreeNode _interfaceTree(int depth) => new() {
    Label = _labels[depth],
    Children = depth + 1 < _labels.Length ? new List<InterfaceTreeNode> { _interfaceTree(depth + 1) } : null,
  };

  private static ImmutableTreeNode _immutableTree(int depth) => new() {
    Label = _labels[depth],
    Children = depth + 1 < _labels.Length ? [_immutableTree(depth + 1)] : null,
  };

  private static ArrayTreeNode _arrayTree(int depth) => new() {
    Label = _labels[depth],
    Children = depth + 1 < _labels.Length ? [_arrayTree(depth + 1)] : null,
  };

  /// <summary>a (branch) &gt; b (leaf) &gt; c (branch) &gt; d (leaf).</summary>
  private static MutualBranch _mutualTree() => new() {
    Label = "a",
    Leaves = [new MutualLeaf {
      Label = "b",
      Branches = [new MutualBranch { Label = "c", Leaves = [new MutualLeaf { Label = "d" }] }],
    }],
  };

  private static string _path(ListTreeNode? node) {
    if (node is null) {
      return "";
    }
    var below = node.Children is { Count: > 0 } c ? _path(c[0]) : "";
    return node.Label + _below(below);
  }

  private static string _path(InterfaceTreeNode? node) {
    if (node is null) {
      return "";
    }
    var below = node.Children is { Count: > 0 } c ? _path(c[0]) : "";
    return node.Label + _below(below);
  }

  private static string _path(ImmutableTreeNode? node) {
    if (node is null) {
      return "";
    }
    var below = node.Children is { Count: > 0 } c ? _path(c[0]) : "";
    return node.Label + _below(below);
  }

  private static string _path(ArrayTreeNode? node) {
    if (node is null) {
      return "";
    }
    var below = node.Children is { Length: > 0 } c ? _path(c[0]) : "";
    return node.Label + _below(below);
  }

  private static string _path(MutualBranch? branch) {
    if (branch is null) {
      return "";
    }
    var leaf = branch.Leaves is { Count: > 0 } leaves ? leaves[0] : null;
    if (leaf is null) {
      return branch.Label;
    }
    var next = leaf.Branches is { Count: > 0 } branches ? ">" + _path(branches[0]) : "";
    return $"{branch.Label}>{leaf.Label}{next}";
  }

  private static string _below(string rest) => rest.Length == 0 ? "" : ">" + rest;

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AListOfItself_ReadsBackEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id, Skeleton = [_listTree(0)] }, atomic);

    var read = await _readAsync<ListTreeDocument>(LIST_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d")
      .Because("mapped, the tree read back as a>b: every level below the first self-reference was dropped on write");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnIListOfItself_ReadsBackEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(INTERFACE_TABLE, id, new InterfaceTreeDocument { Id = id, Skeleton = [_interfaceTree(0)] }, atomic);

    var read = await _readAsync<InterfaceTreeDocument>(INTERFACE_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnImmutableListOfItself_ReadsBackEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(IMMUTABLE_TABLE, id, new ImmutableTreeDocument { Id = id, Skeleton = [_immutableTree(0)] }, atomic);

    var read = await _readAsync<ImmutableTreeDocument>(IMMUTABLE_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task AnArrayOfItself_ReadsBackEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(ARRAY_TABLE, id, new ArrayTreeDocument { Id = id, Skeleton = [_arrayTree(0)] }, atomic);

    var read = await _readAsync<ArrayTreeDocument>(ARRAY_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d");
  }

  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task MutualRecursion_ReadsBackEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(MUTUAL_TABLE, id, new MutualTreeDocument { Id = id, Roots = [_mutualTree()] }, atomic);

    var read = await _readAsync<MutualTreeDocument>(MUTUAL_TABLE, id);

    await Assert.That(_path(read!.Roots!.Single())).IsEqualTo("a>b>c>d");
  }

  /// <summary>
  /// Replacing the whole document with a deeper tree keeps the new levels, which on the mapped path were not
  /// persisted either.
  /// </summary>
  [Test]
  [Arguments(true)]
  [Arguments(false)]
  public async Task ReplacingTheDocumentWithADeeperTree_KeepsEveryLevelAsync(bool atomic) {
    Guid id = TrackedGuid.New();
    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id, Skeleton = [new ListTreeNode { Label = "a" }] }, atomic);
    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id, Skeleton = [_listTree(0)] }, atomic);

    var read = await _readAsync<ListTreeDocument>(LIST_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d");
  }

  /// <summary>The context's own query, which is what a lens reads through, materializes every level too.</summary>
  [Test]
  public async Task TheContextQuery_ReadsBackEveryLevelAsync() {
    Guid id = TrackedGuid.New();
    await _writeAsync(MUTUAL_TABLE, id, new MutualTreeDocument { Id = id, Roots = [_mutualTree()] }, atomic: true);

    await using var reading = _context();
    var row = await reading.Set<PerspectiveRow<MutualTreeDocument>>().SingleAsync(r => r.Id == id);

    await Assert.That(_path(row.Data.Roots!.Single())).IsEqualTo("a>b>c>d");
  }

  /// <summary>
  /// A row an earlier release wrote on the mapped path, where the tree was cut after its first self-reference, still
  /// reads: the levels it holds come back, and the levels that were never written are absent. Nothing in the database
  /// can restore them; re-projecting the stream from its events does.
  /// </summary>
  [Test]
  public async Task ARowTheMappedPathTruncated_StillReadsWhatItHoldsAsync() {
    Guid id = TrackedGuid.New();
    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id }, atomic: true);
    await _overwriteDataAsync(LIST_TABLE, id, $$"""
      {"Id":"{{id}}","Skeleton":[{"Label":"a","Children":[{"Label":"b"}]}]}
      """);

    var read = await _readAsync<ListTreeDocument>(LIST_TABLE, id);

    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b");
    await Assert.That(read.Skeleton![0].Children![0].Children).IsNull();
  }

  /// <summary>Re-projecting the stream writes the whole tree over the truncated row, which is the recovery.</summary>
  [Test]
  public async Task ATruncatedRowRewritten_ReadsBackEveryLevelAsync() {
    Guid id = TrackedGuid.New();
    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id }, atomic: true);
    await _overwriteDataAsync(LIST_TABLE, id, $$"""
      {"Id":"{{id}}","Skeleton":[{"Label":"a","Children":[{"Label":"b"}]}]}
      """);

    await _writeAsync(LIST_TABLE, id, new ListTreeDocument { Id = id, Skeleton = [_listTree(0)] }, atomic: true);

    var read = await _readAsync<ListTreeDocument>(LIST_TABLE, id);
    await Assert.That(_path(read!.Skeleton!.Single())).IsEqualTo("a>b>c>d");
  }

  private async Task _overwriteDataAsync(string table, Guid id, string data) {
    await using var conn = new Npgsql.NpgsqlConnection(_connectionString);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"UPDATE {table} SET data = @data::jsonb WHERE id = @id";
    cmd.Parameters.AddWithValue(nameof(id), id);
    cmd.Parameters.AddWithValue(nameof(data), data);
    await Assert.That(await cmd.ExecuteNonQueryAsync()).IsEqualTo(1);
  }
}
