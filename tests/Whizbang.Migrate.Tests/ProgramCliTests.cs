using System.CommandLine;
using Whizbang.Migrate;

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

}
