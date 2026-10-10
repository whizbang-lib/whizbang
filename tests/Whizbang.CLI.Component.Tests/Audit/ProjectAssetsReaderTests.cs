// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Whizbang.CLI.Audit;

namespace Whizbang.CLI.Tests.Audit;

/// <summary>
/// Tests for <see cref="ProjectAssetsReader"/>: which Whizbang package versions a project, solution
/// or directory resolves, read from restore output built from a real <c>dotnet restore</c>.
/// </summary>
/// <tests>Whizbang.CLI/Audit/ProjectAssetsReader.cs</tests>
public class ProjectAssetsReaderTests {
  private const string V2615 = "0.2615.0";

  private static ResolvedPackage _whizbang(string name, string version) =>
    new(ProjectAssetsReader.PACKAGE_PREFIX + name, version);

  [Test]
  public async Task ReadWhizbangPackages_Project_IncludesTransitivePackagesAsync() {
    // The sample references only Data.EFCore.Postgres. Core, Data.Postgres, Data.Schema and
    // Data.EFCore.Custom arrive transitively, and an advisory against any of them still ships in
    // the build. Reading only direct references would miss four of the five.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(project);

    await Assert.That(packages).IsEquivalentTo([
      _whizbang("Core", V2615),
      _whizbang("Data.EFCore.Custom", V2615),
      _whizbang("Data.EFCore.Postgres", V2615),
      _whizbang("Data.Postgres", V2615),
      _whizbang("Data.Schema", V2615),
    ]);
  }

  [Test]
  public async Task ReadWhizbangPackages_Project_LeavesOutEveryOtherPackageAsync() {
    // The sample also resolves Humanizer.Core and the EF Core and Npgsql packages. NuGet Audit
    // already covers those; this command answers only for Whizbang.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(project);

    await Assert.That(packages.All(p => p.Id.StartsWith(ProjectAssetsReader.PACKAGE_PREFIX, StringComparison.Ordinal))).IsTrue();
  }

  [Test]
  public async Task ReadWhizbangPackages_ProjectReferenceNamedLikeAPackage_IsNotAPackageAsync() {
    // A solution that builds Whizbang from source lists those projects in the assets file with
    // type "project". No published advisory can describe a local build, so they are not queried.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", AuditWorkspace.Assets(
      ("net10.0", "SoftwareExtravaganza.Whizbang.Core/1.0.0", "package"),
      ("net10.0", "SoftwareExtravaganza.Whizbang.Local/1.0.0", "project")));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(project);

    await Assert.That(packages).IsEquivalentTo([_whizbang("Core", "1.0.0")]);
  }

  [Test]
  public async Task ReadWhizbangPackages_MultiTargetedProject_ReadsEveryFrameworkAsync() {
    // Each target framework resolves on its own, and they can land on different versions.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", AuditWorkspace.Assets(
      ("net9.0", "SoftwareExtravaganza.Whizbang.Core/1.0.0", "package"),
      ("net10.0", "SoftwareExtravaganza.Whizbang.Core/1.1.0", "package")));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(project);

    await Assert.That(packages).IsEquivalentTo([_whizbang("Core", "1.0.0"), _whizbang("Core", "1.1.0")]);
  }

  [Test]
  public async Task ReadWhizbangPackages_Directory_ReadsEveryProjectUnderItAsync() {
    // Two services pinning Core at different versions are two things to check, not one.
    using var workspace = new AuditWorkspace();
    workspace.AddProject("src/SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));
    workspace.AddProject("src/Worker", "Worker", AuditWorkspace.Fixture("Worker"));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(workspace.Root);

    await Assert.That(packages).Contains(_whizbang("Core", V2615));
    await Assert.That(packages).Contains(_whizbang("Core", "0.2610.0"));
    await Assert.That(packages.Count).IsEqualTo(6);
  }

  [Test]
  public async Task ReadWhizbangPackages_SameVersionInTwoProjects_IsListedOnceAsync() {
    using var workspace = new AuditWorkspace();
    workspace.AddProject("a", "A", AuditWorkspace.Fixture("Worker"));
    workspace.AddProject("b", "B", AuditWorkspace.Fixture("Worker"));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(workspace.Root);

    await Assert.That(packages).IsEquivalentTo([_whizbang("Core", "0.2610.0")]);
  }

  [Test]
  public async Task ReadWhizbangPackages_Results_AreOrderedByIdThenVersionAsync() {
    // A stable order keeps the report diffable between runs.
    using var workspace = new AuditWorkspace();
    workspace.AddProject("b", "B", AuditWorkspace.Fixture("SampleApp"));
    workspace.AddProject("a", "A", AuditWorkspace.Fixture("Worker"));

    var packages = ProjectAssetsReader.ReadWhizbangPackages(workspace.Root);

    await Assert.That(packages[0]).IsEqualTo(_whizbang("Core", "0.2610.0"));
    await Assert.That(packages[1]).IsEqualTo(_whizbang("Core", V2615));
    await Assert.That(packages[^1]).IsEqualTo(_whizbang("Data.Schema", V2615));
  }

  [Test]
  public async Task ReadWhizbangPackages_Sln_ReadsTheProjectsItListsAsync() {
    // .sln paths use backslashes on every platform, and solution folders appear as Project
    // lines whose "path" is just a name. Only real project files are read.
    using var workspace = new AuditWorkspace();
    workspace.AddProject("src/SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));
    workspace.AddProject("src/Worker", "Worker", AuditWorkspace.Fixture("Worker"));
    workspace.AddProject("unlisted", "Unlisted", "not read, so not parsed");
    var solution = workspace.AddFile("Product.sln", """
      Microsoft Visual Studio Solution File, Format Version 12.00
      Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{0D4C6B7E-0000-0000-0000-000000000001}"
      EndProject
      Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "SampleApp", "src\SampleApp\SampleApp.csproj", "{0D4C6B7E-0000-0000-0000-000000000002}"
      EndProject
      Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Worker", "src\Worker\Worker.csproj", "{0D4C6B7E-0000-0000-0000-000000000003}"
      EndProject
      """);

    var packages = ProjectAssetsReader.ReadWhizbangPackages(solution);

    await Assert.That(packages.Count).IsEqualTo(6);
  }

  [Test]
  public async Task ReadWhizbangPackages_Slnx_ReadsTheProjectsItListsAsync() {
    using var workspace = new AuditWorkspace();
    workspace.AddProject("src/SampleApp", "SampleApp", AuditWorkspace.Fixture("SampleApp"));
    workspace.AddProject("src/Worker", "Worker", AuditWorkspace.Fixture("Worker"));
    workspace.AddProject("unlisted", "Unlisted", "not read, so not parsed");
    var solution = workspace.AddFile("Product.slnx", """
      <Solution>
        <Folder Name="/src/">
          <Project Path="src/SampleApp/SampleApp.csproj" />
          <Project Path="src\Worker\Worker.csproj" />
        </Folder>
      </Solution>
      """);

    var packages = ProjectAssetsReader.ReadWhizbangPackages(solution);

    await Assert.That(packages.Count).IsEqualTo(6);
  }

  [Test]
  public async Task ReadWhizbangPackages_MalformedSlnx_FailsTheCheckAsync() {
    using var workspace = new AuditWorkspace();
    var solution = workspace.AddFile("Product.slnx", "<Solution><Project Path=");

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(solution));

    await Assert.That(exception.Message).Contains(solution);
  }

  [Test]
  public async Task ReadWhizbangPackages_NoAssetsFile_SaysToRestoreFirstAsync() {
    // Without restore output there is nothing to check. Reporting "no advisories" here would be
    // a clean bill of health for a build nobody looked at.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", assetsJson: null);

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(project));

    await Assert.That(exception.Message).Contains("dotnet restore");
    await Assert.That(exception.Message).Contains(project);
  }

  [Test]
  public async Task ReadWhizbangPackages_OneOfSeveralProjectsUnrestored_FailsTheWholeCheckAsync() {
    // Checking the restored half and staying quiet about the rest would report a partial
    // answer as a full one.
    using var workspace = new AuditWorkspace();
    workspace.AddProject("a", "A", AuditWorkspace.Fixture("Worker"));
    var unrestored = workspace.AddProject("b", "B", assetsJson: null);

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(workspace.Root));

    await Assert.That(exception.Message).Contains(unrestored);
  }

  [Test]
  [Arguments("{ this is not json")]
  [Arguments("null")]
  public async Task ReadWhizbangPackages_UnreadableAssetsFile_FailsTheCheckAsync(string content) {
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", content);

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(project));

    await Assert.That(exception.Message).Contains(AuditWorkspace.AssetsPath(project));
  }

  [Test]
  public async Task ReadWhizbangPackages_AssetsFileLockedByAnotherWriter_FailsTheCheckAsync() {
    // A restore still writing the file is a read that fails, which is a check that did not run.
    using var workspace = new AuditWorkspace();
    var project = workspace.AddProject("App", "App", AuditWorkspace.Fixture("Worker"));
    await using var writer = new FileStream(AuditWorkspace.AssetsPath(project), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(project));

    await Assert.That(exception.InnerException).IsTypeOf<IOException>();
  }

  [Test]
  public async Task ReadWhizbangPackages_MissingPath_FailsTheCheckAsync() {
    using var workspace = new AuditWorkspace();
    var missing = Path.Combine(workspace.Root, "nope");

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(missing));

    await Assert.That(exception.Message).Contains(missing);
  }

  [Test]
  public async Task ReadWhizbangPackages_FileThatIsNotAProjectOrSolution_FailsTheCheckAsync() {
    using var workspace = new AuditWorkspace();
    var notes = workspace.AddFile("notes.txt", "hello");

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(notes));

    await Assert.That(exception.Message).Contains("notes.txt");
  }

  [Test]
  public async Task ReadWhizbangPackages_DirectoryWithNoProjects_FailsTheCheckAsync() {
    // Pointing the command at the wrong folder must not read as "nothing to worry about".
    using var workspace = new AuditWorkspace();
    workspace.AddFile("README.md", "# nothing here");

    var exception = Assert.Throws<AuditCheckException>(() => ProjectAssetsReader.ReadWhizbangPackages(workspace.Root));

    await Assert.That(exception.Message).Contains("No project");
  }
}
