using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Observability;
using Whizbang.Core.Startup;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The <c>Assess</c> verdict table (increment 9), exhaustively at the pure function and end to end
/// against a real ledger. Ordering is SemVer precedence; an unparseable own version refuses; an
/// unparseable recorded version cannot outrank anything.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres/EFCorePostgresStartupAssessor.cs</code-under-test>
[Category("Integration")]
[NotInParallel("EFCorePostgresTests")]
public class StartupAssessorTests : EFCoreTestBase {

  // ── the verdict table, as a pure function ───────────────────────────────

  [Test]
  public async Task EmptyLedger_VerdictIsMigrateAsync() {
    var assessment = EFCorePostgresStartupAssessor.ComputeVerdict("0.9.4", []);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Migrate)
      .Because("a fresh database is this instance's to migrate — contend for the duty");
  }

  [Test]
  [Arguments("0.9.4", "0.9.3")]
  [Arguments("0.9.4", "0.9.4")]
  [Arguments("1.0.0", "1.0.0-rc.1")]
  [Arguments("1.0.0-alpha.10", "1.0.0-alpha.2")]
  public async Task OnlyOlderOrSameRecorded_VerdictIsServeAsync(string mine, string recorded) {
    var assessment = EFCorePostgresStartupAssessor.ComputeVerdict(mine, [recorded]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Serve);
  }

  [Test]
  [Arguments("0.9.4", "0.9.5")]
  [Arguments("1.0.0-rc.1", "1.0.0")]
  [Arguments("1.0.0-alpha.2", "1.0.0-alpha.10")]
  public async Task AnyNewerRecorded_VerdictIsStandDownAsync(string mine, string recorded) {
    var assessment = EFCorePostgresStartupAssessor.ComputeVerdict(mine, ["0.0.1", recorded]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("numeric pre-release identifiers compare numerically — alpha.10 is newer than "
             + "alpha.2, and being wrong here means migrating when the instance must stand down");
  }

  [Test]
  public async Task UnparseableOwnVersion_RefusesRatherThanGuessesAsync() {
    var assessment = EFCorePostgresStartupAssessor.ComputeVerdict("not-a-version", ["0.9.4"]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.StandDown)
      .Because("an unparseable version is not guessed at — every wrong answer here is worse "
             + "than stopping");
  }

  [Test]
  public async Task UnparseableRecordedVersion_CannotOutrankAnythingAsync() {
    var assessment = EFCorePostgresStartupAssessor.ComputeVerdict("0.9.4", ["garbage", "0.9.3"]);
    await Assert.That(assessment.Verdict).IsEqualTo(StartupVerdict.Serve)
      .Because("unreadable history does not block — the same stance the ledger guard takes");
  }

  // ── end to end against the real ledger ─────────────────────────────────

  private EFCorePostgresStartupAssessor _assessorRunning(string version) {
    var services = new ServiceCollection();
    services.AddScoped(_ => CreateDbContext());
    var provider = services.BuildServiceProvider();
    return new EFCorePostgresStartupAssessor(
      provider.GetRequiredService<IServiceScopeFactory>(),
      typeof(WorkCoordinationDbContext),
      new LibraryVersionProvider(version));
  }

  [Test]
  [Timeout(60000)]
  public async Task AgainstTheRealLedger_ANewerRecordedVersion_StandsThisBinaryDownAsync(
      CancellationToken cancellationToken) {
    // The ledger was written by this build; a binary claiming the SemVer floor is older than it.
    var standDown = await _assessorRunning("0.0.0-0").AssessAsync(cancellationToken);
    await Assert.That(standDown.Verdict).IsEqualTo(StartupVerdict.StandDown);

    // And a binary far in the future outranks everything recorded.
    var serve = await _assessorRunning("999.0.0").AssessAsync(cancellationToken);
    await Assert.That(serve.Verdict).IsEqualTo(StartupVerdict.Serve);
  }
}
