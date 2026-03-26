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
/// <tests>tests/Whizbang.Generators.Tests/ThirdPartyGuidInterceptionTests.cs:Generator_MartenCombGuid_InterceptsAndAddsSourceMartenMetadataAsync</tests>
/// Source generator that intercepts GUID creation calls and wraps them with TrackedGuid.
/// Uses C# 12 interceptors feature for zero-overhead compile-time interception.
/// </summary>
[Generator]
public class GuidInterceptorGenerator : IIncrementalGenerator {
  // Method signatures to intercept
  private const string GUID_TYPE = "System.Guid";
  private const string SUPPRESS_ATTRIBUTE = "Whizbang.Core.SuppressGuidInterceptionAttribute";
  private const string SUPPRESS_ATTRIBUTE_NAME = "SuppressGuidInterceptionAttribute";
  private const string SUPPRESS_SHORT_NAME = "SuppressGuidInterception";
  private const string METHOD_NEW_GUID = "NewGuid";
  private const string GUID_VERSION_7 = "Version7";

  // Third-party library patterns
  private static readonly (string TypePattern, string MethodName, string Version, string Source)[] _thirdPartyMethods = [
    ("Marten.Schema.Identity.CombGuidIdGeneration", METHOD_NEW_GUID, GUID_VERSION_7, "SourceMarten"),
    ("UUIDNext.Uuid", "NewDatabaseFriendly", GUID_VERSION_7, "SourceUuidNext"),
    ("UUIDNext.Uuid", "NewSequential", GUID_VERSION_7, "SourceUuidNext"),
    ("Medo.Uuid7", "NewUuid7", GUID_VERSION_7, "SourceMedo"),
  ];

  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // Check if interception is enabled via MSBuild property
    var interceptionEnabled = context.AnalyzerConfigOptionsProvider.Select(
        static (provider, _) => {
          provider.GlobalOptions.TryGetValue("build_property.WhizbangGuidInterceptionEnabled", out var value);
          return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    );

    // Discover GUID creation calls (Guid.NewGuid(), Guid.CreateVersion7())
    var guidCalls = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => node is InvocationExpressionSyntax invocation &&
                                       invocation.Expression is MemberAccessExpressionSyntax,
        transform: static (ctx, ct) => _extractGuidCallInfo(ctx, ct)
    );

    // Separate intercepted and suppressed calls
    var interceptedCalls = guidCalls
        .Where(static info => info.Intercepted is not null)
        .Select(static (info, _) => info.Intercepted!);

    var suppressedCalls = guidCalls
        .Where(static info => info.Suppressed is not null)
        .Select(static (info, _) => info.Suppressed!);

    // Combine with compilation and enabled flag for generation
    var compilationAndCalls = context.CompilationProvider
        .Combine(interceptedCalls.Collect())
        .Combine(suppressedCalls.Collect())
        .Combine(interceptionEnabled);

    context.RegisterSourceOutput(
        compilationAndCalls,
        static (ctx, data) => {
          var compilation = data.Left.Left.Left;
          var intercepted = data.Left.Left.Right;
          var suppressed = data.Left.Right;
          var enabled = data.Right;
          _generateInterceptors(ctx, intercepted, suppressed, enabled);
        }
    );
  }

  private static (GuidInterceptionInfo? Intercepted, SuppressedGuidInterceptionInfo? Suppressed) _extractGuidCallInfo(
      GeneratorSyntaxContext context,
      CancellationToken ct) {

    var invocation = (InvocationExpressionSyntax)context.Node;
    var memberAccess = (MemberAccessExpressionSyntax)invocation.Expression;
    var methodName = memberAccess.Name.Identifier.Text;

    // Get symbol info for the method being called
    var symbolInfo = context.SemanticModel.GetSymbolInfo(invocation, ct);
    if (symbolInfo.Symbol is not IMethodSymbol methodSymbol) {
      return (null, null);
    }

    // Check if it's a GUID creation method we want to intercept
    var containingType = methodSymbol.ContainingType?.ToDisplayString();
    if (containingType is null) {
      return (null, null);
    }

    // Skip internal Whizbang library code - we control that and don't need interception
    var callingTypeSymbol = _getContainingTypeSymbol(context, invocation, ct);
    if (callingTypeSymbol is not null) {
      var callingNamespace = callingTypeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
      if (callingNamespace.StartsWith("Whizbang", StringComparison.Ordinal)) {
        return (null, null);
      }
    }

    // Check for #pragma warning disable WHIZ055/WHIZ056
    if (_hasPragmaSuppression(invocation)) {
      return (null, null);
    }

    string? guidVersion = null;
    string? guidSource = null;

    // Check for System.Guid methods
    if (containingType == GUID_TYPE) {
      if (methodName == METHOD_NEW_GUID) {
        guidVersion = "Version4";
        guidSource = "SourceMicrosoft";
      } else if (methodName == "CreateVersion7") {
        guidVersion = GUID_VERSION_7;
        guidSource = "SourceMicrosoft";
      }
    }

    // Check for third-party methods
    if (guidVersion is null) {
      foreach (var (typePattern, method, version, source) in _thirdPartyMethods) {
        if (containingType == typePattern && methodName == method) {
          guidVersion = version;
          guidSource = source;
          break;
        }
      }
    }

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
        InterceptorMethodName: interceptorName
    ), null);
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
        attr.AttributeClass?.ToDisplayString() == SUPPRESS_ATTRIBUTE ||
        attr.AttributeClass?.Name == SUPPRESS_ATTRIBUTE_NAME ||
        attr.AttributeClass?.Name == SUPPRESS_SHORT_NAME)
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
          var attrName = attrSymbol.ToDisplayString();
          if (attrName == SUPPRESS_ATTRIBUTE ||
              attrSymbol.Name == SUPPRESS_ATTRIBUTE_NAME ||
              attrSymbol.Name == SUPPRESS_SHORT_NAME) {
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

    foreach (var trivia in triviaList) {
      if (_isActiveDisablePragma(trivia, root, position)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Checks if a pragma trivia is an active WHIZ055/WHIZ056 disable that hasn't been restored before the given position.
  /// </summary>
  private static bool _isActiveDisablePragma(SyntaxTrivia trivia, SyntaxNode root, int position) {
    if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax pragma) {
      return false;
    }

    var isDisable = pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword);
    if (!isDisable) {
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
    if (trivia.GetStructure() is not PragmaWarningDirectiveTriviaSyntax restorePragma) {
      return false;
    }

    var isRestore = restorePragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.RestoreKeyword);
    if (!isRestore) {
      return false;
    }

    var restoreCodes = restorePragma.ErrorCodes
        .OfType<IdentifierNameSyntax>()
        .Select(id => id.Identifier.Text)
        .ToList();

    return restoreCodes.Contains("WHIZ055") || restoreCodes.Contains("WHIZ056") || restoreCodes.Count == 0;
  }

#pragma warning disable S1144 // Called from RegisterSourceOutput lambda at line 78; enabled is used at line 337
  private static void _generateInterceptors(
      SourceProductionContext context,
            ImmutableArray<GuidInterceptionInfo> intercepted,
      ImmutableArray<SuppressedGuidInterceptionInfo> suppressed,
      bool enabled) {
#pragma warning restore S1144

    // Report diagnostics for intercepted calls (always, even when disabled)
    foreach (var info in intercepted) {
      context.ReportDiagnostic(Diagnostic.Create(
          DiagnosticDescriptors.GuidCallIntercepted,
          Location.None,
          $"{info.FullyQualifiedTypeName.Replace("global::", "")}.{info.OriginalMethod}()",
          info.FilePath,
          info.LineNumber.ToString(CultureInfo.InvariantCulture)
      ));
    }

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

    // Skip code generation if interception is disabled via MSBuild property
    if (!enabled) {
      return;
    }

    // Generate interceptors if there are any calls to intercept
    if (intercepted.IsEmpty) {
      return;
    }

    var sb = new StringBuilder();
    sb.AppendLine("// <auto-generated/>");
    sb.AppendLine($"// Generated by Whizbang.Generators.GuidInterceptorGenerator at {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    sb.AppendLine("// DO NOT EDIT - Changes will be overwritten");
    sb.AppendLine("#nullable enable");
    sb.AppendLine();
    sb.AppendLine("namespace System.Runtime.CompilerServices {");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Specifies the location where an interceptor method intercepts a call.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  [global::System.AttributeUsage(");
    sb.AppendLine("      global::System.AttributeTargets.Method,");
    sb.AppendLine("      AllowMultiple = true,");
    sb.AppendLine("      Inherited = false)]");
    sb.AppendLine("  file sealed class InterceptsLocationAttribute : global::System.Attribute {");
    sb.AppendLine("    public InterceptsLocationAttribute(string filePath, int line, int column) { }");
    sb.AppendLine("  }");
    sb.AppendLine("}");
    sb.AppendLine();
    sb.AppendLine("namespace Whizbang.Generators.Generated {");
    sb.AppendLine("  /// <summary>");
    sb.AppendLine("  /// Auto-generated interceptors for GUID creation calls.");
    sb.AppendLine("  /// </summary>");
    sb.AppendLine("  file static class GuidInterceptors {");

    foreach (var info in intercepted) {
      sb.AppendLine();
      sb.AppendLine("    /// <summary>");
      sb.AppendLine($"    /// Intercepts {info.FullyQualifiedTypeName.Replace("global::", "")}.{info.OriginalMethod}() at {info.FilePath}:{info.LineNumber}");
      sb.AppendLine("    /// </summary>");
      sb.AppendLine($"    [global::System.Runtime.CompilerServices.InterceptsLocation(\"{_escapeString(info.FilePath)}\", {info.LineNumber}, {info.ColumnNumber})]");
      sb.AppendLine($"    internal static global::Whizbang.Core.ValueObjects.TrackedGuid {info.InterceptorMethodName}() {{");

      // Generate the actual call based on the original method
      var originalCall = info.OriginalMethod switch {
        METHOD_NEW_GUID when info.FullyQualifiedTypeName == "global::System.Guid" => "global::System.Guid.NewGuid()",
        "CreateVersion7" when info.FullyQualifiedTypeName == "global::System.Guid" => "global::System.Guid.CreateVersion7()",
        METHOD_NEW_GUID => $"{info.FullyQualifiedTypeName}.NewGuid()",
        "NewSequential" => $"{info.FullyQualifiedTypeName}.NewSequential()",
        "NewDatabaseFriendly" => $"{info.FullyQualifiedTypeName}.NewDatabaseFriendly(global::UUIDNext.Database.PostgreSql)",
        "NewUuid7" => $"{info.FullyQualifiedTypeName}.NewUuid7().ToGuid()",
        _ => $"{info.FullyQualifiedTypeName}.{info.OriginalMethod}()"
      };

      sb.AppendLine("      return global::Whizbang.Core.ValueObjects.TrackedGuid.FromIntercepted(");
      sb.AppendLine($"          {originalCall},");
      sb.AppendLine($"          global::Whizbang.Core.ValueObjects.GuidMetadata.{info.GuidVersion} | global::Whizbang.Core.ValueObjects.GuidMetadata.{info.GuidSource});");
      sb.AppendLine("    }");
    }

    sb.AppendLine("  }");
    sb.AppendLine("}");

    context.AddSource("GuidInterceptors.g.cs", sb.ToString());
  }

  private static string _escapeString(string s) {
    return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
  }
}
