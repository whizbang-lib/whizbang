using System.Xml.Linq;

namespace Whizbang.Migrate.PackageManagement;

/// <summary>
/// Manages NuGet package references during migration.
/// Handles both Central Package Management (Directory.Packages.props) and traditional PackageReference.
/// </summary>
/// <docs>migration-guide/automated-migration</docs>
public sealed class PackageManager {
  /// <summary>
  /// Package mappings from old Marten/Wolverine packages to Whizbang equivalents.
  /// </summary>
  private static readonly Dictionary<string, string?> _packageMappings = new(StringComparer.OrdinalIgnoreCase) {
    // Marten packages -> SoftwareExtravaganza.Whizbang.Postgres
    ["Marten"] = "SoftwareExtravaganza.Whizbang.Postgres",
    ["Marten.Events"] = "SoftwareExtravaganza.Whizbang.Core",
    ["Marten.AspNetCore"] = null, // Remove, no equivalent needed

    // Wolverine packages -> SoftwareExtravaganza.Whizbang.Core
    // Note: NuGet packages use "WolverineFx" prefix, but some projects use "Wolverine" prefix
    ["WolverineFx"] = "SoftwareExtravaganza.Whizbang.Core",
    ["Wolverine"] = "SoftwareExtravaganza.Whizbang.Core",
    ["WolverineFx.Marten"] = "SoftwareExtravaganza.Whizbang.Postgres",
    ["Wolverine.Marten"] = "SoftwareExtravaganza.Whizbang.Postgres",
    ["WolverineFx.RabbitMQ"] = "SoftwareExtravaganza.Whizbang.Transports.RabbitMQ",
    ["Wolverine.RabbitMQ"] = "SoftwareExtravaganza.Whizbang.Transports.RabbitMQ",
    ["WolverineFx.AzureServiceBus"] = "SoftwareExtravaganza.Whizbang.Transports.AzureServiceBus",
    ["Wolverine.AzureServiceBus"] = "SoftwareExtravaganza.Whizbang.Transports.AzureServiceBus",
    ["WolverineFx.Kafka"] = "SoftwareExtravaganza.Whizbang.Transports.Kafka",
    ["Wolverine.Kafka"] = "SoftwareExtravaganza.Whizbang.Transports.Kafka"
  };

  /// <summary>
  /// Packages that should be removed but have no replacement.
  /// </summary>
  private static readonly HashSet<string> _packagesToRemove = new(StringComparer.OrdinalIgnoreCase) {
    "Marten.CommandLine",
    "Marten.PLv8",
    "Marten.NodaTime",
    "Wolverine.Http",
    "WolverineFx.Http",
    "Wolverine.FluentValidation",
    "WolverineFx.FluentValidation"
  };

  /// <summary>
  /// Updates package references for all projects that had files transformed.
  /// </summary>
  /// <param name="solutionRoot">Root directory containing the solution.</param>
  /// <param name="transformedFiles">List of files that were transformed.</param>
  /// <param name="settings">Package management settings.</param>
  /// <param name="ct">Cancellation token.</param>
  /// <returns>Result of the package management operation.</returns>
  public static async Task<PackageResult> UpdatePackagesAsync(
      string solutionRoot,
      IReadOnlyList<string> transformedFiles,
      PackageSettings settings,
      CancellationToken ct = default) {
    var changes = new List<PackageChange>();
    var warnings = new List<string>();

    // Find all unique projects containing transformed files
    var transformedProjects = _findTransformedProjects(transformedFiles);

    // Check for Central Package Management (search upward from source directory)
    var directoryPackagesProps = _findDirectoryPackagesProps(solutionRoot);
    var usesCpm = directoryPackagesProps != null;

    if (usesCpm && directoryPackagesProps != null) {
      // Update Directory.Packages.props with version entries
      var cpmChanges = await _updateDirectoryPackagesPropsAsync(
          directoryPackagesProps,
          settings,
          ct);
      changes.AddRange(cpmChanges);
    }

    // Find ALL projects that might have old packages (not just transformed ones)
    // This ensures we remove old package references even from projects without transformed code
    var allProjects = _findAllProjectsWithOldPackages(solutionRoot);
    var projectsToProcess = new HashSet<string>(transformedProjects, StringComparer.OrdinalIgnoreCase);
    foreach (var proj in allProjects) {
      projectsToProcess.Add(proj);
    }

    if (projectsToProcess.Count == 0) {
      return new PackageResult(true, changes, warnings);
    }

    // Update each project
    foreach (var projectPath in projectsToProcess) {
      ct.ThrowIfCancellationRequested();

      if (!File.Exists(projectPath)) {
        warnings.Add($"Project file not found: {projectPath}");
        continue;
      }

      // Skip generator projects
      if (projectPath.Contains(".Generators", StringComparison.OrdinalIgnoreCase) ||
          projectPath.Contains("Generator", StringComparison.OrdinalIgnoreCase)) {
        continue;
      }

      var projectChanges = await _updateProjectFileAsync(
          projectPath,
          usesCpm,
          settings,
          ct);
      changes.AddRange(projectChanges);
    }

    return new PackageResult(true, changes, warnings);
  }

  /// <summary>
  /// Finds all .csproj files containing the transformed files.
  /// </summary>
  private static HashSet<string> _findTransformedProjects(IReadOnlyList<string> transformedFiles) {
    var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var file in transformedFiles) {
      var directory = Path.GetDirectoryName(file);
      while (!string.IsNullOrEmpty(directory)) {
        var csprojFiles = Directory.GetFiles(directory, "*.csproj");
        if (csprojFiles.Length > 0) {
          projects.Add(csprojFiles[0]);
          break;
        }
        directory = Path.GetDirectoryName(directory);
      }
    }

    return projects;
  }

  /// <summary>
  /// Finds all .csproj files that reference old Marten/Wolverine packages.
  /// </summary>
  private static HashSet<string> _findAllProjectsWithOldPackages(string sourceRoot) {
    var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var oldPackageNames = _packageMappings.Keys
        .Concat(_packagesToRemove)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    try {
      var csprojFiles = Directory.GetFiles(sourceRoot, "*.csproj", SearchOption.AllDirectories);
      foreach (var csproj in csprojFiles) {
        var content = File.ReadAllText(csproj);
        // Quick check if file contains any old package references
        foreach (var oldPackage in oldPackageNames) {
          if (content.Contains($"Include=\"{oldPackage}\"", StringComparison.OrdinalIgnoreCase)) {
            projects.Add(csproj);
            break;
          }
        }
      }
    } catch {
      // Ignore errors scanning for projects
    }

    return projects;
  }

  /// <summary>
  /// Finds Directory.Packages.props by searching upward from the given path.
  /// </summary>
  private static string? _findDirectoryPackagesProps(string startPath) {
    var directory = startPath;
    while (!string.IsNullOrEmpty(directory)) {
      var propsPath = Path.Combine(directory, "Directory.Packages.props");
      if (File.Exists(propsPath)) {
        return propsPath;
      }
      directory = Path.GetDirectoryName(directory);
    }
    return null;
  }

  /// <summary>
  /// Updates Directory.Packages.props for Central Package Management.
  /// </summary>
  private static async Task<List<PackageChange>> _updateDirectoryPackagesPropsAsync(
      string propsPath,
      PackageSettings settings,
      CancellationToken ct) {
    var changes = new List<PackageChange>();

    var content = await File.ReadAllTextAsync(propsPath, ct);
    var doc = XDocument.Parse(content);
    var root = doc.Root;
    if (root == null) {
      return changes;
    }

    // Find or create ItemGroup for PackageVersion entries
    var itemGroup = root.Elements("ItemGroup")
        .FirstOrDefault(ig => ig.Elements("PackageVersion").Any())
        ?? _getOrCreateItemGroup(root);

    var packagesToAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var existingPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Collect existing PackageVersion entries
    foreach (var pv in itemGroup.Elements("PackageVersion").ToList()) {
      var include = pv.Attribute("Include")?.Value;
      if (string.IsNullOrEmpty(include)) {
        continue;
      }

      existingPackages.Add(include);

      // Check if this is a package to remove/replace
      if (_packageMappings.TryGetValue(include, out var replacement)) {
        if (settings.RemoveOldPackages) {
          pv.Remove();
          changes.Add(new PackageChange(
              propsPath,
              PackageChangeType.Removed,
              include,
              null,
              "Removed from Directory.Packages.props"));
        }

        if (!string.IsNullOrEmpty(replacement)) {
          packagesToAdd.Add(replacement);
        }
      } else if (_packagesToRemove.Contains(include) && settings.RemoveOldPackages) {
        pv.Remove();
        changes.Add(new PackageChange(
            propsPath,
            PackageChangeType.Removed,
            include,
            null,
            "Removed from Directory.Packages.props (no replacement)"));
      }
    }

    // Add new Whizbang packages
    foreach (var package in packagesToAdd) {
      if (existingPackages.Contains(package)) {
        continue;
      }

      var newElement = new XElement("PackageVersion",
          new XAttribute("Include", package),
          new XAttribute("Version", settings.WhizbangVersion));
      itemGroup.Add(newElement);

      changes.Add(new PackageChange(
          propsPath,
          PackageChangeType.Added,
          package,
          settings.WhizbangVersion,
          "Added to Directory.Packages.props"));
    }

    // Save if there were changes
    if (changes.Count > 0) {
      await File.WriteAllTextAsync(propsPath, doc.ToString(), ct);
    }

    return changes;
  }

  /// <summary>
  /// Updates a single project file.
  /// </summary>
  private static async Task<List<PackageChange>> _updateProjectFileAsync(
      string projectPath,
      bool usesCpm,
      PackageSettings settings,
      CancellationToken ct) {
    var changes = new List<PackageChange>();

    var content = await File.ReadAllTextAsync(projectPath, ct);
    var doc = XDocument.Parse(content);
    var root = doc.Root;
    if (root == null) {
      return changes;
    }

    // Find ALL ItemGroups with PackageReference entries (there can be multiple)
    var itemGroups = root.Elements("ItemGroup")
        .Where(ig => ig.Elements("PackageReference").Any())
        .ToList();

    var packagesToAdd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var existingPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Process existing PackageReference entries in ALL ItemGroups
    foreach (var itemGroup in itemGroups) {
      foreach (var pr in itemGroup.Elements("PackageReference").ToList()) {
        var include = pr.Attribute("Include")?.Value;
        if (string.IsNullOrEmpty(include)) {
          continue;
        }

        existingPackages.Add(include);

        // Check if this is a package to remove/replace
        if (_packageMappings.TryGetValue(include, out var replacement)) {
          if (settings.RemoveOldPackages) {
            pr.Remove();
            changes.Add(new PackageChange(
                projectPath,
                PackageChangeType.Removed,
                include,
                null,
                "Removed PackageReference"));
          }

          if (!string.IsNullOrEmpty(replacement)) {
            packagesToAdd.Add(replacement);
          }
        } else if (_packagesToRemove.Contains(include) && settings.RemoveOldPackages) {
          pr.Remove();
          changes.Add(new PackageChange(
              projectPath,
              PackageChangeType.Removed,
              include,
              null,
              "Removed PackageReference (no replacement)"));
        }
      }
    }

    // Add new Whizbang packages to the first ItemGroup (or create one)
    var targetItemGroup = itemGroups.FirstOrDefault() ?? _getOrCreateItemGroup(root);

    foreach (var package in packagesToAdd) {
      if (existingPackages.Contains(package)) {
        continue;
      }

      XElement newElement;
      if (usesCpm) {
        // CPM: Just include, no version
        newElement = new XElement("PackageReference",
            new XAttribute("Include", package));
      } else {
        // Traditional: Include with version
        newElement = new XElement("PackageReference",
            new XAttribute("Include", package),
            new XAttribute("Version", settings.WhizbangVersion));
      }

      targetItemGroup.Add(newElement);

      changes.Add(new PackageChange(
          projectPath,
          PackageChangeType.Added,
          package,
          usesCpm ? null : settings.WhizbangVersion,
          usesCpm ? "Added PackageReference (version from CPM)" : "Added PackageReference"));
    }

    // Save if there were changes
    if (changes.Count > 0) {
      await File.WriteAllTextAsync(projectPath, doc.ToString(), ct);
    }

    return changes;
  }

  /// <summary>
  /// Gets or creates an ItemGroup element.
  /// </summary>
  private static XElement _getOrCreateItemGroup(XElement root) {
    var itemGroup = root.Element("ItemGroup");
    if (itemGroup == null) {
      itemGroup = new XElement("ItemGroup");
      root.Add(itemGroup);
    }
    return itemGroup;
  }
}

/// <summary>
/// Settings for package management during migration.
/// </summary>
public sealed class PackageSettings {
  /// <summary>
  /// Whether to automatically manage packages. Default: true.
  /// </summary>
  public bool AutoManage { get; set; } = true;

  /// <summary>
  /// Version of Whizbang packages to use.
  /// </summary>
  public string WhizbangVersion { get; set; } = "1.0.0";

  /// <summary>
  /// Whether to remove old Marten/Wolverine packages. Default: true.
  /// </summary>
  public bool RemoveOldPackages { get; set; } = true;

  /// <summary>
  /// Packages to preserve (don't remove even if Marten/Wolverine).
  /// </summary>
  public List<string> PreservePackages { get; set; } = [];
}

/// <summary>
/// Result of package management operation.
/// </summary>
/// <param name="Success">Whether the operation succeeded.</param>
/// <param name="Changes">List of package changes made.</param>
/// <param name="Warnings">Any warnings encountered.</param>
public sealed record PackageResult(
    bool Success,
    IReadOnlyList<PackageChange> Changes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Represents a single package change.
/// </summary>
/// <param name="FilePath">Path to the file that was modified.</param>
/// <param name="ChangeType">Type of change made.</param>
/// <param name="PackageName">Name of the package.</param>
/// <param name="Version">Version of the package (null for CPM or removals).</param>
/// <param name="Description">Description of the change.</param>
public sealed record PackageChange(
    string FilePath,
    PackageChangeType ChangeType,
    string PackageName,
    string? Version,
    string Description);

/// <summary>
/// Type of package change.
/// </summary>
public enum PackageChangeType {
  /// <summary>
  /// Package was added.
  /// </summary>
  Added,

  /// <summary>
  /// Package was removed.
  /// </summary>
  Removed,

  /// <summary>
  /// Package version was updated.
  /// </summary>
  Updated
}
