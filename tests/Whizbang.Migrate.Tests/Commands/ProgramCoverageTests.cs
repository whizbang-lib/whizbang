using Whizbang.Migrate;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests.Commands;

/// <summary>
/// Coverage for <c>apply</c> handler branches in the whizbang-migrate CLI entry point that are
/// not exercised by <see cref="ProgramCliTests"/>: merging exclude patterns from a decision
/// file, the "more changes" overflow summary, and the package-changes report section.
/// </summary>
/// <remarks>
/// The tool is invoked from pipelines (see residue entry G): a summary section that silently
/// stops rendering, or an exclude pattern that is read but never forwarded, still exits 0 and
/// looks like a clean run to the caller. These tests assert the effect on disk in addition to
/// the printed report, so a regression that only breaks the report (but not the underlying
/// work) is still caught.
/// </remarks>
/// <tests>Whizbang.Migrate/Program.cs:BuildRootCommand</tests>
public class ProgramCoverageTests {

  private const string WOLVERINE_HANDLER = """
    using Wolverine;

    public class CreateOrderHandler : IHandle<CreateOrderCommand> {
      public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
    }

    public record CreateOrderCommand(string OrderId);
    """;

  private static string _newTempDir(string label) {
    var dir = Path.Combine(Path.GetTempPath(), $"whizbang-{label}-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    return dir;
  }

  /// <summary>The console the command actually wrote to, which for a CLI is its whole product.</summary>
  private static string _stdout()
    => ((TUnit.Core.Interfaces.ITestOutput)TestContext.Current!).GetStandardOutput();


  // ── apply: exclude patterns merged from a decision file ────────────────────

  [Test]
  public async Task Apply_WithDecisionFileExcludePatterns_MergesThemAndSkipsMatchingFileAsync() {
    // If a decision file's exclude_patterns are read but never merged into the file filter, an
    // operator who protected generated or vendored code through the decision file (rather than
    // --exclude) gets it rewritten anyway -- silently, since the run still exits 0.
    var dir = _newTempDir("decision-exclude");
    try {
      var handlerFile = Path.Combine(dir, "Handler.cs");
      var excludedFile = Path.Combine(dir, "Excluded.cs");
      await File.WriteAllTextAsync(handlerFile, WOLVERINE_HANDLER);
      await File.WriteAllTextAsync(excludedFile, WOLVERINE_HANDLER);

      var decisionPath = Path.Combine(dir, "decisions.json");
      var decisions = DecisionFile.Create(dir);
      decisions.ExcludePatterns.Add("**/Excluded.cs");
      await decisions.SaveAsync(decisionPath);

      var exitCode = await Program.Main(["apply", "-p", dir, "-d", decisionPath]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(_stdout()).Contains("Exclude patterns from decision file: **/Excluded.cs")
        .Because("the operator has to see that the decision file's own patterns took effect, not "
               + "just whatever was passed on the command line");
      await Assert.That(await File.ReadAllTextAsync(excludedFile)).IsEqualTo(WOLVERINE_HANDLER)
        .Because("a pattern read from the decision file but not merged into the filter would "
               + "rewrite exactly this file");
      await Assert.That(await File.ReadAllTextAsync(handlerFile)).IsNotEqualTo(WOLVERINE_HANDLER)
        .Because("the exclusion must be selective -- proving only the named file survived, not "
               + "that apply silently did nothing at all");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }


  // ── apply: file-change summary overflow ─────────────────────────────────────

  private const string MANY_GUID_CALLS = """
    using System;

    public class OrderIdFactory(ILogger logger) {
      public Guid NextOrderId1() => Guid.NewGuid();
      public Guid NextOrderId2() => Guid.NewGuid();
      public Guid NextOrderId3() => Guid.NewGuid();
      public Guid NextOrderId4() => Guid.NewGuid();
      public Guid NextOrderId5() => Guid.NewGuid();
      public Guid NextOrderId6() => Guid.NewGuid();
      public Guid NextOrderId7() => Guid.NewGuid();
      public Guid NextOrderId8() => Guid.NewGuid();
    }
    """;

  [Test]
  public async Task Apply_WhenAFileHasMoreThanFiveChanges_SummarizesTheOverflowAsync() {
    // The per-file change list is capped at five lines so the console output stays readable.
    // If the overflow count is dropped or miscalculated, an operator reviewing a large file's
    // changes sees an incomplete list with no indication that anything is missing.
    var dir = _newTempDir("many-changes");
    try {
      var file = Path.Combine(dir, "OrderIdFactory.cs");
      await File.WriteAllTextAsync(file, MANY_GUID_CALLS);

      var exitCode = await Program.Main(["apply", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);
      // 8 Guid.NewGuid() replacements + 1 added using + 1 constructor-parameter change = 10;
      // 5 are printed, so 5 remain -- this is the overflow line, not a guess at the total.
      await Assert.That(_stdout()).Contains("... and 5 more changes")
        .Because("the overflow count must match what was actually left off the printed list");
      await Assert.That(await File.ReadAllTextAsync(file)).Contains("idProvider.NewGuid()")
        .Because("the changes summarized in the report have to be changes that really happened");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }


  // ── apply: package-changes report section ───────────────────────────────────

  [Test]
  public async Task Apply_WhenPackagesChange_PrintsThePackageChangesSectionAsync() {
    // Package changes are the part of a migration a build error points at afterward -- a
    // reference nobody removed, or a new one nobody added. If this section stops rendering, an
    // operator has no way to see which package edits actually happened versus which the tool
    // merely computed, short of diffing the project file by hand.
    var dir = _newTempDir("package-changes");
    try {
      var handlerFile = Path.Combine(dir, "Handler.cs");
      await File.WriteAllTextAsync(handlerFile, WOLVERINE_HANDLER);

      var projectFile = Path.Combine(dir, "Sample.csproj");
      await File.WriteAllTextAsync(projectFile, """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Marten" Version="7.0.0" />
          </ItemGroup>
        </Project>
        """);

      var exitCode = await Program.Main(["apply", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);

      var updatedProject = await File.ReadAllTextAsync(projectFile);
      await Assert.That(updatedProject).DoesNotContain("Include=\"Marten\"")
        .Because("the old package reference has to actually be removed, not merely reported");
      await Assert.That(updatedProject).Contains("SoftwareExtravaganza.Whizbang.Data.Postgres")
        .Because("its Whizbang replacement has to actually be added");

      var stdout = _stdout();
      await Assert.That(stdout).Contains("=== Package Changes ===");
      await Assert.That(stdout).Contains("[REMOVED] Marten")
        .Because("an operator has to see which old package was dropped");
      await Assert.That(stdout).Contains("[ADDED] SoftwareExtravaganza.Whizbang.Data.Postgres v1.0.0")
        .Because("and which replacement was added, with the version that will be restored");
      await Assert.That(stdout).Contains("(Sample.csproj)")
        .Because("with multiple projects in a solution, the change has to name which file it "
               + "touched");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }
}
