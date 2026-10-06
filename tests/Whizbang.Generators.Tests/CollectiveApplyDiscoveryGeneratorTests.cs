// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Tests for <c>CollectiveApplyDiscoveryGenerator</c>. Confirms the
/// generator discovers methods marked with <c>[CollectiveApplyFor]</c>
/// and emits a compile-time dispatch table reflecting the attribute's
/// <c>ScopeHandling</c> and <c>SpecKind</c> parameters.
/// </summary>
/// <tests>Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs</tests>
[Category("Generators")]
[Category("CollectiveEvents")]
public class CollectiveApplyDiscoveryGeneratorTests {

  // ── Happy path: single default-attributed method ───────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DiscoversDefaultAttributedMethod_EmitsRegistryEntryAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { public string Status { get; set; } = ""; }

            public sealed record ArchiveJobsCollectiveEvent(
              CollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> Apply(ArchiveJobsCollectiveEvent e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("JobModel");
    await Assert.That(code).Contains("ArchiveJobsCollectiveEvent");
    await Assert.That(code).Contains("JobPerspective");
    await Assert.That(code).Contains("CollectiveScopeHandling.Framework")
      .Because("Unspecified attribute parameters should emit the Framework default — perspectives can opt out per-method, but they have to ask.");
    await Assert.That(code).Contains("CollectiveSpecKind.Linq")
      .Because("Unspecified SpecKind defaults to Linq — Dapper raw-SQL is the explicit escape hatch.");
    await Assert.That(code).Contains("(handler, evt, query) =>")
      .Because("The emitted invoker is 3-arg so the applier can thread the ICollectiveQuery context.");
    await Assert.That(code).DoesNotContain(")evt, query)")
      .Because("A handler that omits the context param is invoked without it — the third arg is just ignored.");
  }

  // ── Handler that takes the ICollectiveQuery context ────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_DiscoversQueryContextHandler_PassesQueryToInvokerAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { public string Status { get; set; } = ""; }

            public sealed record ArchiveJobsCollectiveEvent(
              CollectiveScope Scope,
              System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> Apply(ArchiveJobsCollectiveEvent e, ICollectiveQuery q) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains(")evt, query)")
      .Because("A handler that declares the ICollectiveQuery param is invoked with the threaded context.");
  }

  // ── No attributes: empty registry, no compile error ────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithNoAttributedMethods_EmitsEmptyRegistryAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed class JobPerspective {
              public ICollectiveSpec<JobModel> NotAttributed(int x) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    // Generator MUST emit a registry file even if empty so downstream
    // code (the projection runner) can statically reference it.
    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("CollectiveApplyRegistry");
    await Assert.That(code).DoesNotContain("JobPerspective.NotAttributed")
      .Because("Method without [CollectiveApplyFor] must not be picked up.");
  }

  // ── ScopeHandling = Custom override ────────────────────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithCustomScopeHandling_EmitsCustomEnumValueAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed record E(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class P {
              [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
              public ICollectiveSpec<JobModel> Apply(E e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("CollectiveScopeHandling.Custom")
      .Because("Generator MUST honor explicit ScopeHandling = Custom — the runner branches on this to skip filter composition.");
    await Assert.That(code).DoesNotContain("CollectiveScopeHandling.Framework")
      .Because("Only one mode per entry; the Custom override replaces the default.");
  }

  // ── SpecKind = RawSql override ─────────────────────────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithRawSqlSpecKind_EmitsRawSqlEnumValueAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed record E(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class P {
              [CollectiveApplyFor(SpecKind = CollectiveSpecKind.RawSql)]
              public ICollectiveSpec<JobModel> Apply(E e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("CollectiveSpecKind.RawSql")
      .Because("Generator MUST emit the escape-hatch SpecKind so the runner routes to the raw-SQL adapter instead of the LINQ visitor.");
  }

  // ── §6: per-handler BatchSize / StatementTimeoutSeconds overrides ───────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithApplyKnobOverrides_EmitsOverridesOnEntryAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed record E(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class P {
              [CollectiveApplyFor(BatchSize = 250, StatementTimeoutSeconds = 15)]
              public ICollectiveSpec<JobModel> Apply(E e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("BatchSizeOverride: 250")
      .Because("A handler that declares BatchSize must carry it on the entry so the applier can override the global default for this handler.");
    await Assert.That(code).Contains("StatementTimeoutSecondsOverride: 15")
      .Because("A handler that declares StatementTimeoutSeconds must carry it on the entry to override the global timeout per apply.");
  }

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithoutApplyKnobs_EmitsZeroInheritSentinelsAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed record E(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class P {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> Apply(E e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).Contains("BatchSizeOverride: 0")
      .Because("An unspecified knob emits the 0 sentinel meaning 'inherit the global CollectiveApplyOptions default'.");
    await Assert.That(code).Contains("StatementTimeoutSecondsOverride: 0")
      .Because("An unspecified timeout knob emits the 0 sentinel meaning 'inherit the global default'.");
  }

  // ── Multiple handlers on the same class ────────────────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_WithMultipleHandlersOnSameClass_EmitsAllEntriesAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class JobModel { }
            public sealed record ArchiveEvent(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;
            public sealed record TouchEvent(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class JobPerspective {
              [CollectiveApplyFor]
              public ICollectiveSpec<JobModel> ArchiveAll(ArchiveEvent e) => null!;

              [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Custom)]
              public ICollectiveSpec<JobModel> TouchAll(TouchEvent e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("ArchiveEvent");
    await Assert.That(code).Contains("TouchEvent");
    await Assert.That(code).Contains("CollectiveScopeHandling.Framework");
    await Assert.That(code).Contains("CollectiveScopeHandling.Custom");
  }

  // ── No reflection in emitted code ──────────────────────────────────────

  [Test]
  [RequiresAssemblyFiles]
  public async Task Generator_EmittedCode_UsesNoReflectionAsync() {
    const string source = $$"""
            using Whizbang.Core.Messaging;
            using Whizbang.Core.Perspectives;

            namespace TestApp;

            public sealed class M { }
            public sealed record E(CollectiveScope Scope, System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

            public sealed class P {
              [CollectiveApplyFor]
              public ICollectiveSpec<M> Apply(E e) => null!;
            }

            """;

    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>(source);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs")!;

    // AOT invariant — the emitted code must not contain anything that
    // would force reflection at runtime.
    await Assert.That(code).DoesNotContain("System.Reflection")
      .Because("Per L19 + plan constraint #1, the emitted dispatch table must be reflection-free.");
    await Assert.That(code).DoesNotContain(".Invoke(")
      .Because("MethodInfo.Invoke would force reflection; the generator must emit a typed static lambda invoker instead.");
    await Assert.That(code).DoesNotContain("Activator.CreateInstance")
      .Because("Instances are resolved from DI at the call site; the emitted code must not construct anything via reflection.");
  }
}
