// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Reflection;
using System.Text.RegularExpressions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Messaging;
using Whizbang.Core.Validation;

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// The guards that keep the time-ordering check wired (#2528). It arrived once with no caller and stayed that way for
/// months, so:
/// <list type="bullet">
/// <item><description>every concrete event store, work coordinator and work coordinator strategy in the framework's
/// assemblies, found by reflection, either runs the check itself or is listed as a forwarder with the inner it forwards to:
/// a new implementation cannot skip it silently;</description></item>
/// <item><description>the check has callers, at every entry point the inventory names;</description></item>
/// <item><description>every framework site that mints a non-v7 id is on <see cref="FrameworkNonTimeOrderedIds"/>.</description></item>
/// </list>
/// Each scan is checked against a fact it must find, so a scan that looks in the wrong place fails.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#time-ordering-checks</docs>
[Category("Shard5")]
public partial class GuidOrderingEntryPointGuardTests {
  private static readonly string _repoRoot = _findRepoRoot();

  /// <summary>Implementations that run the check themselves, by type name.</summary>
  private static readonly HashSet<string> _validates = new(StringComparer.Ordinal) {
    "EFCoreEventStore", "DapperPostgresEventStore", "InMemoryEventStore",
    "EFCoreWorkCoordinator", "DapperWorkCoordinator",
    "ImmediateWorkCoordinatorStrategy", "IntervalWorkCoordinatorStrategy", "BatchWorkCoordinatorStrategy",
    "ScopedWorkCoordinatorStrategy", "StreamAffinityWorkCoordinatorStrategy",
  };

  /// <summary>Implementations that only forward to an inner implementation that checks, and why that is enough.</summary>
  private static readonly Dictionary<string, string> _forwards = new(StringComparer.Ordinal) {
    ["NonDisposingStrategyAdapter"] = "forwards every queue call to the wrapped strategy",
    ["ForwardingEventStoreDecorator"] = "base of the event store decorators; forwards appends to the inner store",
    ["AppendAndWaitEventStoreDecorator"] = "appends through the inner store, then waits",
    ["AuditingEventStoreDecorator"] = "appends through the inner store first; its audit message id is minted",
    ["SyncTrackingEventStoreDecorator"] = "appends through the inner store first, then tracks",
    ["SecurityContextEventStoreDecorator"] = "builds envelopes with minted ids and appends through the inner store",
    ["UpcastingEventStoreDecorator"] = "forwards appends unchanged",
    ["ArchiveAwareEventStoreDecorator"] = "forwards appends unchanged",
  };

  private static readonly Type[] _surfaces = [typeof(IEventStore), typeof(IWorkCoordinator), typeof(IWorkCoordinatorStrategy)];

  private static IEnumerable<Assembly> _frameworkAssemblies() => [
    typeof(IEventStore).Assembly,
    typeof(EFCoreEventStore<>).Assembly,
    typeof(Whizbang.Data.Dapper.Postgres.DapperWorkCoordinator).Assembly,
    typeof(Whizbang.Data.Dapper.Custom.DapperEventStoreBase).Assembly,
    typeof(Whizbang.Data.Postgres.PostgresRouteTraceStore).Assembly,
  ];

  private static List<string> _implementations() {
    var names = new List<string>();
    foreach (var assembly in _frameworkAssemblies().Distinct()) {
      foreach (var type in assembly.GetTypes()) {
        if (type.IsAbstract || type.IsInterface || !_surfaces.Any(s => s.IsAssignableFrom(type))) {
          continue;
        }
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        names.Add(tick < 0 ? name : name[..tick]);
      }
    }
    return [.. names.Distinct().Order(StringComparer.Ordinal)];
  }

  [Test]
  public async Task EveryEntryPointImplementation_RunsTheCheckOrForwardsToOneThatDoesAsync() {
    var implementations = _implementations();

    await Assert.That(implementations).Contains("EFCoreEventStore").Because("the control: the scan finds the EF Core store");
    await Assert.That(implementations).Contains("DapperWorkCoordinator").Because("the control: the scan finds the Dapper coordinator");
    var unlisted = implementations.Where(n => !_validates.Contains(n) && !_forwards.ContainsKey(n)).ToList();
    await Assert.That(unlisted).IsEmpty()
      .Because("a new event store, work coordinator or strategy must call GuidOrderingValidator, or be listed as a forwarder");
  }

  [Test]
  public async Task EveryImplementationListedAsValidating_CallsTheValidatorInItsSourceAsync() {
    foreach (var name in _validates) {
      // A partial class spans files; one of them runs the check.
      var files = _sourceFilesOf(name);
      await Assert.That(files).IsNotEmpty().Because($"{name} is declared somewhere under src");
      await Assert.That(files.Any(f => ValidatorCall().IsMatch(File.ReadAllText(f)))).IsTrue()
        .Because($"{name} runs the time-ordering check (declared in {string.Join(", ", files.Select(f => Path.GetRelativePath(_repoRoot, f)))})");
    }
  }

  /// <summary>The validator has callers, at every entry point the inventory names; it must never be left uncalled again.</summary>
  [Test]
  public async Task TheValidator_HasCallersAtEveryNamedEntryPointAsync() {
    var callers = Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !f.EndsWith("GuidOrderingValidator.cs", StringComparison.Ordinal)
        && ValidatorCall().IsMatch(File.ReadAllText(f)))
      .Select(f => Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'))
      .ToHashSet(StringComparer.Ordinal);

    string[] required = [
      "src/Whizbang.Core/Dispatcher.cs",
      "src/Whizbang.Core/Messaging/ImmediateWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/IntervalWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/BatchWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/ScopedWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/StreamAffinityWorkCoordinatorStrategy.cs",
      "src/Whizbang.Core/Messaging/InMemoryEventStore.cs",
      "src/Whizbang.Data.EFCore.Postgres/EFCoreWorkCoordinator.cs",
      "src/Whizbang.Data.EFCore.Postgres/EFCoreEventStore.cs",
      "src/Whizbang.Data.Dapper.Postgres/DapperWorkCoordinator.cs",
      "src/Whizbang.Data.Dapper.Postgres/DapperPostgresEventStore.cs",
      "src/Whizbang.Data.Dapper.Sqlite/DapperSqliteEventStore.cs",
    ];
    await Assert.That(callers).IsNotEmpty().Because("GuidOrderingValidator must have callers");
    foreach (var file in required) {
      await Assert.That(callers).Contains(file).Because($"{file} is an entry point and runs the check");
    }
  }

  /// <summary>Every framework site that mints a non-v7 id is on the list, with its reason.</summary>
  [Test]
  public async Task EveryNonTimeOrderedMintingSite_IsOnTheListAsync() {
    var listed = FrameworkNonTimeOrderedIds.All.Select(e => e.Site[..e.Site.IndexOf(':', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
    var found = Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Select(f => Path.GetRelativePath(_repoRoot, f).Replace('\\', '/'))
      .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("Generators", StringComparison.Ordinal)
        && !f.StartsWith("src/Whizbang.Testing/", StringComparison.Ordinal) && !f.Contains("Analyzers", StringComparison.Ordinal)
        && !f.Contains("/Templates/", StringComparison.Ordinal)
        && File.ReadLines(Path.Combine(_repoRoot, f)).Any(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && NonV7Minting().IsMatch(l)))
      .ToList();

    await Assert.That(found).Contains("src/Whizbang.Core/Messaging/IntegrityDeliveryId.cs").Because("the control: the scan finds a known site");
    // The framework's v7 generators build their ids from bytes too, and stamp version 7.
    string[] v7Generators = ["src/Whizbang.Core/ValueObjects/Uuid7Generator.cs", "src/Whizbang.Core/Messaging/DerivedIdentity.cs"];
    await Assert.That(found.Where(f => !listed.Contains(f) && !v7Generators.Contains(f)).ToList()).IsEmpty()
      .Because("a new non-v7 id minting site needs an entry in FrameworkNonTimeOrderedIds saying why it is safe");
    foreach (var entry in FrameworkNonTimeOrderedIds.All) {
      var parts = entry.Site.Split(':');
      var path = Path.Combine(_repoRoot, parts[0]);
      await Assert.That(File.Exists(path)).IsTrue().Because($"{entry.Site} names a file that exists");
      await Assert.That(await File.ReadAllTextAsync(path)).Contains(parts[1]).Because($"{entry.Site} names a member of its file");
    }
  }

  private static List<string> _sourceFilesOf(string typeName) =>
    [.. Directory.EnumerateFiles(Path.Combine(_repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
      .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && Regex.IsMatch(File.ReadAllText(f), $@"\bclass {typeName}\b"))];

  [GeneratedRegex(@"\.(ValidateMessageId|ValidateAppend)\(")]
  private static partial Regex ValidatorCall();

  // A name-based id, a guid built from hash or byte material, a stamped version nibble, or a random v4.
  [GeneratedRegex(@"NameBasedGuid\.V[58]\(|0x0F\) \| 0x[0-9A-F]0\)|new Guid\((hash|bytes|uuid|id)\b|Guid\.NewGuid\(\)")]
  private static partial Regex NonV7Minting();

  private static string _findRepoRoot() {
    var dir = AppContext.BaseDirectory;
    while (dir is not null) {
      if (Directory.Exists(Path.Combine(dir, "src", "Whizbang.Core"))) {
        return dir;
      }
      dir = Path.GetDirectoryName(dir);
    }
    throw new InvalidOperationException("Could not locate whizbang repo root from AppContext.BaseDirectory");
  }
}
