// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests;

/// <summary>
/// The informational notices that describe a whole compilation (#1430): each is reported once, without a location,
/// and says what the generator found. Nothing asserted them before, so a notice could stop being reported unseen.
/// </summary>
[Category("SourceGenerators")]
public class CompilationNoticeDiagnosticTests {
  private const string PERSPECTIVE_SOURCE = """
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public record ItemChanged : IEvent { [StreamId] public string ItemId { get; init; } = ""; }
    public record ItemModel { [StreamId] public string ItemId { get; init; } = ""; }

    public class ItemPerspective : IPerspectiveFor<ItemModel, ItemChanged> {
      public ItemModel Apply(ItemModel currentData, ItemChanged @event) => currentData;
    }
    """;

  [Test]
  [RequiresAssemblyFiles()]
  public async Task Whiz028_ReportsTheRunnerCountAsync() {
    var result = GeneratorTestHelper.RunGenerator<PerspectiveRunnerRegistryGenerator>(PERSPECTIVE_SOURCE);

    var notice = _single(result.Diagnostics, "WHIZ028");
    await Assert.That(notice.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("1 runner(s)");
    await Assert.That(notice.Location).IsEqualTo(Location.None);
  }

  [Test]
  public async Task Whiz701_ReportsThePerspectiveCountAsync() {
    var result = GeneratorTestHelper.RunGenerator<EFCorePerspectiveConfigurationGenerator>(PERSPECTIVE_SOURCE);

    var notice = _single(result.Diagnostics, "WHIZ701");
    await Assert.That(notice.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains("Discovered 1 perspective(s)");
    await Assert.That(notice.Location).IsEqualTo(Location.None);
  }

  [Test]
  public async Task Whiz702_ReportsTheMigrationFilesFoundAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(PERSPECTIVE_SOURCE + """

      [Whizbang.Data.EFCore.Custom.WhizbangDbContext]
      public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
      }
      """);

    var notice = _single(result.Diagnostics, "WHIZ702");
    await Assert.That(notice.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).StartsWith("Found ");
    await Assert.That(notice.GetMessage(System.Globalization.CultureInfo.InvariantCulture)).Contains(".sql")
      .Because("The notice lists the first migration files it embedded.");
    await Assert.That(notice.Location).IsEqualTo(Location.None);
  }

  [Test]
  public async Task Whiz703_ReportsThatNoDbContextWasFoundAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(PERSPECTIVE_SOURCE);

    var notice = _single(result.Diagnostics, "WHIZ703");
    await Assert.That(notice.Severity).IsEqualTo(DiagnosticSeverity.Info);
    await Assert.That(notice.Location).IsEqualTo(Location.None);
  }

  private static Diagnostic _single(IEnumerable<Diagnostic> diagnostics, string id) => diagnostics.Single(d => d.Id == id);
}
