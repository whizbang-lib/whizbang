using Microsoft.EntityFrameworkCore;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Lenses;
using Whizbang.Data.EFCore.Postgres.Functions;

namespace Whizbang.Data.EFCore.Postgres.Tests.Functions;

/// <summary>
/// Unit tests for <see cref="WhizbangJsonDbFunctions"/>. These markers exist purely to be
/// translated by EF Core inside a LINQ expression tree; invoking one directly is a
/// programming error and must fail loudly rather than return a misleading value.
/// </summary>
[Category("Shard1")]
public class WhizbangJsonDbFunctionsTests {

  [Test]
  public async Task AllowedPrincipalsContainsAny_WhenCalledDirectly_ThrowsInvalidOperationAsync() {
    var scope = new PerspectiveScope();

    await Assert.That(() => EF.Functions.AllowedPrincipalsContainsAny(scope, ["tenant-a"]))
        .ThrowsExactly<InvalidOperationException>();
  }

  [Test]
  public async Task AllowedPrincipalsContainsAny_WhenCalledDirectly_ExplainsQueryOnlyUsageAsync() {
    var scope = new PerspectiveScope();

    var exception = Assert.Throws<InvalidOperationException>(
        () => EF.Functions.AllowedPrincipalsContainsAny(scope, ["tenant-a"]));

    await Assert.That(exception!.Message).Contains("EF Core LINQ queries");
  }
}
