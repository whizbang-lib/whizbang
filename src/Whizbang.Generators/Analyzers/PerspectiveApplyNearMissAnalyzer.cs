// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Shared.Utilities;
using Whizbang.Generators.Utilities;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Reports a method on a perspective that has an <c>Apply</c>'s signature and a name one slip away from <c>Apply</c>
/// (#1936). The runner routes only to methods named <c>Apply</c> that implement the interfaces, so a misspelled handler
/// compiles and never runs.
/// </summary>
/// <remarks>
/// <para>The rule is narrow so that an ordinary helper never trips it. A method is reported when all of these hold:</para>
/// <list type="bullet">
/// <item>its name, ignoring case, is within one edit of <c>Apply</c> (one letter inserted, removed or replaced, or two
/// adjacent letters swapped), or starts with <c>Apply</c> or <c>Applie</c>, or ends with <c>Apply</c>;</item>
/// <item>it is an instance method taking the perspective's model and an event, and returns what an <c>Apply</c>
/// returns: the model, <c>ApplyResult&lt;TModel&gt;</c>, <c>ModelAction</c> or <c>(TModel?, ModelAction)</c>;</item>
/// <item>it implements no interface member and overrides nothing (a contract, not a typo);</item>
/// <item>nothing in the perspective calls it or takes it as a method group (a helper an <c>Apply</c> calls is not a
/// handler).</item>
/// </list>
/// </remarks>
/// <docs>fundamentals/perspectives/perspectives</docs>
/// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyNearMissTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveApplyNearMissAnalyzer : DiagnosticAnalyzer {
  private const string CATEGORY = "Whizbang.Perspectives";
  private const string TARGET = "apply";

  /// <summary>
  /// WHIZ113: Error - a method on a perspective looks like a misspelled <c>Apply</c>: an Apply's signature, a name one
  /// slip away, and nothing calls it.
  /// </summary>
  /// <docs>operations/diagnostics/whiz113</docs>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyNearMissTests.cs:AMisspelledApplyThatNothingCalls_IsAnErrorAtTheMethodAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/PerspectiveApplyNearMissTests.cs:TheNameRule_DecidesExactlyTheseNamesAsync</tests>
  public static readonly DiagnosticDescriptor MisspelledApply = new(
      id: "WHIZ113",
      title: "Method looks like a misspelled Apply",
      messageFormat: "Method '{0}' on perspective '{1}' has the signature of an Apply for '{2}', but it is not named Apply and nothing calls it, so it never runs. Rename it to Apply and list '{2}' under a perspective interface, or call it from an Apply.",
      category: CATEGORY,
      defaultSeverity: DiagnosticSeverity.Error,
      isEnabledByDefault: true,
      description: "The runner calls only the Apply methods a perspective's interfaces declare. A method with an Apply's parameters and return whose name is one slip away from Apply (Aply, Apply_, ApplyEvent, OnApply, Applied) compiles and is never called. A helper that an Apply calls, or a method that implements or overrides a contract, is not reported."
  );

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [MisspelledApply];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterSymbolStartAction(_startType, SymbolKind.NamedType);
  }

  private static void _startType(SymbolStartAnalysisContext context) {
    var perspective = (INamedTypeSymbol)context.Symbol;
    if (perspective.TypeKind != TypeKind.Class || !PerspectiveDiscoveryHelper.IsPerspectiveClass(perspective)) {
      return;
    }

    var contract = PerspectiveContract.Of(perspective);
    ImmutableArray<IMethodSymbol> candidates = [.. perspective.GetMembers().OfType<IMethodSymbol>()
      .Where(m => _looksLikeApply(m, contract) && !_isContract(m, perspective))];
    if (candidates.IsEmpty) {
      return;
    }

    // A candidate the perspective's own code calls, or takes as a method group, is a helper, not a lost handler.
    var referenced = new ConcurrentDictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
    context.RegisterOperationAction(
        operation => referenced[((IInvocationOperation)operation.Operation).TargetMethod.OriginalDefinition] = true,
        OperationKind.Invocation);
    context.RegisterOperationAction(
        operation => referenced[((IMethodReferenceOperation)operation.Operation).Method.OriginalDefinition] = true,
        OperationKind.MethodReference);
    context.RegisterSymbolEndAction(end => {
      foreach (var candidate in candidates.Where(c => !referenced.ContainsKey(c))) {
        end.ReportDiagnostic(Diagnostic.Create(
            MisspelledApply,
            candidate.Locations[0],
            candidate.Name,
            perspective.Name,
            TypeNameUtilities.MinimallyQualified(candidate.Parameters[1].Type)));
      }
    });
  }

  /// <summary>An instance method with an Apply's parameters and return, and a name one slip away from Apply.</summary>
  private static bool _looksLikeApply(IMethodSymbol method, PerspectiveContract contract) =>
    method is { MethodKind: MethodKind.Ordinary, IsStatic: false, IsOverride: false, Parameters.Length: 2 }
    && method.Name != PerspectiveContract.APPLY
    && IsNearMissName(method.Name)
    && contract.HasModel(method.Parameters[0].Type)
    && PerspectiveContract.IsEvent(method.Parameters[1].Type)
    && PerspectiveContract.IsApplyShapedReturn(method.Parameters[0].Type, method.ReturnType);

  /// <summary>Whether the method implements a member of an interface the perspective implements.</summary>
  private static bool _isContract(IMethodSymbol method, INamedTypeSymbol perspective) =>
    perspective.AllInterfaces
      .SelectMany(i => i.GetMembers(method.Name))
      .Any(member => SymbolEqualityComparer.Default.Equals(perspective.FindImplementationForInterfaceMember(member), method));

  /// <summary>
  /// The name half of the rule: ignoring case, within one edit of <c>Apply</c>, or starting with <c>Apply</c> or
  /// <c>Applie</c>, or ending with <c>Apply</c>.
  /// </summary>
  internal static bool IsNearMissName(string name) {
    var lower = name.ToLowerInvariant();
    return _withinOneEdit(lower, TARGET)
      || lower.StartsWith(TARGET, StringComparison.Ordinal)
      || lower.StartsWith("applie", StringComparison.Ordinal)
      || lower.EndsWith(TARGET, StringComparison.Ordinal);
  }

  private static bool _withinOneEdit(string name, string target) {
    if (name.Length == target.Length) {
      return _oneReplacedOrSwapped(name, target);
    }
    var (shorter, longer) = name.Length < target.Length ? (name, target) : (target, name);
    return longer.Length - shorter.Length == 1 && _oneInserted(shorter, longer);
  }

  /// <summary>Equal length: identical, or one letter replaced, or two adjacent letters swapped.</summary>
  private static bool _oneReplacedOrSwapped(string a, string b) {
    var i = _commonPrefix(a, b);
    if (i >= a.Length - 1) {
      return true;
    }
    return string.CompareOrdinal(a, i + 1, b, i + 1, a.Length) == 0
      || (a[i] == b[i + 1] && a[i + 1] == b[i] && string.CompareOrdinal(a, i + 2, b, i + 2, a.Length) == 0);
  }

  /// <summary>The longer is the shorter with one letter inserted.</summary>
  private static bool _oneInserted(string shorter, string longer) {
    var i = _commonPrefix(shorter, longer);
    return string.CompareOrdinal(shorter, i, longer, i + 1, longer.Length) == 0;
  }

  /// <summary>How many leading letters match; <paramref name="a"/> is never the longer of the two.</summary>
  private static int _commonPrefix(string a, string b) {
    var i = 0;
    while (i < a.Length && a[i] == b[i]) {
      i++;
    }
    return i;
  }
}
