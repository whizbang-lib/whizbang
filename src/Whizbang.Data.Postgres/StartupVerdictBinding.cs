// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.DependencyInjection;
using Whizbang.Core.Startup;

namespace Whizbang.Data.Postgres;

/// <summary>
/// How a driver's schema pass reads the <c>Assess</c> verdict it acts on (#1992): the one verdict of the host it runs
/// in, from <see cref="StartupAssessmentSource"/>, never a second read of the version record. Every start path reads it
/// here first (the shared initializer's runners, and a direct call to the EF Core driver's initialization), so none can
/// skip the decision.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#one-verdict</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs</tests>
/// <tests>tests/Whizbang.Core.Unit.Workers.Tests/Schema/StartupVerdictBindingTests.cs</tests>
public static class StartupVerdictBinding {
  /// <summary>
  /// The verdict, or <see langword="null"/> when the host has no assessor (no host, or no driver supplied one): the
  /// pass then runs as it always has. A stand-down that applies nothing is refused here, so a pass never begins on it;
  /// a stand-down of the application's schema alone is handed to the pass, which applies the W! migrations first (#2553).
  /// </summary>
  /// <param name="services">The host's services; null for a call made outside a host.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Migrate, Serve or Current, or StandDown with the application's schema held; null when unassessed.</returns>
  /// <exception cref="SchemaStandDownException">The schema is newer than this binary; nothing may be applied.</exception>
  /// <exception cref="StartupAssessmentException">The version record could not be read.</exception>
  public static async Task<StartupAssessment?> ReadAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (services?.GetService<StartupAssessmentSource>() is not { IsConfigured: true } source) {
      return null;
    }
    var assessment = await source.AssessAsync(cancellationToken).ConfigureAwait(false);
    // #2553: W! first. A stand-down of the application's schema alone still lets the W! migrations run (the pass reads
    // HoldsApplication and leaves the application's schema as it is); the instance stands down after them.
    return assessment.StandsDownWithoutMigrating ? throw new SchemaStandDownException(assessment.Reason) : assessment;
  }

  /// <summary>
  /// Refuses a turnkey initialization that ran on an instance whose application schema stood down (#2553): the W!
  /// migrations were applied, and the instance does not serve. A call that returned success there would be the wrong
  /// easy path, since the host's data plane stays held. Nothing when the host has no verdict, or one that serves.
  /// </summary>
  /// <param name="services">The host's services.</param>
  /// <exception cref="SchemaStandDownException">The instance stood down, naming the side and its reason.</exception>
  /// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/WhizbangHostExtensionsTests.cs:EnsureWhizbangInitializedAsync_WhenTheApplicationSchemaStoodDown_RunsTheWMigrations_ThenThrowsNamingItAsync</tests>
  public static void ThrowIfStoodDown(IServiceProvider services) {
    ArgumentNullException.ThrowIfNull(services);
    if (services.GetService<StartupAssessmentSource>()?.Verdict is { Verdict: StartupVerdict.StandDown } stood) {
      throw SchemaStandDownException.AfterTheWMigrations(stood.Reason);
    }
  }

  /// <summary>Whether the verdict lets the pass skip the election and the migration: only <see cref="StartupVerdict.Current"/>.</summary>
  /// <param name="assessment">The verdict, or null when unassessed.</param>
  /// <returns><see langword="true"/> for Current.</returns>
  public static bool IsCurrent(StartupAssessment? assessment) => assessment?.Verdict == StartupVerdict.Current;
}

/// <summary>
/// A schema pass was asked for on an instance whose <c>Assess</c> verdict is a stand-down (#1992): the schema is newer
/// than this binary, so nothing is applied. The shared initializer never asks; a direct call to a driver's
/// initialization on a stood-down host gets this instead of a bootstrap or a migration over newer definitions.
/// </summary>
/// <docs>operations/startup/rolling-upgrades#standing-down</docs>
/// <tests>tests/Whizbang.Data.EFCore.Postgres.Tests/StartupParity/StartupParityTests.cs:TheTurnkeyCall_OnAStoodDownInstance_IsRefused_AndAppliesNothingAsync</tests>
public sealed class SchemaStandDownException : InvalidOperationException {
  /// <summary>Creates the refusal with no reason.</summary>
  public SchemaStandDownException() : this("the schema is newer than this binary") { }

  /// <summary>Creates the refusal naming the verdict's reason.</summary>
  /// <param name="reason">Why the instance stood down: the schema's version against the binary's.</param>
  public SchemaStandDownException(string reason)
    : base($"This instance stood down, so no schema initialization runs on it and nothing is applied: {reason}") { }

  /// <summary>
  /// The refusal of a stand-down that came after the W! migrations ran (#2553): the application's schema stood down, so
  /// the instance does not serve.
  /// </summary>
  /// <param name="reason">The verdict's reason, which names the side and its changes.</param>
  /// <returns>The refusal.</returns>
  public static SchemaStandDownException AfterTheWMigrations(string reason) =>
    new(reason, verbatimPrefix: "This instance applied the W! migrations, then stood down because its application schema cannot be served: ");

  private SchemaStandDownException(string reason, string verbatimPrefix) : base(verbatimPrefix + reason) { }

  /// <summary>Creates the refusal naming the reason, over a cause.</summary>
  /// <param name="reason">Why the instance stood down.</param>
  /// <param name="innerException">The cause.</param>
  public SchemaStandDownException(string reason, Exception innerException)
    : base($"This instance stood down, so no schema initialization runs on it and nothing is applied: {reason}", innerException) { }
}
