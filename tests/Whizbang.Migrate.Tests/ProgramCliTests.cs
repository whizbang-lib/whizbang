using System.CommandLine;
using Whizbang.Migrate;
using Whizbang.Migrate.Wizard;

namespace Whizbang.Migrate.Tests;

/// <summary>
/// Tests for the whizbang-migrate CLI surface.
/// </summary>
/// <remarks>
/// The command names and option aliases are a contract. Scripts, pipelines and the migration
/// documentation all invoke them by name, so a rename or a dropped registration breaks every
/// caller while the tool itself still starts and reports success. Nothing else in the suite
/// would notice, because the failure is a command that quietly no longer exists.
/// </remarks>
/// <tests>Whizbang.Migrate/Program.cs:BuildRootCommand</tests>
public class ProgramCliTests {

  private static Command _command(string name)
    => Program.BuildRootCommand().Subcommands.Single(c => c.Name == name);

  [Test]
  public async Task BuildRootCommand_RegistersEveryDocumentedCommandAsync() {
    // These five names are what the docs and any pipeline invoke. One silently disappearing is
    // indistinguishable from the tool working right up until that step does nothing.
    var names = Program.BuildRootCommand().Subcommands.Select(c => c.Name).ToList();

    await Assert.That(names).Contains("analyze");
    await Assert.That(names).Contains("plan");
    await Assert.That(names).Contains("apply");
    await Assert.That(names).Contains("rollback");
    await Assert.That(names).Contains("status");
  }

  [Test]
  [Arguments("analyze")]
  [Arguments("plan")]
  [Arguments("apply")]
  [Arguments("rollback")]
  [Arguments("status")]
  public async Task EveryCommand_CarriesADescriptionForHelpAsync(string command) {
    // The description is the whole of `--help` for that command. An empty one ships a CLI that
    // lists a command and says nothing about what it does.
    await Assert.That(_command(command).Description).IsNotNullOrEmpty();
  }

  [Test]
  [Arguments("analyze")]
  [Arguments("plan")]
  [Arguments("apply")]
  [Arguments("status")]
  public async Task ProjectScopedCommands_AcceptTheProjectOptionAndItsShortFormAsync(string command) {
    // -p is the alias every documented example uses. Losing it on one command breaks exactly
    // that command's examples, which is the kind of gap nobody finds until they hit it.
    // rollback is deliberately absent: it addresses a checkpoint, not a project path.
    var aliases = _command(command).Options.SelectMany(o => o.Aliases).ToList();

    await Assert.That(aliases).Contains("--project");
    await Assert.That(aliases).Contains("-p");
  }

  [Test]
  public async Task Rollback_TakesACheckpointArgumentAndCanListThemAsync() {
    // rollback is addressed by checkpoint id rather than project path, so its surface is an
    // argument plus --list. Asserting that keeps the exception explicit instead of leaving it
    // looking like a command that simply forgot --project.
    var rollback = _command("rollback");

    await Assert.That(rollback.Arguments.Select(a => a.Name)).Contains("checkpoint");
    var aliases = rollback.Options.SelectMany(o => o.Aliases).ToList();
    await Assert.That(aliases).Contains("--list");
    await Assert.That(aliases).Contains("-l");
    await Assert.That(aliases).DoesNotContain("--project")
      .Because("rollback operates on a checkpoint id; offering a project path would imply a "
             + "scoping it does not have");
  }

  [Test]
  public async Task Analyze_OffersTheFormatOptionAsync() {
    var aliases = _command("analyze").Options.SelectMany(o => o.Aliases).ToList();

    await Assert.That(aliases).Contains("--format");
    await Assert.That(aliases).Contains("-f");
  }

  [Test]
  public async Task Apply_OffersADryRunAsync() {
    // Dry run is the safety valve on a command that rewrites a whole solution in place. If the
    // flag stops being registered, `--dry-run` becomes an unrecognized argument -- and a user
    // expecting a preview gets a real migration instead.
    var aliases = _command("apply").Options.SelectMany(o => o.Aliases).ToList();

    await Assert.That(aliases).Contains("--dry-run");
  }

  [Test]
  public async Task RootCommand_DescribesTheToolAsync() {
    await Assert.That(Program.BuildRootCommand().Description).IsNotNullOrEmpty();
  }

  [Test]
  public async Task BuildRootCommand_IsFreeOfSideEffectsAsync() {
    // Building the tree must not touch the console or the filesystem: it is called once per
    // process today, but the tests above call it repeatedly, and a builder that printed or
    // wrote would make every one of them order-dependent.
    var first = Program.BuildRootCommand();
    var second = Program.BuildRootCommand();

    await Assert.That(first.Subcommands.Count).IsEqualTo(second.Subcommands.Count);
    await Assert.That(first).IsNotSameReferenceAs(second);
  }

  // ── Exit codes ────────────────────────────────────────────────────────────

  [Test]
  public async Task Analyze_OnAMissingDirectory_ExitsNonZeroAsync() {
    // The tool runs from pipelines. Printing "Directory not found" to stderr and exiting 0 is
    // indistinguishable from success to every script that calls it, so the next step runs
    // against a project that was never analyzed -- let alone migrated.
    var missing = Path.Combine(Path.GetTempPath(), "whizbang-absent", Guid.NewGuid().ToString("N"));

    var exitCode = await Program.Main(["analyze", "-p", missing]);

    await Assert.That(exitCode).IsNotEqualTo(0);
  }

  [Test]
  public async Task Apply_WithAMissingDecisionFile_ExitsNonZeroAsync() {
    // A decision file that cannot be found means the migration would run with defaults instead
    // of the operator's recorded choices. That has to stop the pipeline, not proceed quietly.
    var missing = Path.Combine(Path.GetTempPath(), "whizbang-absent", $"{Guid.NewGuid():N}.json");

    var exitCode = await Program.Main(["apply", "-p", Path.GetTempPath(), "-d", missing]);

    await Assert.That(exitCode).IsNotEqualTo(0);
  }

  [Test]
  public async Task UnknownCommand_ExitsNonZeroAsync() {
    // A typo in a pipeline step must fail that step rather than be recorded as completed work.
    var exitCode = await Program.Main(["definitely-not-a-command"]);

    await Assert.That(exitCode).IsNotEqualTo(0);
  }

  [Test]
  public async Task Help_ExitsZeroAsync() {
    // The counterpart: asking for help is not a failure, and a CLI that reported one would
    // break pipelines that probe --help.
    var exitCode = await Program.Main(["--help"]);

    await Assert.That(exitCode).IsEqualTo(0);
  }


  // ── End-to-end: option wiring observed through the filesystem ──────────────

  private const string WOLVERINE_HANDLER = """
    using Wolverine;

    public class CreateOrderHandler : IHandle<CreateOrderCommand> {
      public Task Handle(CreateOrderCommand command) => Task.CompletedTask;
    }

    public record CreateOrderCommand(string OrderId);
    """;

  private static async Task<(string Dir, string File)> _seedProjectAsync(string fileName = "Handler.cs") {
    var dir = Path.Combine(Path.GetTempPath(), $"whizbang-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, fileName);
    await File.WriteAllTextAsync(path, WOLVERINE_HANDLER);
    return (dir, path);
  }

  [Test]
  public async Task Apply_WithoutDryRun_RewritesTheSourceAsync() {
    // The baseline the dry-run assertion is measured against: without the flag, apply really
    // does edit the file in place. Asserting only "dry run changed nothing" would pass just as
    // well if apply were silently doing nothing at all.
    var (dir, file) = await _seedProjectAsync();
    try {
      var exitCode = await Program.Main(["apply", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(await File.ReadAllTextAsync(file)).IsNotEqualTo(WOLVERINE_HANDLER);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_WithDryRun_LeavesTheSourceByteForByteAsync() {
    // ApplyCommand already has a test that it honors dryRun. This is the other half: that the
    // CLI actually routes --dry-run into it. A flag parsed and dropped on the floor looks
    // identical from the command's side, and turns an expected preview into a real migration.
    var (dir, file) = await _seedProjectAsync();
    try {
      var exitCode = await Program.Main(["apply", "-p", dir, "--dry-run"]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(WOLVERINE_HANDLER)
        .Because("--dry-run has to reach ApplyCommand, not merely be accepted by the parser");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_WithAnExcludePattern_SkipsTheExcludedFileAsync() {
    // Exclusions are how an operator protects generated or vendored code from being rewritten.
    // If the option is parsed but not forwarded, those files get migrated anyway.
    var (dir, file) = await _seedProjectAsync("Excluded.cs");
    try {
      var exitCode = await Program.Main(["apply", "-p", dir, "--exclude", "**/Excluded.cs"]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(WOLVERINE_HANDLER)
        .Because("an excluded file must survive the run untouched");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Analyze_OnARealProject_SucceedsAsync() {
    // Exercises the analyze handler end to end: option binding, both analyzers, and the table
    // rendering. A project containing Wolverine handlers is the ordinary input.
    var (dir, _) = await _seedProjectAsync();
    try {
      var exitCode = await Program.Main(["analyze", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Analyze_OnAProjectWithNothingToMigrate_StillSucceedsAsync() {
    // Nothing to migrate is a legitimate answer, not a failure -- a pipeline running analyze
    // across many projects must not treat a clean one as a broken step.
    var dir = Path.Combine(Path.GetTempPath(), $"whizbang-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      await File.WriteAllTextAsync(Path.Combine(dir, "Plain.cs"), "public class Plain { }\n");

      var exitCode = await Program.Main(["analyze", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Status_OnAProjectWithNoMigration_SucceedsAsync() {
    var dir = Path.Combine(Path.GetTempPath(), $"whizbang-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      var exitCode = await Program.Main(["status", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }


  // ── Decision file, end to end through the CLI ─────────────────────────────

  [Test]
  public async Task Apply_GenerateDecisionFile_WritesAFileThatLoadsBackAsync() {
    // This is the user-facing path for the commented decision format: the tool writes it, the
    // operator edits it by hand, and a later run has to read it back. A generated file that
    // cannot be reloaded strands the migration before it starts.
    var (dir, _) = await _seedProjectAsync();
    var decisionPath = Path.Combine(dir, "decisions.jsonc");
    try {
      var exitCode = await Program.Main(["apply", "-p", dir, "--generate-decision-file", decisionPath]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(File.Exists(decisionPath)).IsTrue();

      var loaded = await DecisionFile.LoadAsync(decisionPath);
      await Assert.That(loaded.ProjectPath).IsEqualTo(dir);
      await Assert.That(await File.ReadAllTextAsync(decisionPath)).Contains("//")
        .Because("the generated file is meant to be hand-edited, which is what the comments are for");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_GenerateDecisionFile_DoesNotTransformAnythingAsync() {
    // Generating the file is a preparation step, not a migration. Rewriting source here would
    // migrate a project before the operator had made a single decision.
    var (dir, file) = await _seedProjectAsync();
    var decisionPath = Path.Combine(dir, "decisions.jsonc");
    try {
      await Program.Main(["apply", "-p", dir, "--generate-decision-file", decisionPath]);

      await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(WOLVERINE_HANDLER)
        .Because("generating decisions must not also apply them");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_WithADecisionFileSayingSkip_LeavesTheHandlerAloneAsync() {
    // The decision file exists so an operator can overrule the tool per category. If the CLI
    // accepted -d and then ignored its contents, every recorded decision would be silently
    // discarded -- and the run would look successful while doing the opposite of what was asked.
    var (dir, file) = await _seedProjectAsync();
    var decisionPath = Path.Combine(dir, "decisions.json");
    try {
      var decisions = DecisionFile.Create(dir);
      decisions.Decisions.Handlers.Default = DecisionChoice.Skip;
      decisions.Decisions.Projections.Default = DecisionChoice.Skip;
      await decisions.SaveAsync(decisionPath);

      var exitCode = await Program.Main(["apply", "-p", dir, "-d", decisionPath]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(await File.ReadAllTextAsync(file)).IsEqualTo(WOLVERINE_HANDLER)
        .Because("a decision file that says Skip has to reach the transformer, not just be read");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_WithADecisionFileSayingConvert_TransformsAsync() {
    // The control for the Skip case above: with the same wiring and the opposite decision, the
    // file really is rewritten. Without this, "Skip worked" would also be satisfied by apply
    // doing nothing at all.
    var (dir, file) = await _seedProjectAsync();
    var decisionPath = Path.Combine(dir, "decisions.json");
    try {
      var decisions = DecisionFile.Create(dir);
      decisions.Decisions.Handlers.Default = DecisionChoice.Convert;
      await decisions.SaveAsync(decisionPath);

      var exitCode = await Program.Main(["apply", "-p", dir, "-d", decisionPath]);

      await Assert.That(exitCode).IsEqualTo(0);
      await Assert.That(await File.ReadAllTextAsync(file)).IsNotEqualTo(WOLVERINE_HANDLER);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Apply_GeneratedDecisionFile_IsImmediatelyUsableAsync() {
    // The two halves joined: generate, then hand the generated file straight back to apply.
    // This is exactly the sequence the tool prints as its own next-step instruction, so a
    // generated file the tool cannot consume would break its documented workflow.
    var (dir, _) = await _seedProjectAsync();
    var decisionPath = Path.Combine(dir, "decisions.jsonc");
    try {
      await Program.Main(["apply", "-p", dir, "--generate-decision-file", decisionPath]);

      var exitCode = await Program.Main(["apply", "-p", dir, "-d", decisionPath]);

      await Assert.That(exitCode).IsEqualTo(0);
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }


  private const string FULL_MARTEN_WOLVERINE_PROJECT = """
    using Marten;
    using Marten.Events.Aggregation;
    using Wolverine;

    public class OrderHandler : IHandle<PlaceOrder> {
      public Task Handle(PlaceOrder command) => Task.CompletedTask;
    }

    public class OrderProjection : SingleStreamProjection<OrderModel> {
      public void Apply(OrderPlaced @event, OrderModel state) { state.Status = "placed"; }
    }

    public class OrderRepository {
      private readonly IDocumentSession _session;
      private readonly IDocumentStore _store;
      public OrderRepository(IDocumentSession session, IDocumentStore store) {
        _session = session;
        _store = store;
      }
    }

    public static class Startup {
      public static void Configure(IServiceCollection services) {
        services.AddMarten(o => { });
        services.UseWolverine();
      }
    }

    public class OrderModel { public string Status { get; set; } = ""; }
    public record PlaceOrder(string Id);
    public record OrderPlaced(string Id);
    """;

  [Test]
  public async Task Analyze_OnAProjectUsingEveryMigratablePattern_RendersAndSucceedsAsync() {
    // The analyze report has a section per artefact kind, and each is skipped when its list is
    // empty. A project exercising only handlers -- which is what the other tests use -- never
    // reaches the projection, event-store or DI rendering at all. Those sections are the tool's
    // primary output, and a formatting fault in one of them takes down the whole command for
    // any consumer whose project happens to contain that artefact.
    var dir = Path.Combine(Path.GetTempPath(), $"whizbang-cli-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    try {
      await File.WriteAllTextAsync(Path.Combine(dir, "Everything.cs"), FULL_MARTEN_WOLVERINE_PROJECT);

      var exitCode = await Program.Main(["analyze", "-p", dir]);

      await Assert.That(exitCode).IsEqualTo(0)
        .Because("a project using every migratable pattern is the ordinary input, not an edge case");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

  [Test]
  public async Task Analyze_WithJsonFormat_ReportsUnimplementedWithoutFaultingAsync() {
    // The json format is advertised by --format and is not implemented yet. Two separate
    // requirements: it must not fault, and it must not claim success -- a caller that asked for
    // JSON and got prose under exit 0 fails its parse with nothing explaining why.
    var (dir, _) = await _seedProjectAsync();
    try {
      var exitCode = await Program.Main(["analyze", "-p", dir, "--format", "json"]);

      await Assert.That(exitCode).IsNotEqualTo(0)
        .Because("output that is not JSON must not be returned under a success code");
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }


  [Test]
  [Arguments("--list")]
  [Arguments("-l")]
  public async Task Rollback_ListingCheckpoints_DoesNotReportSuccessWhileUnimplementedAsync(
      string listAlias) {
    // The listing is not built yet. Exiting 0 would tell a caller the tool had looked and found
    // no checkpoints, which is indistinguishable from a tree that genuinely has none.
    var exitCode = await Program.BuildRootCommand().InvokeAsync(["rollback", listAlias]);

    await Assert.That(exitCode).IsNotEqualTo(0)
      .Because("an unimplemented listing must not be reported as an empty one");
  }

  [Test]
  public async Task Rollback_ToACheckpoint_DoesNotReportSuccessWhileUnimplementedAsync() {
    // The dangerous case: `whizbang-migrate rollback <id> && deploy` would treat a no-op as a
    // restored tree and deploy on top of the migration it believed it had just reverted.
    var exitCode = await Program.BuildRootCommand()
      .InvokeAsync(["rollback", "checkpoint-0001"]);

    await Assert.That(exitCode).IsNotEqualTo(0)
      .Because("a rollback that did not happen must not exit 0");
  }

  [Test]
  public async Task Rollback_WithoutCheckpointOrList_FailsRatherThanDoingNothingAsync() {
    // Neither branch matches, so the handler previously fell through and exited 0 having taken
    // no action at all -- a silent no-op is the worst of the three outcomes.
    var exitCode = await Program.BuildRootCommand().InvokeAsync(["rollback"]);

    await Assert.That(exitCode).IsNotEqualTo(0)
      .Because("invoking rollback with no target is a usage error, not a successful no-op");
  }


  [Test]
  public async Task Plan_DoesNotReportSuccessWhileUnimplementedAsync() {
    // The command accepts --output. Exiting 0 says a plan was written there; nothing was, so a
    // pipeline that reads that path next picks up a stale plan or an absent file.
    var exitCode = await Program.BuildRootCommand().InvokeAsync(["plan"]);

    await Assert.That(exitCode).IsNotEqualTo(0)
      .Because("a plan that was never produced must not be reported as produced");
  }

  [Test]
  public async Task Analyze_TableFormat_StillSucceedsOnAnEmptyTreeAsync() {
    // Guards the fix above: making the json branch fail must not make the default path fail.
    var tempDir = Path.Combine(Path.GetTempPath(), $"whizbang-table-{Guid.NewGuid():N}");
    Directory.CreateDirectory(tempDir);

    try {
      var exitCode = await Program.BuildRootCommand()
        .InvokeAsync(["analyze", "--project", tempDir]);

      await Assert.That(exitCode).IsEqualTo(0)
        .Because("a tree with nothing to migrate analyzes cleanly, it is not an error");
    } finally {
      Directory.Delete(tempDir, recursive: true);
    }
  }


  [Test]
  [Arguments("analyze", "Sample.csproj")]
  [Arguments("analyze", "Sample.sln")]
  [Arguments("apply", "Sample.csproj")]
  [Arguments("apply", "Sample.sln")]
  public async Task Commands_GivenAProjectOrSolutionFile_ScanTheContainingDirectoryAsync(
      string command, string projectFileName) {
    // --project is documented as taking a project or solution file, but every analyzer walks a
    // directory. Passing the file has to resolve to its parent; if the strip is dropped the
    // tool scans a path that is not a directory, finds nothing, and reports a clean tree --
    // a false all-clear on a codebase that does need migrating.
    var (dir, _) = await _seedProjectAsync();
    try {
      var projectFile = Path.Combine(dir, projectFileName);
      await File.WriteAllTextAsync(projectFile, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

      var exitCode = await Program.Main([command, "-p", projectFile]);

      await Assert.That(exitCode).IsEqualTo(0)
        .Because("pointing at the project file is the documented way to invoke the tool");

      if (command == "apply") {
        var rewritten = await File.ReadAllTextAsync(Path.Combine(dir, "Handler.cs"));
        await Assert.That(rewritten).Contains("Whizbang.Core")
          .Because("resolving the file to its directory is what lets the handler be found; "
                 + "an unresolved path would leave the source untouched and still exit 0");
      }
    } finally {
      Directory.Delete(dir, recursive: true);
    }
  }

}
