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
}
