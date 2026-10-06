// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.EFCore.Postgres.Generators;

namespace Whizbang.Generators.Tests.Analyzers;

/// <summary>
/// WHIZ400 checks the type argument of <c>Query&lt;T&gt;</c> and <c>GetByIdAsync&lt;T&gt;</c> only: another
/// generic method on the same multi-model lens takes whatever type it declares, and is not checked.
/// </summary>
/// <code-under-test>src/Whizbang.Data.EFCore.Postgres.Generators/LensQueryTypeArgumentAnalyzer.cs</code-under-test>
public class LensQueryTypeArgumentAnalyzerBranchTests {
  private static string _source(string call) => $$"""
    using System;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    namespace Whizbang.Core.Lenses {
      public interface ILensQuery { }
      public class PerspectiveRow<T> where T : class { public T Data { get; set; } = default!; }
      public interface ILensQuery<T1, T2> : ILensQuery
          where T1 : class
          where T2 : class {
        IQueryable<PerspectiveRow<T>> Query<T>() where T : class;
        Task<T?> GetByIdAsync<T>(Guid id, CancellationToken ct = default) where T : class;
        T Describe<T>() where T : class;
      }
    }

    namespace TestNamespace {
      public class Order { }
      public class Customer { }
      public class Unrelated { }

      public class Resolver {
        private readonly Whizbang.Core.Lenses.ILensQuery<Order, Customer> _query = null!;
        public object Run() => {{call}};
      }
    }
    """;

  [Test]
  [RequiresAssemblyFiles]
  public async Task AnotherGenericMethod_IsNotCheckedAsync() {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(_source("_query.Describe<Unrelated>()"));

    await Assert.That(diagnostics).IsEmpty();
  }

  [Test]
  [RequiresAssemblyFiles]
  [Arguments("_query.Query<Unrelated>()")]
  [Arguments("_query.GetByIdAsync<Unrelated>(Guid.Empty)")]
  public async Task QueryAndGetById_WithAnUnrelatedType_AreWHIZ400Async(string call) {
    var diagnostics = await AnalyzerTestHelper.GetDiagnosticsAsync<LensQueryTypeArgumentAnalyzer>(_source(call));

    await Assert.That(diagnostics.Select(d => d.Id)).Contains("WHIZ400");
  }
}
