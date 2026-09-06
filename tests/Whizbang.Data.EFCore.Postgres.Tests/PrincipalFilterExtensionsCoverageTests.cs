using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Core.Security;
using Whizbang.Core.ValueObjects;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Coverage-round tests for <see cref="PrincipalFilterExtensions.FilterByUserOrPrincipals{TModel}"/>'s
/// three branches the existing <see cref="PrincipalFilterExtensionsTests"/> suite never isolates:
/// neither a user id nor any principal supplied, a user id with no principals, and principals with
/// no user id. All three are pure LINQ predicate composition over <see cref="PerspectiveRow{TModel}"/>
/// -- no PostgreSQL JSONB translation is involved (that is exercised, and already covered, by the
/// combined branch's existing Postgres-backed tests) -- so these run entirely in memory via
/// <see cref="Enumerable.AsQueryable{TElement}"/>, with no database required.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PrincipalFilterExtensions.cs</code-under-test>
[Category("Shard1")]
public class PrincipalFilterExtensionsCoverageTests {

  // A principal filter decides which rows a caller may see. A caller who supplies neither a user
  // id nor any principal must see nothing -- the alternative (no criteria = everything) silently
  // turns "I don't know who's asking" into full access, which is a data leak, not a convenience.
  [Test]
  public async Task FilterByUserOrPrincipals_NoUserIdAndNoPrincipals_ReturnsNoRowsAsync() {
    var rows = new List<PerspectiveRow<Order>> {
      _row(userId: "user-1", allowedPrincipals: ["user:user-1"]),
    }.AsQueryable();

    var result = rows.FilterByUserOrPrincipals(null, new HashSet<SecurityPrincipalId>()).ToList();

    await Assert.That(result.Count).IsEqualTo(0)
      .Because("no user id and no principals must resolve to zero access, never the whole table");
  }

  // With a user id but no principals to also check, ownership alone must decide -- a filter that
  // silently stopped applying the ownership half would let every row through unfiltered.
  [Test]
  public async Task FilterByUserOrPrincipals_UserIdOnlyNoPrincipals_MatchesOnlyTheOwnedRowAsync() {
    var ownedId = Guid.NewGuid();
    var otherId = Guid.NewGuid();
    var rows = new List<PerspectiveRow<Order>> {
      _row(id: ownedId, userId: "user-alice", allowedPrincipals: []),
      _row(id: otherId, userId: "user-bob", allowedPrincipals: []),
    }.AsQueryable();

    var result = rows.FilterByUserOrPrincipals("user-alice", new HashSet<SecurityPrincipalId>()).ToList();

    await Assert.That(result.Count).IsEqualTo(1)
      .Because("with an empty principal set, only direct ownership should decide access");
    await Assert.That(result[0].Id).IsEqualTo(ownedId);
  }

  // With principals but no user id supplied (a lookup made with only group/service context),
  // shared access alone must still work -- a filter that required BOTH inputs to ever match
  // anything would silently deny every principal-only caller.
  [Test]
  public async Task FilterByUserOrPrincipals_PrincipalsOnlyNoUserId_MatchesOnlyTheSharedRowAsync() {
    var sharedId = Guid.NewGuid();
    var unsharedId = Guid.NewGuid();
    var rows = new List<PerspectiveRow<Order>> {
      _row(id: sharedId, userId: "user-bob", allowedPrincipals: ["group:sales-team"]),
      _row(id: unsharedId, userId: "user-charlie", allowedPrincipals: ["group:engineering"]),
    }.AsQueryable();
    var callerPrincipals = new HashSet<SecurityPrincipalId> { SecurityPrincipalId.Group("sales-team") };

    var result = rows.FilterByUserOrPrincipals(null, callerPrincipals).ToList();

    await Assert.That(result.Count).IsEqualTo(1)
      .Because("with no user id supplied, sharing via AllowedPrincipals must still work on its own");
    await Assert.That(result[0].Id).IsEqualTo(sharedId);
  }

  // ===== Helpers =====

  private static PerspectiveRow<Order> _row(string? userId, List<string> allowedPrincipals, Guid? id = null) =>
    new() {
      // TrackedGuid.NewMedo(), not Guid.NewGuid(): every generated Whizbang id type rejects
      // anything but a UUIDv7, because ordering by id is ordering by time.
      Id = id ?? (Guid)TrackedGuid.NewMedo(),
      Data = new Order { OrderId = TestOrderId.From((Guid)TrackedGuid.NewMedo()), Amount = 1m, Status = "Created" },
      Metadata = new PerspectiveMetadata {
        EventType = "OrderCreated",
        EventId = Guid.NewGuid().ToString(),
        Timestamp = DateTime.UtcNow,
      },
      Scope = new PerspectiveScope { UserId = userId, AllowedPrincipals = allowedPrincipals },
      CreatedAt = DateTime.UtcNow,
      UpdatedAt = DateTime.UtcNow,
      Version = 1,
    };
}
