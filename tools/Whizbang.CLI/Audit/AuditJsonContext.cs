// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Whizbang.CLI.Audit;

/// <summary>
/// Source-generated serialization for everything the audit command reads and writes, so the
/// command needs no reflection and stays trimming- and AOT-safe.
/// </summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  WriteIndented = true)]
[JsonSerializable(typeof(ProjectAssetsFile))]
[JsonSerializable(typeof(OsvBatchRequest))]
[JsonSerializable(typeof(OsvBatchResponse))]
[JsonSerializable(typeof(OsvVulnerability))]
internal sealed partial class AuditJsonContext : JsonSerializerContext;

/// <summary>
/// The part of <c>obj/project.assets.json</c> the command reads: per target framework, every
/// resolved <c>id/version</c> and whether it is a package or a project reference.
/// </summary>
internal sealed class ProjectAssetsFile {
  /// <summary>Resolved dependencies, keyed by target framework, then by <c>id/version</c>.</summary>
  public Dictionary<string, Dictionary<string, ProjectAssetsTargetEntry>> Targets { get; set; } = [];
}

/// <summary>One resolved dependency in a target framework.</summary>
internal sealed class ProjectAssetsTargetEntry {
  /// <summary><c>package</c> for a NuGet package, <c>project</c> for a project reference.</summary>
  public string? Type { get; init; }
}
