// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.IO;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Whizbang.Generators.Tests;

/// <summary>
/// Each type that declares <c>[LoggerMessage]</c> methods does so in one declaration, under a name no other such
/// declaration in its namespace shares (#2484).
/// </summary>
/// <remarks>
/// The .NET 10 logging generator (Microsoft.Extensions.Logging.Generators 10.0.x, which the <c>net10.0</c> build
/// uses) collects the declarations into a hash set of syntax nodes, whose order changes from one compiler process to
/// the next, then sorts them by namespace and name with an unstable sort. Two declarations with the same namespace
/// and name (two parts of one partial class, or two nested <c>Log</c> classes in different outer types) therefore
/// come out in a different order from build to build, and so does the assembly. The .NET 11 generator sorts by a
/// unique key and is not affected. This guard keeps every key unique, so the <c>net10.0</c> packages are
/// reproducible too; it can go when the libraries stop targeting <c>net10.0</c>.
/// </remarks>
public class LoggerMessageDeclarationGuardTests {
  [Test]
  public async Task EveryLoggerMessageDeclaration_HasANamespaceAndNameOfItsOwnAsync() {
    var root = _findRepoRoot();
    var declarations = new List<(string Key, string Path)>();
    foreach (var directory in new[] { "src", "tools" }) {
      foreach (var file in Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) {
          continue;
        }
        var tree = CSharpSyntaxTree.ParseText(await File.ReadAllTextAsync(file));
        declarations.AddRange(LoggerMessageDeclarationKeys(await tree.GetRootAsync()).Select(key => (key, relative)));
      }
    }

    await Assert.That(declarations.Count).IsGreaterThan(10)
      .Because("the library declares [LoggerMessage] methods in many types, or the scan read nothing");
    var shared = declarations.GroupBy(d => d.Key, StringComparer.Ordinal).Where(g => g.Count() > 1)
      .Select(g => $"{g.Key} in {string.Join(", ", g.Select(d => d.Path))}").ToList();
    await Assert.That(shared).IsEmpty()
      .Because("declare a type's [LoggerMessage] methods in one partial declaration, and give a nested log class a name "
        + "unique in its namespace: " + string.Join("; ", shared));
  }

  [Test]
  public async Task Keys_NameTheNamespaceAndTypeOfEachDeclarationWithLoggerMessageMethodsAsync() {
    const string source = """
namespace A.B {
  public partial class Worker {
    [LoggerMessage(1, LogLevel.Information, "x")] static partial void X(ILogger l);
    private static partial class Log {
      [Microsoft.Extensions.Logging.LoggerMessageAttribute(2, LogLevel.Information, "y")] public static partial void Y(ILogger l);
    }
    private static class NoLogging { static void Z() { } }
  }
}
namespace C;
public partial class Other { [LoggerMessage(3, LogLevel.Information, "z")] static partial void Z(ILogger l); }
public partial class Plain { [Obsolete] void W() { } }
""";

    var keys = LoggerMessageDeclarationKeys(await CSharpSyntaxTree.ParseText(source).GetRootAsync()).ToList();

    await Assert.That(keys).IsEquivalentTo(["A.B.Worker", "A.B.Log", "C.Other"]);
  }

  /// <summary>
  /// The logging generator's sort key (namespace and simple type name) of every type declaration that directly
  /// declares a <c>[LoggerMessage]</c> method.
  /// </summary>
  internal static IEnumerable<string> LoggerMessageDeclarationKeys(Microsoft.CodeAnalysis.SyntaxNode root) =>
    root.DescendantNodes().OfType<TypeDeclarationSyntax>()
      .Where(type => type.Members.OfType<MethodDeclarationSyntax>().Any(_hasLoggerMessage))
      .Select(type => $"{_namespaceOf(type)}.{type.Identifier.Text}");

  private static bool _hasLoggerMessage(MethodDeclarationSyntax method) =>
    method.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => {
      var name = attribute.Name.ToString();
      var simple = name[(name.LastIndexOf('.') + 1)..];
      return simple is "LoggerMessage" or "LoggerMessageAttribute";
    });

  private static string _namespaceOf(TypeDeclarationSyntax type) =>
    string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(ns => ns.Name.ToString()));

  private static string _findRepoRoot() {
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whizbang.slnx"))) {
      dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Whizbang.slnx not found above the test binary.");
  }
}
