using System.Xml.Linq;
using Whizbang.Migrate.PackageManagement;

namespace Whizbang.Migrate.Tests.PackageManagement;

/// <summary>
/// Tests for the package reference migration.
/// </summary>
/// <remarks>
/// This rewrites project files, so its mistakes surface as a solution that no longer restores
/// or builds. Two shapes matter most: a version attribute emitted under Central Package
/// Management is an outright NU1008 build failure, and a package the operator explicitly asked
/// to keep must survive -- that setting is the only override they have.
/// </remarks>
/// <tests>Whizbang.Migrate/PackageManagement/PackageManager.cs:*</tests>
public class PackageManagerTests {

  private sealed class TempSolution : IDisposable {
    public string Root { get; }
    public TempSolution() {
      Root = Path.Combine(Path.GetTempPath(), "whizbang-packages", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(Root);
    }
    public string AddProject(string name, params string[] packageIncludes) {
      var refs = string.Join("\n    ", packageIncludes.Select(p =>
        $"""<PackageReference Include="{p}" Version="7.0.0" />"""));
      var path = Path.Combine(Root, $"{name}.csproj");
      File.WriteAllText(path, $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            {refs}
          </ItemGroup>
        </Project>
        """);
      return path;
    }
    /// <summary>Enables CPM with the given packages already carrying central versions.</summary>
    public void EnableCentralPackageManagement(params string[] packageVersions) {
      var entries = string.Join("\n    ", packageVersions.Select(p =>
        $"""<PackageVersion Include="{p}" Version="7.0.0" />"""));
      File.WriteAllText(Path.Combine(Root, "Directory.Packages.props"), $"""
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            {entries}
          </ItemGroup>
        </Project>
        """);
    }

    public void EnableCentralPackageManagement() {
      File.WriteAllText(Path.Combine(Root, "Directory.Packages.props"), """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
          </ItemGroup>
        </Project>
        """);
    }
    public void Dispose() {
      if (Directory.Exists(Root)) { Directory.Delete(Root, recursive: true); }
    }
  }

  private static List<string> _packageRefs(string projectPath) {
    var doc = XDocument.Load(projectPath);
    return [.. doc.Root!.Elements("ItemGroup")
      .SelectMany(g => g.Elements("PackageReference"))
      .Select(e => e.Attribute("Include")!.Value)];
  }

  private static string? _versionOf(string projectPath, string package) {
    var doc = XDocument.Load(projectPath);
    return doc.Root!.Elements("ItemGroup")
      .SelectMany(g => g.Elements("PackageReference"))
      .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, package, StringComparison.OrdinalIgnoreCase))
      ?.Attribute("Version")?.Value;
  }

  [Test]
  public async Task UpdatePackagesAsync_ReplacesMartenWithItsWhizbangEquivalentAsync() {
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten");

    var result = await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var refs = _packageRefs(project);
    await Assert.That(result.Success).IsTrue();
    await Assert.That(refs).DoesNotContain("Marten");
    await Assert.That(refs).Contains("SoftwareExtravaganza.Whizbang.Data.Postgres");
  }

  [Test]
  public async Task UpdatePackagesAsync_UnderCentralPackageManagement_OmitsTheVersionAttributeAsync() {
    // A PackageReference carrying an inline Version under CPM is NU1008: the solution stops
    // restoring outright. This is the difference between a migrated project and a broken one.
    using var sln = new TempSolution();
    sln.EnableCentralPackageManagement();
    var project = sln.AddProject("OrderService", "Wolverine");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    await Assert.That(_packageRefs(project)).Contains("SoftwareExtravaganza.Whizbang.Core");
    await Assert.That(_versionOf(project, "SoftwareExtravaganza.Whizbang.Core")).IsNull()
      .Because("central package management forbids inline versions -- emitting one fails restore");
  }

  [Test]
  public async Task UpdatePackagesAsync_WithoutCentralManagement_StampsTheConfiguredVersionAsync() {
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Wolverine");

    await PackageManager.UpdatePackagesAsync(
      sln.Root, [project], new PackageSettings { WhizbangVersion = "2.3.4" });

    await Assert.That(_versionOf(project, "SoftwareExtravaganza.Whizbang.Core")).IsEqualTo("2.3.4");
  }

  [Test]
  public async Task UpdatePackagesAsync_PreservePackages_KeepsAPackageTheOperatorAskedToKeepAsync() {
    // The operator's only override. It is collected by the wizard, written to the decision file
    // and passed in here -- and was ignored, so the package was removed anyway while the tool
    // reported the setting as supported.
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings {
      RemoveOldPackages = true,
      PreservePackages = ["Marten"],
    });

    await Assert.That(_packageRefs(project)).Contains("Marten")
      .Because("an explicit preserve has to outrank the blanket removal, or the setting is a lie");
  }

  [Test]
  public async Task UpdatePackagesAsync_PreservePackages_MatchesCaseInsensitivelyAsync() {
    // NuGet ids are case-insensitive and operators type them by hand, so a case mismatch must
    // not quietly turn a preserve into a removal.
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings {
      PreservePackages = ["marten"],
    });

    await Assert.That(_packageRefs(project)).Contains("Marten");
  }

  [Test]
  public async Task UpdatePackagesAsync_PreservePackages_AlsoProtectsRemoveOnlyPackagesAsync() {
    // Packages with no replacement are removed outright, which is exactly the case where an
    // operator is most likely to want an exception.
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten.CommandLine");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings {
      PreservePackages = ["Marten.CommandLine"],
    });

    await Assert.That(_packageRefs(project)).Contains("Marten.CommandLine");
  }

  [Test]
  public async Task UpdatePackagesAsync_RemoveOldPackagesDisabled_LeavesTheOldReferenceInPlaceAsync() {
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(
      sln.Root, [project], new PackageSettings { RemoveOldPackages = false });

    await Assert.That(_packageRefs(project)).Contains("Marten");
  }

  [Test]
  public async Task UpdatePackagesAsync_PackageWithNoEquivalent_IsRemovedWithoutAReplacementAsync() {
    // Marten.CommandLine has no Whizbang counterpart. Inventing one would add a reference that
    // cannot restore.
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten.CommandLine");

    var result = await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var refs = _packageRefs(project);
    await Assert.That(refs).DoesNotContain("Marten.CommandLine");
    await Assert.That(refs.Any(r => r.StartsWith("SoftwareExtravaganza.", StringComparison.Ordinal))).IsFalse();
    await Assert.That(result.Changes.Any(c => c.ChangeType == PackageChangeType.Removed)).IsTrue();
  }

  [Test]
  public async Task UpdatePackagesAsync_EveryAddedPackageUsesThePublishedPrefixAsync() {
    // The replacement ids are bare strings that nothing else checks. A wrong prefix produces a
    // reference that cannot restore, and it would look perfectly reasonable in a diff.
    using var sln = new TempSolution();
    var project = sln.AddProject(
      "OrderService", "Marten", "Wolverine.RabbitMQ", "WolverineFx.Http", "HotChocolate.Data.Marten");

    var result = await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var added = result.Changes.Where(c => c.ChangeType == PackageChangeType.Added).ToList();
    await Assert.That(added.Count).IsGreaterThanOrEqualTo(1);
    await Assert.That(added.All(c => c.PackageName.StartsWith("SoftwareExtravaganza.Whizbang.", StringComparison.Ordinal)))
      .IsTrue()
      .Because("published Whizbang packages carry that prefix; anything else will not resolve");
  }

  [Test]
  public async Task UpdatePackagesAsync_RunTwice_DoesNotDuplicateTheAddedReferenceAsync() {
    // Migrations get re-run. A second pass that appends the same PackageReference again produces
    // a project file NuGet rejects.
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());
    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var refs = _packageRefs(project);
    await Assert.That(refs.Count(r => r == "SoftwareExtravaganza.Whizbang.Data.Postgres")).IsEqualTo(1);
  }

  [Test]
  public async Task UpdatePackagesAsync_ReferencesSpreadAcrossItemGroups_AreAllProcessedAsync() {
    // Real project files split references across several ItemGroups, often conditioned. Handling
    // only the first would leave half the old packages behind.
    using var sln = new TempSolution();
    var path = Path.Combine(sln.Root, "Split.csproj");
    File.WriteAllText(path, """
      <Project Sdk="Microsoft.NET.Sdk">
        <ItemGroup>
          <PackageReference Include="Marten" Version="7.0.0" />
        </ItemGroup>
        <ItemGroup>
          <PackageReference Include="Wolverine.RabbitMQ" Version="3.0.0" />
        </ItemGroup>
      </Project>
      """);

    await PackageManager.UpdatePackagesAsync(sln.Root, [path], new PackageSettings());

    var refs = _packageRefs(path);
    await Assert.That(refs).DoesNotContain("Marten");
    await Assert.That(refs).DoesNotContain("Wolverine.RabbitMQ");
    await Assert.That(refs).Contains("SoftwareExtravaganza.Whizbang.Transports.RabbitMQ");
  }

  [Test]
  public async Task UpdatePackagesAsync_UnrelatedPackages_AreLeftAloneAsync() {
    using var sln = new TempSolution();
    var project = sln.AddProject("OrderService", "Marten", "Serilog");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    await Assert.That(_packageRefs(project)).Contains("Serilog")
      .Because("only Marten/Wolverine packages are this migration's concern");
  }

  // ── Central Package Management: the props file half of the contract ────────

  private static void _seedPackagesProps(TempSolution sln, params string[] packageVersions) {
    var entries = string.Join("\n    ", packageVersions.Select(p =>
      $"""<PackageVersion Include="{p}" Version="7.0.0" />"""));
    File.WriteAllText(Path.Combine(sln.Root, "Directory.Packages.props"), $"""
      <Project>
        <PropertyGroup>
          <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
        </PropertyGroup>
        <ItemGroup>
          {entries}
        </ItemGroup>
      </Project>
      """);
  }

  private static List<(string Include, string? Version)> _packageVersions(string propsPath) {
    var doc = XDocument.Load(propsPath);
    return [.. doc.Root!.Elements("ItemGroup")
      .SelectMany(g => g.Elements("PackageVersion"))
      .Select(e => (e.Attribute("Include")!.Value, e.Attribute("Version")?.Value))];
  }

  [Test]
  public async Task UpdatePackagesAsync_UnderCpm_AddsThePackageVersionEntryAsync() {
    // Under central package management a PackageReference carries no version, so the matching
    // PackageVersion in Directory.Packages.props is the only thing that gives it one. Without
    // it restore fails with NU1010 -- the project references a package with no version at all.
    using var sln = new TempSolution();
    _seedPackagesProps(sln, "Marten");
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var versions = _packageVersions(Path.Combine(sln.Root, "Directory.Packages.props"));
    var added = versions.FirstOrDefault(v => v.Include == "SoftwareExtravaganza.Whizbang.Data.Postgres");
    await Assert.That(added.Include).IsNotNull()
      .Because("a CPM reference without its PackageVersion cannot restore");
    await Assert.That(added.Version).IsNotNull();
  }

  [Test]
  public async Task UpdatePackagesAsync_UnderCpm_RemovesTheOldPackageVersionAsync() {
    // Leaving the old entry behind keeps a dependency on the package the migration exists to
    // remove, and it will still restore -- so nothing surfaces the leftover.
    using var sln = new TempSolution();
    _seedPackagesProps(sln, "Marten");
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var includes = _packageVersions(Path.Combine(sln.Root, "Directory.Packages.props"))
      .Select(v => v.Include).ToList();
    await Assert.That(includes).DoesNotContain("Marten");
  }

  [Test]
  public async Task UpdatePackagesAsync_UnderCpm_PreservePackages_KeepsThePackageVersionAsync() {
    // The half of preserve that was missing. CPM splits a reference across two files, so
    // honoring the operator's choice in the project file while deleting the version entry
    // leaves the preserved package with no version -- NU1010, a build failure, and a worse
    // outcome than not preserving it at all.
    using var sln = new TempSolution();
    _seedPackagesProps(sln, "Marten");
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings {
      PreservePackages = ["Marten"],
    });

    var includes = _packageVersions(Path.Combine(sln.Root, "Directory.Packages.props"))
      .Select(v => v.Include).ToList();
    await Assert.That(includes).Contains("Marten")
      .Because("a preserved package must keep the version entry that makes it resolvable");
    await Assert.That(_packageRefs(project)).Contains("Marten")
      .Because("and the reference itself, so the two halves stay consistent");
  }

  [Test]
  public async Task UpdatePackagesAsync_UnderCpm_RunTwice_DoesNotDuplicateThePackageVersionAsync() {
    // Migrations get re-run; two PackageVersion entries for one package is a restore error.
    using var sln = new TempSolution();
    _seedPackagesProps(sln, "Marten");
    var project = sln.AddProject("OrderService", "Marten");

    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());
    await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    var includes = _packageVersions(Path.Combine(sln.Root, "Directory.Packages.props"))
      .Select(v => v.Include).ToList();
    await Assert.That(includes.Count(i => i == "SoftwareExtravaganza.Whizbang.Data.Postgres")).IsEqualTo(1);
  }


  [Test]
  public async Task UpdatePackagesAsync_APackageWithNoEquivalent_IsRemovedFromCentralVersionsAsync() {
    // Some Wolverine packages have no Whizbang counterpart at all. Leaving their central version
    // entry behind after the references are gone is dead configuration that outlives the
    // migration, and the operator has no way to tell it from a package still in use — the whole
    // point of the change list is that a dropped package is reported as dropped rather than
    // silently kept.
    using var sln = new TempSolution();
    sln.EnableCentralPackageManagement("Wolverine.FluentValidation");
    var project = sln.AddProject("OrderService", "Wolverine.FluentValidation");

    var result = await PackageManager.UpdatePackagesAsync(sln.Root, [project], new PackageSettings());

    await Assert.That(result.Success).IsTrue();
    var remaining = _packageVersions(Path.Combine(sln.Root, "Directory.Packages.props"))
      .Select(v => v.Include).ToList();
    await Assert.That(remaining).DoesNotContain("Wolverine.FluentValidation")
      .Because("nothing replaces it, so the central version entry has no reason to survive the "
             + "reference that used it");
    await Assert.That(result.Changes.Any(c =>
        c.PackageName == "Wolverine.FluentValidation" && c.ChangeType == PackageChangeType.Removed))
      .IsTrue()
      .Because("a package dropped with no replacement is exactly the change an operator needs "
             + "reported, because nothing in the migrated solution will mention it again");
  }

  [Test]
  public async Task UpdatePackagesAsync_GeneratorProjects_AreLeftAloneAsync() {
    // Source generators target netstandard2.0 and reference Roslyn, not the runtime packages.
    // Adding Whizbang references to one does not migrate it — it stops it building, and the
    // failure lands in a project the author never edited.
    using var sln = new TempSolution();
    var generator = sln.AddProject("OrderService.Generators", "Wolverine");

    var result = await PackageManager.UpdatePackagesAsync(sln.Root, [generator], new PackageSettings());

    await Assert.That(result.Success).IsTrue();
    await Assert.That(_packageRefs(generator)).Contains("Wolverine")
      .Because("the generator project is skipped whole, so even its stale reference is left for "
             + "the author rather than rewritten by a pass that does not understand it");
    await Assert.That(result.Changes.Any(c => c.FilePath == generator)).IsFalse()
      .Because("reporting a change to a project that was deliberately skipped would send the "
             + "author looking for an edit that is not there");
  }
}
