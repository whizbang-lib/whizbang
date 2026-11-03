// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

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
/// the migration ledger beside the library version (#2027).
/// </summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
/// <tests>tests/Whizbang.Core.Tests/Versioning/ApplicationVersionTests.cs</tests>
public readonly record struct ApplicationVersion {
  /// <summary>The version nobody knows.</summary>
  public static ApplicationVersion Unknown => default;

  /// <summary>How this version orders.</summary>
  public ApplicationVersionScheme Scheme { get; }

  /// <summary>The text recorded for this version, or null when it is unknown.</summary>
  public string? Recorded { get; }

  /// <summary>Resolves the version from the host's override and the entry assembly.</summary>
  public static ApplicationVersion Resolve(string? explicitVersion, long? deployNumber, string? entryInformationalVersion) =>
    throw new NotImplementedException();

  /// <summary>Resolves the version from configuration and the entry assembly.</summary>
  public static ApplicationVersion FromConfiguration(IConfiguration? configuration, string? entryInformationalVersion) =>
    throw new NotImplementedException();

  /// <summary>Reads a recorded version back.</summary>
  public static ApplicationVersion FromRecorded(string? recorded) => throw new NotImplementedException();

  /// <summary>Orders two versions; null when they cannot be ordered.</summary>
  public int? CompareTo(ApplicationVersion other) => throw new NotImplementedException(Recorded + other.Recorded);
}

/// <summary>This instance's application version.</summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
public interface IApplicationVersionProvider {
  /// <summary>The application version this instance runs.</summary>
  ApplicationVersion ApplicationVersion { get; }
}

/// <summary>The default <see cref="IApplicationVersionProvider"/> over a fixed value.</summary>
/// <docs>operations/startup/startup-status#the-instance-row</docs>
public sealed class ApplicationVersionProvider(ApplicationVersion applicationVersion) : IApplicationVersionProvider {
  /// <inheritdoc />
  public ApplicationVersion ApplicationVersion => applicationVersion;
}
