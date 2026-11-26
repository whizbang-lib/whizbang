// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Startup;
using Whizbang.Data.Postgres;

namespace Whizbang.Core.Tests.Schema;

/// <summary>
/// #1992: a driver's schema pass reads the host's one Assess verdict, never a second answer, and refuses to begin on a
/// stand-down. Without a host or an assessor it runs as it always has.
/// </summary>
/// <code-under-test>src/Whizbang.Data.Postgres/StartupVerdictBinding.cs</code-under-test>
[Category("Startup")]
public class StartupVerdictBindingTests {
  [Test]
  public async Task WithNoHost_NoSource_OrNoAssessor_ThePassIsUnassessedAsync() {
    await using var empty = new ServiceCollection().BuildServiceProvider();
    await using var unassessed = _host(NullStartupAssessor.Instance);

    await Assert.That(await StartupVerdictBinding.ReadAsync(null, CancellationToken.None)).IsNull();
    await Assert.That(await StartupVerdictBinding.ReadAsync(empty, CancellationToken.None)).IsNull();
    await Assert.That(await StartupVerdictBinding.ReadAsync(unassessed, CancellationToken.None)).IsNull();
  }

  [Test]
  [Arguments(StartupVerdict.Migrate, false)]
  [Arguments(StartupVerdict.Serve, false)]
  [Arguments(StartupVerdict.Current, true)]
  public async Task AnyVerdictButStandDown_IsHandedToThePass_AndOnlyCurrentSkipsItAsync(StartupVerdict verdict, bool current) {
    var assessment = new StartupAssessment(verdict, "why");
    await using var host = _host(new Fixed(assessment));

    var read = await StartupVerdictBinding.ReadAsync(host, CancellationToken.None);

    await Assert.That(read).IsSameReferenceAs(assessment);
    await Assert.That(StartupVerdictBinding.IsCurrent(read)).IsEqualTo(current);
    await Assert.That(StartupVerdictBinding.IsCurrent(null)).IsFalse();
  }

  [Test]
  public async Task AStandDown_IsRefused_NamingTheReasonAsync() {
    await using var host = _host(new Fixed(new StartupAssessment(StartupVerdict.StandDown, "the ledger records 2.0.0")));

    var refusal = await Assert.That(async () => await StartupVerdictBinding.ReadAsync(host, CancellationToken.None))
      .ThrowsExactly<SchemaStandDownException>();

    await Assert.That(refusal!.Message).Contains("the ledger records 2.0.0");
  }

  [Test]
  public async Task AnApplicationStandDown_IsHandedToThePass_SoTheWMigrationsRunFirst_ButAWStandDownIsRefusedAsync() {
    // #2553: W! first. The application's stand-down never blocks the W! migrations, so the pass runs them with the
    // application's schema held; a W! stand-down (or W! served as it is while the application stands down) runs nothing.
    var applicationOnly = new StartupAssessment(StartupVerdict.StandDown, "application schema: newer columns") {
      Platform = new SchemaSideVerdict(Whizbang.Core.Versioning.SchemaChangeKind.Platform, StartupVerdict.Serve, "nothing applied since"),
      Application = new SchemaSideVerdict(Whizbang.Core.Versioning.SchemaChangeKind.Application, StartupVerdict.StandDown, "newer columns"),
    };
    var wAsItIs = applicationOnly with {
      Platform = new SchemaSideVerdict(Whizbang.Core.Versioning.SchemaChangeKind.Platform, StartupVerdict.Current, "served as it is"),
    };
    await using var host = _host(new Fixed(applicationOnly));
    await using var asItIs = _host(new Fixed(wAsItIs));

    var read = await StartupVerdictBinding.ReadAsync(host, CancellationToken.None);

    await Assert.That(read).IsSameReferenceAs(applicationOnly);
    await Assert.That(read!.HoldsApplication).IsTrue().Because("the pass applies the W! migrations alone");
    await Assert.That(async () => await StartupVerdictBinding.ReadAsync(asItIs, CancellationToken.None))
      .ThrowsExactly<SchemaStandDownException>();
  }

  [Test]
  public async Task TheRefusal_CarriesItsStandardShapesAsync() {
    var cause = new InvalidOperationException("x");

    await Assert.That(new SchemaStandDownException().Message).Contains("newer than this binary");
    await Assert.That(new SchemaStandDownException("r", cause).InnerException).IsSameReferenceAs(cause);
  }

  private static ServiceProvider _host(IStartupAssessor assessor) =>
    new ServiceCollection()
      .AddSingleton(new StartupAssessmentSource(
        assessor, NullStartupStageReporter.Instance, new FakeTimeProvider(), NullLogger<StartupAssessmentSource>.Instance))
      .BuildServiceProvider();

  private sealed class Fixed(StartupAssessment assessment) : IStartupAssessor {
    public Task<StartupAssessment> AssessAsync(CancellationToken cancellationToken) => Task.FromResult(assessment);
  }
}
