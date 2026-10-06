// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Naming the defaults explicitly, <c>ScopeHandling = Framework</c> and <c>SpecKind = Linq</c>, emits
/// the same registration as leaving them out; only the non-zero values select Custom and RawSql.
/// </summary>
/// <tests>src/Whizbang.Generators/CollectiveApplyDiscoveryGenerator.cs</tests>
[Category("SourceGenerators")]
public class CollectiveApplyDiscoveryGeneratorBranchTests {
  [Test]
  [RequiresAssemblyFiles]
  public async Task ExplicitDefaults_EmitFrameworkAndLinqAsync() {
    var result = GeneratorTestHelper.RunGenerator<CollectiveApplyDiscoveryGenerator>("""
      using Whizbang.Core.Messaging;
      using Whizbang.Core.Perspectives;

      namespace TestApp {
        public sealed class JobModel { }

        public sealed record TouchEvent(
          ICollectiveScope Scope,
          System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds) : ICollectiveEvent;

        public sealed class JobPerspective {
          [CollectiveApplyFor(ScopeHandling = CollectiveScopeHandling.Framework, SpecKind = CollectiveSpecKind.Linq)]
          public ICollectiveSpec<JobModel> Touch(TouchEvent e) => null!;
        }
      }

      namespace Whizbang.Core.Messaging {
        public interface ICollectiveScope { string ScopeKind { get; } }
        public interface ICollectiveEvent {
          ICollectiveScope Scope { get; }
          System.Collections.Generic.IReadOnlyList<System.Guid> MatchedStreamIds { get; }
        }
      }

      namespace Whizbang.Core.Perspectives {
        public interface ICollectiveSetters<TModel> where TModel : class { }
        public interface ICollectiveSpec<TModel> where TModel : class {
          System.Linq.Expressions.Expression<System.Action<ICollectiveSetters<TModel>>> Setters { get; }
        }

        public enum CollectiveScopeHandling { Framework = 0, Custom = 1 }
        public enum CollectiveSpecKind { Linq = 0, RawSql = 1 }

        [System.AttributeUsage(System.AttributeTargets.Method)]
        public sealed class CollectiveApplyForAttribute : System.Attribute {
          public CollectiveScopeHandling ScopeHandling { get; init; }
          public CollectiveSpecKind SpecKind { get; init; }
        }
      }
      """);
    var code = GeneratorTestHelper.GetGeneratedSource(result, "CollectiveApplyRegistry.g.cs");

    await Assert.That(code).IsNotNull();
    await Assert.That(code).Contains("CollectiveScopeHandling.Framework");
    await Assert.That(code).Contains("CollectiveSpecKind.Linq");
    await Assert.That(code).DoesNotContain("CollectiveScopeHandling.Custom");
    await Assert.That(code).DoesNotContain("CollectiveSpecKind.RawSql");
  }
}
