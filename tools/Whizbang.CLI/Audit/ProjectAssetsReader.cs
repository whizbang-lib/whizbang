// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Whizbang.CLI.Audit;

/// <summary>
/// A package version a project resolves after restore.
/// </summary>
/// <param name="Id">The package id, as NuGet spells it.</param>
/// <param name="Version">The resolved version.</param>
internal sealed record ResolvedPackage(string Id, string Version);

/// <summary>
/// Finds the Whizbang package versions a project, solution or directory of projects actually
/// resolves, by reading each project's <c>obj/project.assets.json</c>.
/// </summary>
/// <remarks>
/// The assets file is what restore resolved, transitive packages included, so it answers "which
/// versions does this build use" exactly; the project file only says what was asked for. It exists
/// only after <c>dotnet restore</c>, and its absence is a failure to check, not a clean result.
/// </remarks>
/// <docs>tools/cli-audit#what-it-checks</docs>
internal static partial class ProjectAssetsReader {
  /// <summary>The id prefix every published Whizbang package carries.</summary>
  public const string PACKAGE_PREFIX = "SoftwareExtravaganza.Whizbang.";

  /// <summary>
  /// Reads the Whizbang packages resolved by every project <paramref name="path"/> names.
  /// </summary>
  /// <param name="path">A project file, a .sln or .slnx solution, or a directory searched recursively for projects.</param>
  /// <returns>The distinct Whizbang package versions, ordered by id then version.</returns>
  /// <exception cref="AuditCheckException">
  /// The path names no project, or a project has no readable assets file (run <c>dotnet restore</c> first).
  /// </exception>
  public static IReadOnlyList<ResolvedPackage> ReadWhizbangPackages(string path) {
    var projects = _findProjects(Path.GetFullPath(path));
    if (projects.Count == 0) {
      throw new AuditCheckException($"No project found in {path}. Pass --project with a project, solution or directory.");
    }

    return [.. projects
      .SelectMany(_readPackages)
      .Distinct()
      .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
      .ThenBy(p => p.Version, Comparer<string>.Create(NuGetVersionOrder.Compare))];
  }

  private static List<string> _findProjects(string path) {
    if (Directory.Exists(path)) {
      return [.. Directory
        .EnumerateFiles(path, "*.*proj", SearchOption.AllDirectories)
        .Where(_isProjectFile)
        .Order(StringComparer.Ordinal)];
    }

    if (!File.Exists(path)) {
      throw new AuditCheckException($"{path} does not exist.");
    }

    return Path.GetExtension(path).ToUpperInvariant() switch {
      ".SLN" => _resolveListed(path, _slnProjectPaths(File.ReadAllText(path))),
      ".SLNX" => _resolveListed(path, _slnxProjectPaths(path)),
      _ when _isProjectFile(path) => [path],
      _ => throw new AuditCheckException($"{path} is not a project file, a .sln or .slnx solution, or a directory."),
    };
  }

  private static bool _isProjectFile(string path) =>
    Path.GetExtension(path).ToUpperInvariant() is ".CSPROJ" or ".FSPROJ" or ".VBPROJ";

  private static IEnumerable<string> _slnProjectPaths(string solution) =>
    _slnProjectLine().Matches(solution).Select(m => m.Groups["path"].Value);

  private static IEnumerable<string> _slnxProjectPaths(string solutionPath) {
    try {
      return [.. XDocument.Load(solutionPath).Descendants("Project").SelectMany(p => p.Attributes("Path")).Select(a => a.Value)];
    } catch (XmlException ex) {
      throw new AuditCheckException($"{solutionPath} could not be read as a solution: {ex.Message}", ex);
    }
  }

  // Solution files write paths relative to themselves, with backslashes on every platform.
  // Solution folders appear as entries too, with a bare name for a path; they are not projects.
  private static List<string> _resolveListed(string solutionPath, IEnumerable<string> listed) {
    var directory = Path.GetDirectoryName(solutionPath)!;
    return [.. listed
      .Where(_isProjectFile)
      .Select(p => Path.GetFullPath(Path.Combine(directory, p.Replace('\\', Path.DirectorySeparatorChar))))];
  }

  private static IEnumerable<ResolvedPackage> _readPackages(string projectPath) {
    var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
    if (!File.Exists(assetsPath)) {
      throw new AuditCheckException(
        $"{projectPath} has no restore output ({assetsPath}). Run dotnet restore first, then audit again.");
    }

    ProjectAssetsFile? assets;
    try {
      using var stream = File.OpenRead(assetsPath);
      assets = JsonSerializer.Deserialize(stream, AuditJsonContext.Default.ProjectAssetsFile);
    } catch (JsonException ex) {
      throw new AuditCheckException($"{assetsPath} could not be read as restore output: {ex.Message}", ex);
    } catch (IOException ex) {
      throw new AuditCheckException($"{assetsPath} could not be read: {ex.Message}", ex);
    }

    if (assets is null) {
      throw new AuditCheckException($"{assetsPath} could not be read as restore output: it is empty.");
    }

    // Keys are "id/version". A package id cannot contain '/', so the first one separates them.
    return assets.Targets.Values
      .SelectMany(target => target)
      .Where(entry => entry.Value.Type == "package"
        && entry.Key.StartsWith(PACKAGE_PREFIX, StringComparison.OrdinalIgnoreCase))
      .Select(entry => entry.Key.Split('/', 2))
      .Select(parts => new ResolvedPackage(parts[0], parts[1]));
  }

  [GeneratedRegex(""""^Project\("\{[^}]*\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"""", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
  private static partial Regex _slnProjectLine();
}
