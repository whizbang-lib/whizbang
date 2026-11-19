// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Whizbang.Core.Versioning;

/// <summary>How an <see cref="ApplicationVersion"/> orders: by SemVer precedence, by deploy number, or not at all.</summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
public enum ApplicationVersionScheme {
  /// <summary>No version is known. It never orders against anything.</summary>
  Unknown = 0,

  /// <summary>A SemVer version, ordered by SemVer precedence.</summary>
  SemVer = 1,

  /// <summary>A monotonic deploy number the host supplied, ordered numerically.</summary>
  DeployNumber = 2,
}

/// <summary>
/// The version of the application (the service built on Whizbang) an instance runs, recorded on its instance row and in
/// the migration ledger beside the library version (#2027). Either version can be part of a rollout.
/// </summary>
/// <remarks>
/// <para>
/// Resolved once per process (owner decisions on #2022): a version the host supplies explicitly
/// (<c>Whizbang:Application:Version</c>, which must be SemVer) or a monotonic deploy number
/// (<c>Whizbang:Application:DeployNumber</c>) overrides; otherwise the entry assembly's informational version when it
/// reads as SemVer; otherwise the version is unknown.
/// </para>
/// <para>
/// An unknown version never orders against anything, so it never stands another instance down and is never stood down
/// by the application version alone. Two known versions order only within one scheme: a SemVer and a deploy number say
/// nothing about each other.
/// </para>
/// </remarks>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/ApplicationVersionTests.cs</tests>
public readonly record struct ApplicationVersion {
  /// <summary>The configuration key of a host-supplied version (SemVer).</summary>
  public const string VERSION_KEY = "Whizbang:Application:Version";

  /// <summary>The configuration key of a host-supplied monotonic deploy number.</summary>
  public const string DEPLOY_NUMBER_KEY = "Whizbang:Application:DeployNumber";

  private const string DEPLOY_PREFIX = "deploy:";
  private const string UNKNOWN_TEXT = "unknown";

  // Nullable: a default SemanticVersion carries a null pre-release, which its own comparison does not expect, and the
  // record's equality compares this field.
  private readonly SemanticVersion? _semVer;
  private readonly long _deployNumber;

  private ApplicationVersion(ApplicationVersionScheme scheme, string recorded, SemanticVersion? semVer, long deployNumber) {
    Scheme = scheme;
    Recorded = recorded;
    _semVer = semVer;
    _deployNumber = deployNumber;
  }

  /// <summary>The version nobody knows.</summary>
  public static ApplicationVersion Unknown => default;

  /// <summary>How this version orders.</summary>
  public ApplicationVersionScheme Scheme { get; }

  /// <summary>
  /// The text recorded for this version: SemVer text (build metadata dropped), <c>deploy:&lt;n&gt;</c> for a deploy
  /// number, or null when it is unknown.
  /// </summary>
  public string? Recorded { get; }

  /// <summary>Resolves the version from the host's override and the entry assembly.</summary>
  /// <param name="explicitVersion">A version the host supplies; must be SemVer. Blank is no override.</param>
  /// <param name="deployNumber">A monotonic deploy number the host supplies; must not be negative.</param>
  /// <param name="entryInformationalVersion">The entry assembly's informational version, when there is one.</param>
  /// <returns>The resolved version.</returns>
  /// <exception cref="InvalidOperationException">Both overrides are supplied, the explicit version is not SemVer, or
  /// the deploy number is negative: a host that says so explicitly is told at once, never read as unknown.</exception>
  public static ApplicationVersion Resolve(string? explicitVersion, long? deployNumber, string? entryInformationalVersion) {
    var hasExplicit = !string.IsNullOrWhiteSpace(explicitVersion);
    if (hasExplicit && deployNumber is not null) {
      throw new InvalidOperationException(
        $"Both {VERSION_KEY} and {DEPLOY_NUMBER_KEY} are set; the application version is one or the other.");
    }
    if (deployNumber is { } number) {
      return number >= 0
        ? _fromDeployNumber(number)
        : throw new InvalidOperationException($"{DEPLOY_NUMBER_KEY} must not be negative; it is {number}.");
    }
    if (hasExplicit) {
      return SemanticVersion.TryParse(explicitVersion, out var parsed)
        ? _fromSemVer(parsed)
        : throw new InvalidOperationException($"{VERSION_KEY} is '{explicitVersion}', which is not a SemVer version.");
    }
    return SemanticVersion.TryParse(entryInformationalVersion, out var entry) ? _fromSemVer(entry) : Unknown;
  }

  /// <summary>Resolves the version from configuration and the entry assembly.</summary>
  /// <param name="configuration">Where the host's override is read from; null reads none.</param>
  /// <param name="entryInformationalVersion">The entry assembly's informational version, when there is one.</param>
  /// <returns>The resolved version.</returns>
  /// <exception cref="InvalidOperationException">The override is malformed (see <see cref="Resolve"/>), or the deploy
  /// number is not a whole number.</exception>
  public static ApplicationVersion FromConfiguration(IConfiguration? configuration, string? entryInformationalVersion) {
    var deployText = configuration?[DEPLOY_NUMBER_KEY];
    long? deployNumber = null;
    if (!string.IsNullOrWhiteSpace(deployText)) {
      deployNumber = long.TryParse(deployText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
        ? number
        : throw new InvalidOperationException($"{DEPLOY_NUMBER_KEY} is '{deployText}', which is not a whole number.");
    }
    return Resolve(configuration?[VERSION_KEY], deployNumber, entryInformationalVersion);
  }

  /// <summary>Reads a recorded version back; anything that is neither form is unknown.</summary>
  /// <param name="recorded">The recorded text.</param>
  /// <returns>The version it records.</returns>
  public static ApplicationVersion FromRecorded(string? recorded) {
    if (recorded?.StartsWith(DEPLOY_PREFIX, StringComparison.Ordinal) == true) {
      return long.TryParse(recorded.AsSpan(DEPLOY_PREFIX.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
        ? _fromDeployNumber(number)
        : Unknown;
    }
    return SemanticVersion.TryParse(recorded, out var parsed) ? _fromSemVer(parsed) : Unknown;
  }

  /// <summary>
  /// Orders this version against <paramref name="other"/>: negative when older, zero when equal, positive when newer;
  /// null when the two cannot be ordered (either is unknown, or they use different schemes).
  /// </summary>
  /// <param name="other">The version to compare with.</param>
  /// <returns>The order, or null when there is none.</returns>
  public int? CompareTo(ApplicationVersion other) {
    if (Scheme == ApplicationVersionScheme.Unknown || Scheme != other.Scheme) {
      return null;
    }
    return Math.Sign(Scheme == ApplicationVersionScheme.SemVer
      ? _semVer!.Value.CompareTo(other._semVer!.Value)
      : _deployNumber.CompareTo(other._deployNumber));
  }

  /// <summary>The recorded text, or <c>unknown</c>.</summary>
  /// <returns>The text.</returns>
  public override string ToString() => Recorded ?? UNKNOWN_TEXT;

  private static ApplicationVersion _fromSemVer(SemanticVersion version) =>
    new(ApplicationVersionScheme.SemVer, version.ToString(), version, 0);

  private static ApplicationVersion _fromDeployNumber(long number) =>
    new(ApplicationVersionScheme.DeployNumber, DEPLOY_PREFIX + number.ToString(CultureInfo.InvariantCulture), null, number);
}

/// <summary>This instance's application version (#2027).</summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
public interface IApplicationVersionProvider {
  /// <summary>The application version this instance runs.</summary>
  ApplicationVersion ApplicationVersion { get; }
}

/// <summary>The default <see cref="IApplicationVersionProvider"/> over a fixed value.</summary>
/// <param name="applicationVersion">The version.</param>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/ApplicationVersionTests.cs:Provider_ReturnsItsVersionAsync</tests>
public sealed class ApplicationVersionProvider(ApplicationVersion applicationVersion) : IApplicationVersionProvider {
  /// <inheritdoc />
  public ApplicationVersion ApplicationVersion => applicationVersion;
}
