// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.Postgres;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The row the SELECT-then-UPDATE path writes over an existing one. The scope, business time and
/// version are each a decision the event or its hooks make, and each decision is asserted both ways.
/// </summary>
/// <remarks>
/// Asserted directly because the hooks that decide them come from a process-wide registry; driving
/// every combination through an upsert would mean replacing that registry under every other test.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/BaseUpsertStrategy.cs</code-under-test>
[Category("Shard1")]
public class BaseUpsertStrategyUpdatedRowTests {
  private static readonly DateTime _created = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
  private static readonly DateTime _storedUpdated = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
  private static readonly DateTime _eventTime = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

  private static readonly string[] _newPrincipals = ["group:new"];

  public sealed class Model {
    public string Name { get; set; } = string.Empty;
  }

  private static PerspectiveRow<Model> _existing() => new() {
    Id = Guid.NewGuid(),
    Data = new Model { Name = "before" },
    Metadata = new PerspectiveMetadata { EventType = "Old", EventId = "1" },
    Scope = new PerspectiveScope { TenantId = "stored-tenant", AllowedPrincipals = ["user:stored"] },
    CreatedAt = _created,
    UpdatedAt = _storedUpdated,
    Version = 4,
  };

  private static PerEventApplyHookPlan _plan(bool bumpVersion, bool suppressActivity) =>
    new(UpdatedAt: null, BumpVersion: bumpVersion, ModelFieldSetters: [], SuppressActivity: suppressActivity);

  [Test]
  public async Task AnOrdinaryEvent_StampsBusinessTime_BumpsTheVersion_AndKeepsTheStoredScopeAsync() {
    var existing = _existing();
    var incomingScope = new PerspectiveScope { TenantId = "incoming-tenant" };

    var row = BaseUpsertStrategy.BuildUpdatedRow(
      existing, new Model { Name = "after" }, new PerspectiveMetadata { EventType = "New", EventId = "2" },
      incomingScope, forceUpdateScope: false, _plan(bumpVersion: true, suppressActivity: false), _eventTime);

    await Assert.That(row.Id).IsEqualTo(existing.Id);
    await Assert.That(row.Data.Name).IsEqualTo("after");
    await Assert.That(row.Metadata.EventType).IsEqualTo("New");
    await Assert.That(row.CreatedAt).IsEqualTo(_created);
    await Assert.That(row.UpdatedAt).IsEqualTo(_eventTime);
    await Assert.That(row.Version).IsEqualTo(5);
    await Assert.That(row.Scope.TenantId).IsEqualTo("stored-tenant")
      .Because("only a scope event may change a row's scope; an ordinary event leaves the security boundary alone");
    await Assert.That(row.Scope).IsNotSameReferenceAs(existing.Scope)
      .Because("the scope is cloned so EF Core never sees the stored instance attached twice");
  }

  [Test]
  public async Task ANonActivityScopeEvent_KeepsBusinessTimeAndVersion_AndReplacesTheScopeAsync() {
    var existing = _existing();
    var incomingScope = new PerspectiveScope { TenantId = "incoming-tenant", AllowedPrincipals = ["group:new"] };

    var row = BaseUpsertStrategy.BuildUpdatedRow(
      existing, new Model { Name = "repaired" }, new PerspectiveMetadata(),
      incomingScope, forceUpdateScope: true, _plan(bumpVersion: false, suppressActivity: true), _eventTime);

    await Assert.That(row.UpdatedAt).IsEqualTo(_storedUpdated)
      .Because("a non-activity event is written but does not move business time");
    await Assert.That(row.Version).IsEqualTo(4);
    await Assert.That(row.Scope.TenantId).IsEqualTo("incoming-tenant");
    await Assert.That(row.Scope.AllowedPrincipals).IsEquivalentTo(_newPrincipals);
  }

  [Test]
  public async Task AScopeMissingItsLists_IsClonedWithEmptyListsAsync() {
    // A stored scope read from a document without these keys materializes with null lists; the clone
    // must not carry the nulls into the update.
    var existing = _existing();
    existing.Scope = new PerspectiveScope { TenantId = "t", AllowedPrincipals = null!, Extensions = null! };

    var row = BaseUpsertStrategy.BuildUpdatedRow(
      existing, new Model(), new PerspectiveMetadata(),
      new PerspectiveScope(), forceUpdateScope: false, _plan(bumpVersion: true, suppressActivity: false), _eventTime);

    await Assert.That(row.Scope.AllowedPrincipals).IsEmpty();
    await Assert.That(row.Scope.Extensions).IsEmpty();
  }
}
