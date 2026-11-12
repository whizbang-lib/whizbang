// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core;
using Whizbang.Core.Attributes;
using Whizbang.Core.Messaging;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.Dapper.Postgres.Tests;

// The Rename/ExecuteAsync apply path is [Obsolete] (superseded by the ledger-aware reconcile), but it still ships, so these
// tests lock in that it writes what the store uses.
#pragma warning disable CS0618

/// <summary>An event type as it was named before the rename these tests apply.</summary>
public sealed record RenameToolFormerNameProbe : IEvent {
  /// <summary>The order, which is the stream.</summary>
  [StreamId]
  public Guid OrderId { get; init; }
}

/// <summary>The same event type as it is named after the rename.</summary>
public sealed record RenameToolCurrentNameProbe : IEvent {
  /// <summary>The order, which is the stream.</summary>
  [StreamId]
  public Guid OrderId { get; init; }
}

/// <summary>
/// #2265: the obsolete rename tool writes the event store's <c>event_type</c> in the form the append path stores
/// (<c>Ns.Type, Assembly</c>), not the bare CLR name it is handed. Every event here is written through this driver's own
/// append path; each test compares a renamed event's <c>event_type</c> with what the append path writes for the target
/// type, so the tool cannot drift from the append path without failing here.
/// </summary>
/// <docs>fundamentals/identity/pinned-identity</docs>
/// <code-under-test>src/Whizbang.Data.Dapper.Postgres/DapperEventTypeRenameTool.cs</code-under-test>
public class DapperEventTypeRenameToolStoredNameTests : PostgresTestBase {
  private const string PINNED_ID = "6b2f0c1e-9a51-4d7e-8f3a-2c4b5d6e7f80";
  private static readonly string _formerName = TypeNameFormatter.FormatClrTypeName(typeof(RenameToolFormerNameProbe));
  private static readonly string _currentName = TypeNameFormatter.FormatClrTypeName(typeof(RenameToolCurrentNameProbe));

  [Test]
  public async Task ManualRename_WritesTheEventTypeTheAppendPathWritesForTheTargetTypeAsync() {
    await using var harness = await _harnessAsync(_catalogOfTheCurrentType(pinnedId: null));
    var renamed = await harness.AppendAsync(stream => new RenameToolFormerNameProbe { OrderId = stream });
    await Assert.That(await harness.EventTypeOfAsync(renamed)).IsEqualTo(TypeNameFormatter.Format(typeof(RenameToolFormerNameProbe)))
      .Because("the control: the append path stores the stored form, so the bare name the tool is handed matches no row as-is");

    var tool = new DapperEventTypeRenameTool(harness.Catalog, ConnectionFactory);
    tool.Rename(_formerName, _currentName);
    await tool.ExecuteAsync();

    var appendedNow = await harness.AppendAsync(stream => new RenameToolCurrentNameProbe { OrderId = stream });
    await Assert.That(await harness.EventTypeOfAsync(renamed)).IsEqualTo(await harness.EventTypeOfAsync(appendedNow))
      .Because("a renamed event must carry the name the append path writes for its type today, or no walk, grace or "
             + "perspective lookup by stored name finds it");
  }

  [Test]
  public async Task DetectedRename_WritesTheEventTypeTheAppendPathWritesForTheTargetTypeAsync() {
    await using var harness = await _harnessAsync(_catalogOfTheCurrentType(PINNED_ID));
    var renamed = await harness.AppendAsync(stream => new RenameToolFormerNameProbe { OrderId = stream });
    await harness.Connection.ExecuteAsync(
      "INSERT INTO wh_message_type_registry (clr_type_name, pinned_id, kind, updated_at) VALUES (@Clr, @PinnedId::uuid, 'event', NOW())",
      new { Clr = _formerName, PinnedId = PINNED_ID });

    var tool = new DapperEventTypeRenameTool(harness.Catalog, ConnectionFactory);
    await Assert.That(await tool.DetectRenamesAsync()).Count().IsEqualTo(1).Because("the registry holds the former name");
    await tool.ExecuteAsync();

    var appendedNow = await harness.AppendAsync(stream => new RenameToolCurrentNameProbe { OrderId = stream });
    await Assert.That(await harness.EventTypeOfAsync(renamed)).IsEqualTo(await harness.EventTypeOfAsync(appendedNow));
  }

  /// <summary>
  /// A manual rename to a name the catalog does not carry has no type to format, so the renamed event keeps the assembly it
  /// was stored under; the name part is all that changes.
  /// </summary>
  [Test]
  public async Task ManualRename_ToANameTheCatalogDoesNotCarry_KeepsTheStoredAssemblyAsync() {
    await using var harness = await _harnessAsync(new Catalog());
    var renamed = await harness.AppendAsync(stream => new RenameToolFormerNameProbe { OrderId = stream });

    var tool = new DapperEventTypeRenameTool(harness.Catalog, ConnectionFactory);
    tool.Rename(_formerName, _currentName);
    await tool.ExecuteAsync();

    var assembly = typeof(RenameToolFormerNameProbe).Assembly.GetName().Name;
    await Assert.That(await harness.EventTypeOfAsync(renamed)).IsEqualTo($"{_currentName}, {assembly}");
  }

  /// <summary>A former name that is a prefix of another type's name renames only its own events.</summary>
  [Test]
  public async Task ManualRename_LeavesATypeWhoseNameOnlyStartsWithTheFormerNameAloneAsync() {
    await using var harness = await _harnessAsync(_catalogOfTheCurrentType(pinnedId: null));
    var other = await harness.AppendAsync(stream => new RenameToolFormerNameProbe { OrderId = stream });

    var tool = new DapperEventTypeRenameTool(harness.Catalog, ConnectionFactory);
    // The prefix of the stored type's name, without its last character: a different type name altogether.
    tool.Rename(_formerName[..^1], _currentName);
    await tool.ExecuteAsync();

    await Assert.That(await harness.EventTypeOfAsync(other)).IsEqualTo(TypeNameFormatter.Format(typeof(RenameToolFormerNameProbe)));
  }

  // ===== Helpers =====

  private static Catalog _catalogOfTheCurrentType(string? pinnedId) =>
    new(new MessageTypeCatalogEntry(typeof(RenameToolCurrentNameProbe), _currentName, "event", pinnedId));

  private async Task<Harness> _harnessAsync(Catalog catalog) {
    var services = new ServiceCollection().AddWhizbangPostgres(
      ConnectionString, Whizbang.Data.Dapper.Postgres.Tests.Generated.WhizbangJsonContext.CreateOptions(), false, (string?)null, null);
    services.AddLogging();
    var provider = services.BuildServiceProvider();
    var connection = new NpgsqlConnection(ConnectionString);
    await connection.OpenAsync();
    return new Harness(provider, connection, catalog);
  }

  private sealed class Harness(ServiceProvider provider, NpgsqlConnection connection, Catalog catalog) : IAsyncDisposable {
    public NpgsqlConnection Connection => connection;
    public Catalog Catalog => catalog;

    /// <summary>Appends a new stream's event through the registered event store and returns the stored event's id.</summary>
    public async Task<Guid> AppendAsync<T>(Func<Guid, T> payloadOf) where T : IEvent {
      var stream = (Guid)TrackedGuid.New();
      await using var scope = provider.CreateAsyncScope();
      await scope.ServiceProvider.GetRequiredService<IEventStore>().AppendAsync(stream, payloadOf(stream));
      return await connection.ExecuteScalarAsync<Guid>("SELECT event_id FROM wh_event_store WHERE stream_id = @stream", new { stream });
    }

    public Task<string?> EventTypeOfAsync(Guid eventId) =>
      connection.ExecuteScalarAsync<string?>("SELECT event_type FROM wh_event_store WHERE event_id = @eventId", new { eventId });

    public async ValueTask DisposeAsync() {
      await connection.DisposeAsync();
      await provider.DisposeAsync();
    }
  }

  private sealed class Catalog(params MessageTypeCatalogEntry[] entries) : IMessageTypeCatalog {
    public IReadOnlyList<MessageTypeCatalogEntry> GetAll() => entries;
  }
}
