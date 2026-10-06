// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Every clause of the purity analyzer's I/O and clock heuristics, each in both directions: a call
/// the clause names is reported and a call it does not name is left alone.
/// </summary>
/// <tests>src/Whizbang.Generators/PerspectivePurityAnalyzer.cs</tests>
[Category("Analyzers")]
public class PerspectivePurityAnalyzerBranchTests {
  private const string HEADER = """
    using System;
    using Whizbang.Core.Perspectives;

    namespace TestApp;

    public class Order { public Guid Id { get; set; } }
    public class OrderUpdated { public Guid OrderId { get; set; } }

    """;

  private static readonly string[] _expectedDatabaseCalls = [
    "TestApp.AppDbContext.Touch", "TestApp.ILensQueryLike.Touch", "TestApp.IPerspectiveStoreLike.Touch",
    "TestApp.OrdersDbSet.Touch", "TestApp.Repo.DeleteAsync", "TestApp.Repo.FindAsync", "TestApp.Repo.GetAsync",
    "TestApp.Repo.InsertAsync", "TestApp.Repo.QueryAsync", "TestApp.Repo.SaveAsync", "TestApp.Repo.UpdateAsync",
    "TestApp.Repo.UpsertAsync",
  ];

  private static readonly string[] _expectedHttpCalls = [
    "TestApp.HttpGateway.DeleteOrder", "TestApp.HttpGateway.GetOrder", "TestApp.HttpGateway.PostOrder",
    "TestApp.HttpGateway.PutOrder", "TestApp.RetryHttpMessageInvoker.Touch", "TestApp.TenantHttpClient.Touch",
  ];

  [RequiresAssemblyFiles]
  private static async Task<ImmutableArray<Diagnostic>> _diagnosticsAsync(string source, bool allowUnsafe = false) {
    var compilation = AnalyzerTestHelper.CreateCompilationWithFrameworkReferences(source);
    if (allowUnsafe) {
      compilation = compilation.WithOptions(((CSharpCompilationOptions)compilation.Options).WithAllowUnsafe(true));
    }
    return await compilation.WithAnalyzers([new PerspectivePurityAnalyzer()]).GetAnalyzerDiagnosticsAsync();
  }

  private static List<string> _calls(ImmutableArray<Diagnostic> diagnostics, string id) =>
    [.. diagnostics.Where(d => d.Id == id)
      .Select(d => d.GetMessage(CultureInfo.InvariantCulture))
      .Select(m => m[(m.IndexOf("calls '", StringComparison.Ordinal) + 7)..])
      .Select(m => m[..m.IndexOf('\'', StringComparison.Ordinal)])
      .OrderBy(m => m, StringComparer.Ordinal)];

  /// <summary>
  /// Database I/O is a call on a type whose name says it is a context, a set, a perspective store
  /// or a lens query, or an <c>...Async</c> method whose name says it saves, inserts, updates,
  /// deletes, upserts, queries, gets or finds. Anything else is not.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task DatabaseHeuristic_ReportsExactlyTheNamedCallsAsync() {
    var diagnostics = await _diagnosticsAsync(HEADER + """
      public class AppDbContext { public void Touch() { } }
      public class OrdersDbSet { public void Touch() { } }
      public class IPerspectiveStoreLike { public void Touch() { } }
      public class ILensQueryLike { public void Touch() { } }
      public class Repo {
        public void SaveAsync() { }
        public void InsertAsync() { }
        public void UpdateAsync() { }
        public void DeleteAsync() { }
        public void UpsertAsync() { }
        public void QueryAsync() { }
        public void GetAsync() { }
        public void FindAsync() { }
        public void RenderAsync() { }
        public void Save() { }
      }

      public class DbPerspective : IPerspectiveFor<Order, OrderUpdated> {
        public Order Apply(Order current, OrderUpdated @event) {
          new AppDbContext().Touch();
          new OrdersDbSet().Touch();
          new IPerspectiveStoreLike().Touch();
          new ILensQueryLike().Touch();
          var repo = new Repo();
          repo.SaveAsync();
          repo.InsertAsync();
          repo.UpdateAsync();
          repo.DeleteAsync();
          repo.UpsertAsync();
          repo.QueryAsync();
          repo.GetAsync();
          repo.FindAsync();
          repo.RenderAsync();
          repo.Save();
          return current;
        }
      }
      """);

    await Assert.That(_calls(diagnostics, "WHIZ102")).IsEquivalentTo(_expectedDatabaseCalls);
  }

  /// <summary>
  /// HTTP I/O is any call on an <c>HttpClient</c> or <c>HttpMessageInvoker</c> type, or a
  /// <c>Get</c>/<c>Post</c>/<c>Put</c>/<c>Delete</c> method on a type whose name mentions Http.
  /// The same verbs elsewhere, and other verbs on an Http type, are not.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task HttpHeuristic_ReportsExactlyTheNamedCallsAsync() {
    var diagnostics = await _diagnosticsAsync(HEADER + """
      public class TenantHttpClient { public void Touch() { } }
      public class RetryHttpMessageInvoker { public void Touch() { } }
      public class HttpGateway {
        public void GetOrder() { }
        public void PostOrder() { }
        public void PutOrder() { }
        public void DeleteOrder() { }
        public void PatchOrder() { }
      }
      public class Ledger {
        public void GetOrder() { }
        public void PostOrder() { }
        public void PutOrder() { }
        public void DeleteOrder() { }
      }

      public class HttpPerspective : IPerspectiveFor<Order, OrderUpdated> {
        public Order Apply(Order current, OrderUpdated @event) {
          new TenantHttpClient().Touch();
          new RetryHttpMessageInvoker().Touch();
          var gateway = new HttpGateway();
          gateway.GetOrder();
          gateway.PostOrder();
          gateway.PutOrder();
          gateway.DeleteOrder();
          gateway.PatchOrder();
          var ledger = new Ledger();
          ledger.GetOrder();
          ledger.PostOrder();
          ledger.PutOrder();
          ledger.DeleteOrder();
          return current;
        }
      }
      """);

    await Assert.That(_calls(diagnostics, "WHIZ103")).IsEquivalentTo(_expectedHttpCalls);
  }

  /// <summary>
  /// The clock check is <c>Now</c> or <c>UtcNow</c> read from <c>DateTime</c> or
  /// <c>DateTimeOffset</c>; any other member of those types, such as a constant, is deterministic.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task ClockHeuristic_ReportsOnlyNowAndUtcNowAsync() {
    var diagnostics = await _diagnosticsAsync(HEADER + """
      public class ClockPerspective : IPerspectiveFor<Order, OrderUpdated> {
        public Order Apply(Order current, OrderUpdated @event) {
          var a = DateTimeOffset.Now;
          var b = DateTimeOffset.MinValue;
          var c = DateTime.MaxValue;
          return current;
        }
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ104")).IsEqualTo(1)
      .Because("DateTimeOffset.Now is the clock; MinValue and MaxValue are constants");
  }

  /// <summary>
  /// A call through a function pointer binds to the pointer's type rather than to a method, so
  /// neither heuristic sees a method to classify, even when the pointer is named like database I/O.
  /// </summary>
  [Test]
  [RequiresAssemblyFiles]
  public async Task FunctionPointerCall_IsNotClassifiedAsIoAsync() {
    var diagnostics = await _diagnosticsAsync(HEADER + """
      public unsafe class PointerPerspective : IPerspectiveFor<Order, OrderUpdated> {
        public delegate*<int> SaveAsync;

        public Order Apply(Order current, OrderUpdated @event) {
          var value = SaveAsync();
          return current;
        }
      }
      """, allowUnsafe: true);

    await Assert.That(diagnostics.Where(d => d.Id is "WHIZ102" or "WHIZ103")).IsEmpty();
  }
}
