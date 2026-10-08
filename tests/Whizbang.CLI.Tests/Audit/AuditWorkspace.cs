// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// A throwaway directory laid out like a restored solution: project files with the
/// <c>obj/project.assets.json</c> restore writes next to them.
/// </summary>
internal sealed class AuditWorkspace : IDisposable {
  public AuditWorkspace() {
    Root = Directory.CreateTempSubdirectory("whizbang-audit-").FullName;
  }

  public string Root { get; }

  /// <summary>
  /// Writes <c>relativeDirectory/name.csproj</c> and, unless <paramref name="assetsJson"/> is null,
  /// its restore output.
  /// </summary>
  /// <returns>The full path of the project file.</returns>
  public string AddProject(string relativeDirectory, string name, string? assetsJson) {
    var directory = Path.Combine(Root, relativeDirectory);
    Directory.CreateDirectory(Path.Combine(directory, "obj"));
    var projectPath = Path.Combine(directory, name + ".csproj");
    File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    if (assetsJson is not null) {
      File.WriteAllText(AssetsPath(projectPath), assetsJson);
    }
    return projectPath;
  }

  /// <summary>Writes a file at <paramref name="relativePath"/> and returns its full path.</summary>
  public string AddFile(string relativePath, string content) {
    var path = Path.Combine(Root, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content);
    return path;
  }

  public static string AssetsPath(string projectPath) =>
    Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");

  /// <summary>A project.assets.json from a real restore, copied to the test output.</summary>
  public static string Fixture(string name) =>
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Audit", "Fixtures", name + ".project.assets.json"));

  /// <summary>
  /// A minimal assets file in the restore shape: one target per framework, each listing
  /// <c>id/version</c> entries with their type.
  /// </summary>
  public static string Assets(params (string Framework, string Key, string Type)[] entries) {
    var targets = entries
      .GroupBy(e => e.Framework)
      .Select(g => $"\"{g.Key}\": {{ {string.Join(", ", g.Select(e => $"\"{e.Key}\": {{ \"type\": \"{e.Type}\" }}"))} }}");
    return $"{{ \"version\": 3, \"targets\": {{ {string.Join(", ", targets)} }} }}";
  }

  public void Dispose() {
    Directory.Delete(Root, recursive: true);
  }
}
