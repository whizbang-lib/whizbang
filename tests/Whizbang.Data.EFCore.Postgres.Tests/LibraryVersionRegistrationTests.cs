using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Perspectives;
using Whizbang.Core.Versioning;

#pragma warning disable CA1707 // test method underscores

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// <para>The <c>Assess</c> step compares this binary's library version against the migration
/// ledger. The version used to be registered only inside the generated turnkey callback, which the
/// driver skips when the consumer has already registered its own DbContext — a supported shape
/// (the driver logs "already registered — skipping turnkey registration"). That consumer then had no
/// <see cref="ILibraryVersionProvider"/>, the assessor read "unreadable", stood down, and the host
/// hung forever at <c>Host.StartAsync</c> (issue #619).</para>
/// <para>The framework knows its own version without anybody injecting it: the driver registers it
/// from a build-time constant, whoever owns the DbContext.</para>
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/PostgresDriverExtensions.cs</code-under-test>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/LibraryVersionInfo.g.cs</code-under-test>
[Category("Shard4")]
public class LibraryVersionRegistrationTests {
  private sealed class ConsumerOwnedDbContext(DbContextOptions<ConsumerOwnedDbContext> options) : DbContext(options) { }

  [Test]
  public async Task Postgres_WhenTheConsumerRegisteredItsOwnDbContext_StillRegistersTheLibraryVersionAsync() {
    var services = new ServiceCollection();
    // Bring-your-own DbContext: registered BEFORE the driver, so the turnkey callback is skipped.
    services.AddDbContext<ConsumerOwnedDbContext>(o => o.UseInMemoryDatabase("byo-library-version"));
    var builder = new WhizbangPerspectiveBuilder(services);
    _ = builder.WithEFCore<ConsumerOwnedDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var version = provider.GetService<ILibraryVersionProvider>();

    await Assert.That(version).IsNotNull()
      .Because("without it the assessor reads its own version as unreadable and stands down, and a "
             + "failed blocking step is a host that never reports ready — the #619 hang");
    await Assert.That(SemanticVersion.TryParse(version!.LibraryVersion, out _)).IsTrue()
      .Because($"the assessor orders versions by SemVer; '{version.LibraryVersion}' must parse");
  }

  [Test]
  public async Task Postgres_RegistersTheLibraryVersionAsTryAdd_SoAnExplicitRegistrationWinsAsync() {
    var services = new ServiceCollection();
    services.AddSingleton<ILibraryVersionProvider>(new LibraryVersionProvider("9.9.9-pinned"));
    services.AddDbContext<ConsumerOwnedDbContext>(o => o.UseInMemoryDatabase("byo-library-version-pinned"));
    _ = new WhizbangPerspectiveBuilder(services).WithEFCore<ConsumerOwnedDbContext>().WithDriver.Postgres;
    await using var provider = services.BuildServiceProvider();

    var version = provider.GetRequiredService<ILibraryVersionProvider>();

    await Assert.That(version.LibraryVersion).IsEqualTo("9.9.9-pinned")
      .Because("a consumer (or the generated registration) that already supplied the version must win; "
             + "the driver's constant is the floor, not an override");
  }

  [Test]
  public async Task LibraryVersionInfo_Value_IsTheBuildVersionAndParsesAsSemVerAsync() {
    // Read through a local: the constant itself is what the MSBuild target stamps, and the analyzer
    // (rightly) refuses to assert on a compile-time constant expression.
    var stamped = LibraryVersionInfo.Value;

    await Assert.That(stamped).IsNotEmpty();
    await Assert.That(SemanticVersion.TryParse(stamped, out _)).IsTrue()
      .Because("the constant is stamped from $(Version) at build time — the same value the generator "
             + "records in the migration ledger, so the two can never disagree");
    await Assert.That(stamped.Contains('+')).IsFalse()
      .Because("build metadata is stripped, matching what the ledger stores");
  }
}
