// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Perspectives;

namespace Whizbang.Data.EFCore.Postgres.Tests.BranchCoverage.EFCore;

/// <summary>
/// Branch coverage for constructor guards written as <c>?? throw</c> field initializers: each
/// rejects a missing collaborator by name and accepts a present one. No database is opened.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/Perspectives/EFCorePerspectiveReplayReader.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCoreFilterableEventStoreQuery.cs</code-under-test>
[Category("Shard5")]
public class ConstructorGuardBranchTests {

  [Test]
  public async Task ReplayReader_RejectsAMissingContextOrEventStore_AcceptsBothAsync() {
    await using var context = _context();
    var eventStore = new EFCoreEventStore<BareContext>(context);

    var noContext = Assert.Throws<ArgumentNullException>(() => _ = new EFCorePerspectiveReplayReader<BareContext>(null!, eventStore));
    var noStore = Assert.Throws<ArgumentNullException>(() => _ = new EFCorePerspectiveReplayReader<BareContext>(context, null!));
    var reader = new EFCorePerspectiveReplayReader<BareContext>(context, eventStore);

    await Assert.That(noContext!.ParamName).IsEqualTo("context");
    await Assert.That(noStore!.ParamName).IsEqualTo("eventStore");
    await Assert.That(reader).IsNotNull();
  }

  [Test]
  public async Task FilterableEventStoreQuery_RejectsAMissingContext_AcceptsAPresentOneAsync() {
    await using var context = _context();

    var missing = Assert.Throws<ArgumentNullException>(() => _ = new EFCoreFilterableEventStoreQuery(null!));
    var query = new EFCoreFilterableEventStoreQuery(context);

    await Assert.That(missing!.ParamName).IsEqualTo("context");
    await Assert.That(query).IsNotNull();
  }

  private static BareContext _context() =>
    new(new DbContextOptionsBuilder<BareContext>().UseNpgsql("Host=localhost;Database=never_opened").Options);

  /// <summary>A DbContext that maps nothing and is never opened.</summary>
  public sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options);
}
