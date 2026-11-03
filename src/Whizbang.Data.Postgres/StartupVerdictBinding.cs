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
/// <tests>tests/Whizbang.Core.Tests/Schema/StartupVerdictBindingTests.cs</tests>
public static class StartupVerdictBinding {
  /// <summary>
  /// The verdict, or <see langword="null"/> when the host has no assessor (no host, or no driver supplied one): the
  /// pass then runs as it always has. A stand-down is refused here, so a pass never begins on it.
  /// </summary>
  /// <param name="services">The host's services; null for a call made outside a host.</param>
  /// <param name="cancellationToken">Cancellation token.</param>
  /// <returns>Migrate, Serve or Current; null when unassessed.</returns>
  /// <exception cref="SchemaStandDownException">The schema is newer than this binary; nothing may be applied.</exception>
  /// <exception cref="StartupAssessmentException">The version record could not be read.</exception>
  public static async Task<StartupAssessment?> ReadAsync(IServiceProvider? services, CancellationToken cancellationToken) {
    if (services?.GetService<StartupAssessmentSource>() is not { IsConfigured: true } source) {
      return null;
    }
    var assessment = await source.AssessAsync(cancellationToken).ConfigureAwait(false);
    return assessment.Verdict == StartupVerdict.StandDown ? throw new SchemaStandDownException(assessment.Reason) : assessment;
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

  /// <summary>Creates the refusal naming the reason, over a cause.</summary>
  /// <param name="reason">Why the instance stood down.</param>
  /// <param name="innerException">The cause.</param>
  public SchemaStandDownException(string reason, Exception innerException)
    : base($"This instance stood down, so no schema initialization runs on it and nothing is applied: {reason}", innerException) { }
}
