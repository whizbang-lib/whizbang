// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:Generator_GuidNewGuid_GeneratesInterceptorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:Generator_GuidCreateVersion7_GeneratesInterceptorAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:Generator_MultipleGuidCalls_GeneratesMultipleInterceptorsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:Generator_SuppressOnMethod_NoInterceptionAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:Generator_SuppressOnClass_NoInterceptionAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorRuntimeTests.cs</tests>
/// Source generator that replaces <c>Guid.NewGuid()</c> and <c>Guid.CreateVersion7()</c> calls with the framework's
/// UUIDv7 (<c>TrackedGuid.New()</c>, time-ordered with counter precision), through C# interceptors.
/// </summary>
/// <remarks>
/// <para>
/// Interception is on by default: a project that does not set <c>WhizbangGuidInterceptionEnabled</c> gets it, and only
/// <c>false</c> turns it off (#2528). <c>[SuppressGuidInterception]</c> keeps the original call where a v4 is
/// deliberate.
/// </para>
/// <para>
/// An interceptor has the intercepted method's signature, so it returns a <see cref="Guid"/>: it cannot attach metadata
/// to the value, only replace it. Third-party time-ordered generators (Marten, UUIDNext, Medo) are therefore not
/// intercepted: they already return time-ordered ids, and a pass-through interceptor would change nothing.
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz058</docs>
[Generator]
public class GuidInterceptorGenerator : IIncrementalGenerator {
  // Method signatures to intercept
  private const string GUID_TYPE = "System.Guid";
  private const string SUPPRESS_ATTRIBUTE = "Whizbang.Core.SuppressGuidInterceptionAttribute";
  private const string SUPPRESS_ATTRIBUTE_NAME = "SuppressGuidInterceptionAttribute";
  private const string SUPPRESS_SHORT_NAME = "SuppressGuidInterception";
  private const string METHOD_NEW_GUID = "NewGuid";
  private const string METHOD_CREATE_VERSION_7 = "CreateVersion7";
  private const string GUID_VERSION_7 = "Version7";
  private const string TEMPLATE_FILE = "GuidInterceptorsTemplate.cs";
  private const string SNIPPETS_FILE = "GuidInterceptorSnippets.cs";

  // Every method name _resolveGuidVersionAndSource can match, derived from the same constants so the predicate
  // cannot drift from the transform.
  private static readonly HashSet<string> _interceptableMethodNames = [
    METHOD_NEW_GUID,
    METHOD_CREATE_VERSION_7,
  ];

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Check if interception is enabled via MSBuild property
    var interceptionEnabled = context.AnalyzerConfigOptionsProvider.Select(
        static (provider, _) => {
          provider.GlobalOptions.TryGetValue("build_property.WhizbangGuidInterceptionEnabled", out var value);
          return IsInterceptionEnabled(value);
        }
    );

    // Discover GUID creation calls (Guid.NewGuid(), Guid.CreateVersion7())
    var guidCalls = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsGuidCallCandidate(node),
        transform: static (ctx, ct) => _extractGuidCallInfo(ctx, ct)
    ).WithTrackingName("GuidCallCandidates");

    // Separate intercepted and suppressed calls
    var interceptedCalls = guidCalls
        .Where(static info => info.Intercepted is not null)
        .Select(static (info, _) => info.Intercepted!);

    var suppressedCalls = guidCalls
        .Where(static info => info.Suppressed is not null)
        .Select(static (info, _) => info.Suppressed!);

    // Combine the calls with the enabled flag for generation. Not with the compilation: the output reads
    // nothing from it, and the compilation changes on every edit, so combining it re-ran the output each time.
    var callsAndEnabled = interceptedCalls.Collect()
        .Combine(suppressedCalls.Collect())
        .Combine(interceptionEnabled);

    context.RegisterSourceOutput(
        callsAndEnabled,
        static (ctx, data) => {
          var intercepted = data.Left.Left;
          var suppressed = data.Left.Right;
          var enabled = data.Right;
          _generateInterceptors(ctx, intercepted, suppressed, enabled);
        }
    );
  }

  /// <summary>
  /// Whether a project's <c>WhizbangGuidInterceptionEnabled</c> value turns interception on. On by default (#2528): an
  /// unset, blank or unrecognized value is on, and only <c>false</c> turns it off.
  /// </summary>
  /// <param name="value">The property's value as the build delivers it, or null when absent.</param>
  /// <returns><see langword="false"/> only for <c>false</c>, in any case and surrounding whitespace.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorGeneratorTests.cs:IsInterceptionEnabled_OnlyFalseTurnsItOffAsync</tests>
  public static bool IsInterceptionEnabled(string? value) =>
      !string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

  /// <summary>
  /// The syntax-only test that decides which nodes reach the semantic transform. Runs on every node of every
  /// edited file, so it must stay cheap and allocation-free, and it admits only a member-access call to a
  /// method name the generator can intercept: a consumer has tens of thousands of member-access calls and a
  /// handful of GUID calls, and the symbol lookup in the transform is the expensive part.
  /// </summary>
  /// <param name="node">The syntax node to test.</param>
  /// <returns><see langword="true"/> when the node is a member-access call to an interceptable method name.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorCandidateFilterTests.cs:IsGuidCallCandidate_OrdinaryMemberAccessCalls_AreRejectedAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/GuidInterceptorCandidateFilterTests.cs:IsGuidCallCandidate_EveryInterceptableMethodName_IsAcceptedAsync</tests>
  public static bool IsGuidCallCandidate(SyntaxNode node) =>
      node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess } &&
      _interceptableMethodNames.Contains(memberAccess.Name.Identifier.Text);

  // S3776: Complexity is from sequential validation checks with early returns — already well-structured
#pragma warning disable S3776
  private static (GuidInterceptionInfo? Intercepted, SuppressedGuidInterceptionInfo? Suppressed) _extractGuidCallInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {
#pragma warning restore S3776

    var invocation = (InvocationExpressionSyntax)context.Node;
    var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
    var methodName = memberAccess.Name.Identifier.Text;

    // Get symbol info for the method being called
    var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, ct);
    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) {
      return (null, null);
    }

    // Check if it's a GUID creation method we want to intercept. A method with no containing type
    // names no interceptable API: _resolveGuidVersionAndSource takes the name as nullable and matches
    // null against none of its patterns, so it answers with no version and no source and the call
    // leaves by the same exit — no separate guard needed here.
    var containingType = TypeNameUtilities.DisplayOrNull(methodSymbol.ContainingType);

    // Skip internal Whizbang library code - we control that and don't need interception
    var callingTypeSymbol = _getContainingTypeSymbol(context, invocation, ct);
    if (callingTypeSymbol is not null) {
      var callingNamespace = TypeNameUtilities.DisplayOrEmpty(TypeNameUtilities.NamespaceOf(callingTypeSymbol));
      if (callingNamespace.StartsWith("Whizbang", StringComparison.Ordinal)) {
        return (null, null);
      }
    }

    // Check for #pragma warning disable WHIZ055/WHIZ056
    if (_hasPragmaSuppression(invocation)) {
      return (null, null);
    }

    var (guidVersion, guidSource) = _resolveGuidVersionAndSource(containingType, methodName);
    if (guidVersion is null || guidSource is null) {
      return (null, null);
    }

    // Get location info
    var location = invocation.GetLocation();
    var lineSpan = location.GetLineSpan();
    var filePath = lineSpan.Path;
    var lineNumber = lineSpan.StartLinePosition.Line + 1; // 1-based
    var columnNumber = memberAccess.Name.SpanStart - invocation.SyntaxTree.GetText(ct).Lines[lineSpan.StartLinePosition.Line].Start + 1;

    // Check for suppression
    var suppressionSource = _checkSuppression(context, invocation, ct);
    if (suppressionSource is not null) {
      return (null, new SuppressedGuidInterceptionInfo(
          FilePath: filePath,
          LineNumber: lineNumber,
          OriginalMethod: $"{containingType}.{methodName}",
          SuppressionSource: suppressionSource,
          Location: location
      ));
    }

    // The compiler's own encoding of the call's location; the file-path form is deprecated.
    var interceptable = context.SemanticModel.GetInterceptableLocation(invocation, ct);
    if (interceptable is null) {
      return (null, null);
    }

    // Generate unique interceptor method name
    var safeFileName = _sanitizeFileName(filePath);
    var interceptorName = $"Intercept_{safeFileName}_{lineNumber}_{columnNumber}";

    return (new GuidInterceptionInfo(
        FilePath: filePath,
        LineNumber: lineNumber,
        ColumnNumber: columnNumber,
        OriginalMethod: methodName,
        FullyQualifiedTypeName: $"global::{containingType}",
        GuidVersion: guidVersion,
        GuidSource: guidSource,
        InterceptorMethodName: interceptorName,
        LocationVersion: interceptable.Version,
        LocationData: interceptable.Data
    ), null);
  }

  /// <summary>
  /// Resolves the GUID version and source metadata for a given containing type and method name.
  /// Only System.Guid's NewGuid and CreateVersion7 are interceptable.
  /// </summary>
  private static (string? Version, string? Source) _resolveGuidVersionAndSource(string? containingType, string methodName) {
    // Check for System.Guid methods
    if (containingType == GUID_TYPE) {
      if (methodName == METHOD_NEW_GUID) {
        return ("Version4", "SourceMicrosoft");
      }
      if (methodName == METHOD_CREATE_VERSION_7) {
        return (GUID_VERSION_7, "SourceMicrosoft");
      }
    }

    return (null, null);
  }

  private static string? _checkSuppression(
      GeneratorSyntaxContext context,
      InvocationExpressionSyntax invocation,
      CancellationToken ct) {

    // Walk up the syntax tree to find containing method and type
    var current = invocation.Parent;
    while (current is not null) {
      if (current is MethodDeclarationSyntax methodDecl &&
          _hasSuppressAttribute(context, methodDecl.AttributeLists, ct)) {
        return "SuppressGuidInterceptionAttribute on method";
      } else if (current is LocalFunctionStatementSyntax localFunc &&
                 _hasSuppressAttribute(context, localFunc.AttributeLists, ct)) {
        return "SuppressGuidInterceptionAttribute on local function";
      } else if (current is TypeDeclarationSyntax typeDecl &&
                 _hasSuppressAttribute(context, typeDecl.AttributeLists, ct)) {
        return "SuppressGuidInterceptionAttribute on type";
      }
      current = current.Parent;
    }

    // Check assembly-level attributes
    var compilation = context.SemanticModel.Compilation;
    return compilation.Assembly.GetAttributes().Any(attr =>
        TypeNameUtilities.IsNamed(attr.AttributeClass, SUPPRESS_ATTRIBUTE) ||
        TypeNameUtilities.SimpleNameOrNull(attr.AttributeClass) is SUPPRESS_ATTRIBUTE_NAME or SUPPRESS_SHORT_NAME)
      ? "SuppressGuidInterceptionAttribute on assembly"
      : null;
  }

  private static bool _hasSuppressAttribute(
      GeneratorSyntaxContext context,
      SyntaxList<AttributeListSyntax> attributeLists,
      CancellationToken ct) {

    foreach (var attrList in attributeLists) {
      foreach (var attr in attrList.Attributes) {
        var attrSymbol = context.SemanticModel.GetSymbolInfo(attr, ct).Symbol?.ContainingType;
        if (attrSymbol is not null) {
          var attrName = TypeNameUtilities.Display(attrSymbol);
          if (attrName == SUPPRESS_ATTRIBUTE ||
              TypeNameUtilities.SimpleName(attrSymbol) == SUPPRESS_ATTRIBUTE_NAME ||
              TypeNameUtilities.SimpleName(attrSymbol) == SUPPRESS_SHORT_NAME) {
            return true;
          }
        }
      }
    }
    return false;
  }

  private static string _sanitizeFileName(string filePath) {
    // Extract just the filename without extension and sanitize
    var fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
    return new string([.. fileName.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);
  }

  private static INamedTypeSymbol? _getContainingTypeSymbol(
      GeneratorSyntaxContext context,
      InvocationExpressionSyntax invocation,
      CancellationToken ct) {

    // Walk up to find containing type declaration
    var current = invocation.Parent;
    while (current is not null) {
      if (current is TypeDeclarationSyntax typeDecl) {
        return context.SemanticModel.GetDeclaredSymbol(typeDecl, ct);
      }
      current = current.Parent;
    }
    return null;
  }

  private static bool _hasPragmaSuppression(InvocationExpressionSyntax invocation) {
    // Check if the invocation is within a #pragma warning disable region for WHIZ055 or WHIZ056
    var syntaxTree = invocation.SyntaxTree;
    var position = invocation.SpanStart;

    // Get all trivia before this position and check for pragma disable
    var root = syntaxTree.GetRoot();
    var triviaList = root.DescendantTrivia()
        .Where(t => t.SpanStart < position &&
                   t.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia));

    return triviaList.Any(trivia => _isActiveDisablePragma(trivia, root, position));
  }

  /// <summary>
  /// Checks if a pragma trivia is an active WHIZ055/WHIZ056 disable that hasn't been restored before the given position.
  /// </summary>
  private static bool _isActiveDisablePragma(SyntaxTrivia trivia, SyntaxNode root, int position) {
    // The caller already filtered to pragma-warning trivia, so the structure test cannot fail today.
    // It stays as the guard on the cast and shares the exit with a pragma that is not a disable.
    if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax pragma
        || !pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)) {
      return false;
    }

    var codes = pragma.ErrorCodes
        .OfType<IdentifierNameSyntax>()
        .Select(id => id.Identifier.Text)
        .ToList();

    if (!codes.Contains("WHIZ055") && !codes.Contains("WHIZ056")) {
      return false;
    }

    // Found a disable before the invocation - check if there's a restore after the disable but before the position
    return !_wasRestoredBefore(root, trivia.SpanStart, position);
  }

  /// <summary>
  /// Checks if a pragma disable was restored between the disable position and the target position.
  /// </summary>
  private static bool _wasRestoredBefore(SyntaxNode root, int disableStart, int position) {
    var restoreTrivia = root.DescendantTrivia()
        .Where(t => t.SpanStart > disableStart &&
                   t.SpanStart < position &&
                   t.IsKind(SyntaxKind.PragmaWarningDirectiveTrivia));

    return restoreTrivia.Any(rt => _isMatchingRestore(rt));
  }

  /// <summary>
  /// Checks if a pragma trivia is a restore directive for WHIZ055/WHIZ056 (or a blanket restore).
  /// </summary>
  private static bool _isMatchingRestore(SyntaxTrivia trivia) {
    // The caller already filtered to pragma-warning trivia, so the structure test cannot fail today.
    // It stays as the guard on the cast and shares the exit with a pragma that is not a restore.
    if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax restorePragma
        || !restorePragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.RestoreKeyword)) {
      return false;
    }

    var restoreCodes = restorePragma.ErrorCodes
        .OfType<IdentifierNameSyntax>()
        .Select(id => id.Identifier.Text)
        .ToList();

    return restoreCodes.Contains("WHIZ055") || restoreCodes.Contains("WHIZ056") || restoreCodes.Count == 0;
  }

  // S3776: Code generation method — complexity from sequential template building with conditional paths
#pragma warning disable S3776, S1144 // S3776: code-gen complexity; S1144: called from RegisterSourceOutput lambda at line 78, enabled used in body
  private static void _generateInterceptors(
      SourceProductionContext context,
            ImmutableArray<GuidInterceptionInfo> intercepted,
      ImmutableArray<SuppressedGuidInterceptionInfo> suppressed,
      bool enabled) {
#pragma warning restore S3776, S1144

    // Report diagnostics for suppressed calls (always, even when disabled)
    foreach (var info in suppressed) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.GuidInterceptionSuppressed,
          info.Location,
          info.OriginalMethod,
          info.FilePath,
          info.LineNumber.ToString(CultureInfo.InvariantCulture),
          info.SuppressionSource
      ));
    }

    // Skip code generation if interception is turned off via MSBuild property: nothing is replaced, so nothing is
    // reported as intercepted.
    if (!enabled) {
      return;
    }

    // Report each replaced call
    foreach (var info in intercepted) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.GuidCallIntercepted,
          Location.None,
          $"{info.FullyQualifiedTypeName.Replace("global::", "")}.{info.OriginalMethod}()",
          info.FilePath,
          info.LineNumber.ToString(CultureInfo.InvariantCulture)
      ));
    }

    // Generate interceptors if there are any calls to intercept
    if (intercepted.IsEmpty) {
      return;
    }

    var assembly = typeof(GuidInterceptorGenerator).Assembly;
    var snippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "INTERCEPTOR_REPLACE");
    var methods = new StringBuilder();
    foreach (var info in intercepted) {
      methods.AppendLine();
      methods.Append(snippet
        .Replace("__ORIGINAL_CALL__", $"{info.FullyQualifiedTypeName.Replace("global::", "")}.{info.OriginalMethod}()")
        .Replace("__FILE_PATH__", info.FilePath)
        .Replace("__LINE__", info.LineNumber.ToString(CultureInfo.InvariantCulture))
        .Replace("__LOCATION_VERSION__", info.LocationVersion.ToString(CultureInfo.InvariantCulture))
        .Replace("__LOCATION_DATA__", _escapeString(info.LocationData))
        .Replace("__INTERCEPTOR_NAME__", info.InterceptorMethodName));
    }

    var source = TemplateUtilities.GetEmbeddedTemplate(assembly, TEMPLATE_FILE);
    source = TemplateUtilities.ReplaceHeaderRegion(assembly, source);
    source = TemplateUtilities.ReplaceRegion(source, "INTERCEPTOR_METHODS", methods.ToString());
    context.AddSource("GuidInterceptors.g.cs", source);
  }

  private static string _escapeString(string s) {
    return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }
}
