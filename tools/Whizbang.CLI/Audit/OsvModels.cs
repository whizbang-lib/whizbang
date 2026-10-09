// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace Whizbang.CLI.Audit;

// Collections are { get; set; } = [] rather than init. The source generator assigns init-only
// properties through an object initializer, so a field the answer leaves out (OSV omits "vulns"
// for a version with no advisories, and "aliases" for an advisory with none) would be set to null
// over the empty default instead of keeping it.

/// <summary>The body of <c>POST /v1/querybatch</c>.</summary>
internal sealed class OsvBatchRequest {
  /// <summary>One query per package version, answered in the same order.</summary>
  public List<OsvQuery> Queries { get; set; } = [];
}

/// <summary>"Which advisories affect this version of this package?"</summary>
internal sealed class OsvQuery {
  /// <summary>The package.</summary>
  public required OsvPackage Package { get; init; }

  /// <summary>The exact version to check.</summary>
  public required string Version { get; init; }

  /// <summary>The page to continue from, when an earlier answer said there is more.</summary>
  [JsonPropertyName("page_token")]
  public string? PageToken { get; init; }
}

/// <summary>A package in an ecosystem.</summary>
internal sealed class OsvPackage {
  /// <summary>The package name.</summary>
  public string? Name { get; init; }

  /// <summary>The ecosystem, <c>NuGet</c> for every package this command checks.</summary>
  public string? Ecosystem { get; init; }
}

/// <summary>The answer to a batch query.</summary>
internal sealed class OsvBatchResponse {
  /// <summary>One result per query, in query order.</summary>
  public List<OsvBatchResult> Results { get; set; } = [];
}

/// <summary>The advisories affecting one queried version.</summary>
internal sealed class OsvBatchResult {
  /// <summary>The advisories, by id only; the record itself is fetched separately.</summary>
  public List<OsvVulnerabilityReference> Vulns { get; set; } = [];

  /// <summary>Set when this result continues on another page.</summary>
  [JsonPropertyName("next_page_token")]
  public string? NextPageToken { get; init; }
}

/// <summary>An advisory id in a batch result.</summary>
internal sealed class OsvVulnerabilityReference {
  /// <summary>The advisory id, such as a GHSA identifier.</summary>
  public required string Id { get; init; }
}

/// <summary>An advisory record from <c>GET /v1/vulns/{id}</c>: the fields the report uses.</summary>
internal sealed class OsvVulnerability {
  /// <summary>The advisory id.</summary>
  public required string Id { get; init; }

  /// <summary>One line describing the advisory.</summary>
  public string? Summary { get; init; }

  /// <summary>Other ids for the same advisory, the CVE among them when one was assigned.</summary>
  public List<string> Aliases { get; set; } = [];

  /// <summary>Fields specific to the database the record came from, the severity among them.</summary>
  [JsonPropertyName("database_specific")]
  public OsvDatabaseSpecific? DatabaseSpecific { get; init; }

  /// <summary>The packages the advisory affects, with their affected and fixed versions.</summary>
  public List<OsvAffected> Affected { get; set; } = [];
}

/// <summary>Database-specific fields of an advisory.</summary>
internal sealed class OsvDatabaseSpecific {
  /// <summary>LOW, MODERATE, HIGH or CRITICAL in GitHub-reviewed records.</summary>
  public string? Severity { get; init; }
}

/// <summary>One affected package in an advisory.</summary>
internal sealed class OsvAffected {
  /// <summary>The package.</summary>
  public OsvPackage? Package { get; init; }

  /// <summary>The affected version ranges.</summary>
  public List<OsvRange> Ranges { get; set; } = [];
}

/// <summary>An affected range: a sequence of introduced and fixed events.</summary>
internal sealed class OsvRange {
  /// <summary>ECOSYSTEM or SEMVER for package versions; a commit range otherwise.</summary>
  public string? Type { get; init; }

  /// <summary>The events, in order.</summary>
  public List<OsvEvent> Events { get; set; } = [];
}

/// <summary>One event in a range.</summary>
internal sealed class OsvEvent {
  /// <summary>The version that fixes the range, when this event is a fix.</summary>
  public string? Fixed { get; init; }
}
