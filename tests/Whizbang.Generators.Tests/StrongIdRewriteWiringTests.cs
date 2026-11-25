// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Generators.Tests;

/// <summary>
/// The generated initializer's part in the strong-id stored form (#2518): every start asks the schema's strong-id
/// document gate, which sets the form this process writes an id in; and the instance doing the schema work runs one slice
/// of the rewrite after the temporal phase, under the rewrite's own lock and the gate's hold.
/// </summary>
/// <docs>fundamentals/identity/whizbang-ids#stored-form</docs>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/Templates/DbContextSchemaExtensionTemplate.cs</code-under-test>
public class StrongIdRewriteWiringTests {
  private const string MODEL = """
    using System;
    using Microsoft.EntityFrameworkCore;
    using Whizbang.Core;
    using Whizbang.Core.Perspectives;
    using Whizbang.Data.EFCore.Custom;

    namespace TestApp;

    public record Placed : IEvent;

    public record OrderModel {
      [StreamId]
      public Guid OrderKey { get; init; }

      public string Label { get; init; } = string.Empty;
    }

    public class OrderPerspective : IPerspectiveFor<OrderModel, Placed> {
      public OrderModel Apply(OrderModel currentData, Placed eventData) => currentData;
    }

    [WhizbangDbContext]
    public class OrderDbContext : DbContext {
      public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
    }
    """;

  private static async Task<string> _generatedAsync() {
    var result = await GeneratorTestHelpers.RunServiceRegistrationGeneratorAsync(MODEL);
    return string.Join("\n", result.GeneratedSources.Select(s => s.SourceText.ToString()));
  }

  /// <summary>
  /// The slice runs after the temporal phase, from the model, under the rewrite's own lock and the gate's hold: never the
  /// schema lock, which a starting instance reads as a migration in progress.
  /// </summary>
  [Test]
  public async Task TheStartupSlice_RunsAfterTheTemporalPhase_UnderItsOwnLockAndTheGatesHoldAsync() {
    var output = await _generatedAsync();
    var temporal = output.IndexOf("CanonicalTemporalRewritePhase.ApplyAsync(", StringComparison.Ordinal);
    var slice = output.IndexOf("StrongIdScalarRewrite.ForModel(", StringComparison.Ordinal);

    await Assert.That(temporal).IsGreaterThan(-1);
    await Assert.That(slice).IsGreaterThan(temporal);
    var callStart = output.LastIndexOf("BatchedRewriteRunner.RunAsync(", slice, StringComparison.Ordinal);
    var callEnd = output.IndexOf("startupClock", slice, StringComparison.Ordinal);
    var call = output[callStart..callEnd];
    await Assert.That(call).Contains("BatchedRewriteLockKey.Compute(\"", StringComparison.Ordinal);
    await Assert.That(call).DoesNotContain("lockId", StringComparison.Ordinal).Because("the schema lock is not the rewrite's");
    await Assert.That(call).Contains("BatchedRewriteBudget.ForStartup(", StringComparison.Ordinal);
    await Assert.That(call).Contains("StrongIdDocumentGate.RewriteHoldAsync(connection, \"", StringComparison.Ordinal);
  }

  /// <summary>Every start asks the gate, the fast path included, after the schema work and outside its lock.</summary>
  [Test]
  public async Task EveryStart_AsksTheStrongIdDocumentGateAsync() {
    var output = await _generatedAsync();
    var analyze = output.IndexOf("await _analyzeNewlyIndexedTablesAsync(", StringComparison.Ordinal);
    var gate = output.IndexOf("await _checkStrongIdDocumentGateAsync(", StringComparison.Ordinal);
    var reconcile = output.IndexOf("await _reconcileManagedObjectsAsync(", StringComparison.Ordinal);

    await Assert.That(gate).IsGreaterThan(analyze);
    await Assert.That(reconcile).IsGreaterThan(gate);
    await Assert.That(output).Contains("StrongIdDocumentGate.CheckAsync(", StringComparison.Ordinal);
    await Assert.That(output).Contains("WhizbangIdDocumentForm.Hold(", StringComparison.Ordinal)
      .Because("a gate that cannot be asked keeps the object form every release reads");
  }
}
