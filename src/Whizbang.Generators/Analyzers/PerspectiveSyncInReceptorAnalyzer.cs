using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Whizbang.Generators.Analyzers;

/// <summary>
/// Roslyn analyzer that reports WHIZ900 when WaitForStreamAsync or WaitAsync is called
/// inside a receptor that fires at an Inline lifecycle stage.
/// </summary>
/// <remarks>
/// <para>
/// Calling WaitForStreamAsync/WaitAsync inside an Inline-stage receptor deadlocks the
/// work coordinator because the single-threaded coordinator cannot process perspective
/// commits while blocked by the synchronous wait.
/// </para>
/// <para>
/// Safe alternatives:
/// <list type="bullet">
/// <item><description>Use a Detached stage: <c>[FireAt(LifecycleStage.PostInboxDetached)]</c></description></item>
/// <item><description>Use event enrichment to carry data forward instead of querying</description></item>
/// </list>
/// </para>
/// </remarks>
/// <docs>operations/diagnostics/whiz900</docs>
/// <tests>Whizbang.Generators.Tests/Analyzers/PerspectiveSyncInReceptorAnalyzerTests.cs</tests>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class PerspectiveSyncInReceptorAnalyzer : DiagnosticAnalyzer {
  private const string RECEPTOR_INTERFACE_PREFIX = "Whizbang.Core.IReceptor<";
  private const string SYNC_RECEPTOR_INTERFACE_PREFIX = "Whizbang.Core.ISyncReceptor<";
  private const string FIRE_AT_ATTRIBUTE = "Whizbang.Core.Messaging.FireAtAttribute";
  private const string LIFECYCLE_STAGE_TYPE = "Whizbang.Core.Messaging.LifecycleStage";

  private static readonly string[] _dangerousMethods = ["WaitForStreamAsync", "WaitAsync"];

  /// <summary>
  /// Inline lifecycle stages that will deadlock if WaitForStreamAsync/WaitAsync is called.
  /// </summary>
  private static readonly string[] _inlineStageNames = [
    "ImmediateInline", "LocalImmediateInline",
    "PreDistributeInline", "DistributeInline", "PostDistributeInline",
    "PreOutboxInline", "PostOutboxInline",
    "PreInboxInline", "PostInboxInline",
    "PrePerspectiveInline", "PostPerspectiveInline", "PostAllPerspectivesInline",
    "PostLifecycleInline"
  ];

  /// <summary>
  /// Default stages for receptors without [FireAt] — all are Inline.
  /// </summary>
  private static readonly string[] _defaultStageNames = [
    "LocalImmediateInline", "PreOutboxInline", "PostInboxInline"
  ];

  /// <inheritdoc/>
  public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
      [DiagnosticDescriptors.PerspectiveSyncInInlineReceptor];

  /// <inheritdoc/>
  public override void Initialize(AnalysisContext context) {
    context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
    context.EnableConcurrentExecution();
    context.RegisterOperationAction(_analyzeInvocation, OperationKind.Invocation);
  }

  private static void _analyzeInvocation(OperationAnalysisContext context) {
    var invocation = (IInvocationOperation)context.Operation;

    // Check if method is WaitForStreamAsync or WaitAsync
    var methodName = invocation.TargetMethod.Name;
    if (!_dangerousMethods.Contains(methodName)) {
      return;
    }

    // Check if the method belongs to IPerspectiveSyncAwaiter
    var containingType = invocation.TargetMethod.ContainingType;
    if (containingType is null) {
      return;
    }

    // Match on type name — works for both the interface and the concrete class
    var typeName = containingType.ToDisplayString();
    if (!typeName.Contains("PerspectiveSyncAwaiter") && !typeName.Contains("IPerspectiveSyncAwaiter")) {
      return;
    }

    // Walk up to find the containing class
    var containingClass = _findContainingClass(context.ContainingSymbol);
    if (containingClass is null) {
      return;
    }

    // Check if the class is a receptor (implements IReceptor<T> or IReceptor<T,R> or ISyncReceptor variants)
    if (!_isReceptor(containingClass)) {
      return;
    }

    // Get the [FireAt] stages (or defaults if none)
    var (inlineStages, isDefault) = _getInlineStages(containingClass);
    if (inlineStages.Length == 0) {
      return; // All stages are Detached — safe
    }

    // Report WHIZ200 for each Inline stage
    var firstInlineStage = inlineStages[0];
    var suggestedDetachedStage = firstInlineStage.Replace("Inline", "Detached");

    context.ReportDiagnostic(Diagnostic.Create(
        DiagnosticDescriptors.PerspectiveSyncInInlineReceptor,
        invocation.Syntax.GetLocation(),
        containingClass.Name,
        methodName,
        firstInlineStage,
        suggestedDetachedStage));
  }

  private static INamedTypeSymbol? _findContainingClass(ISymbol? symbol) {
    while (symbol is not null) {
      if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class } namedType) {
        return namedType;
      }
      symbol = symbol.ContainingSymbol;
    }
    return null;
  }

  private static bool _isReceptor(INamedTypeSymbol classSymbol) {
    foreach (var iface in classSymbol.AllInterfaces) {
      var display = iface.OriginalDefinition.ToDisplayString();
      if (display.StartsWith(RECEPTOR_INTERFACE_PREFIX, System.StringComparison.Ordinal) || display.StartsWith(SYNC_RECEPTOR_INTERFACE_PREFIX, System.StringComparison.Ordinal)) {
        return true;
      }
    }
    return false;
  }

  /// <summary>
  /// Returns the Inline stage names from [FireAt] attributes.
  /// If no [FireAt] attributes, returns the default Inline stages.
  /// Returns empty if all stages are Detached (safe).
  /// </summary>
  private static (string[] InlineStages, bool IsDefault) _getInlineStages(INamedTypeSymbol classSymbol) {
    var fireAtStages = new System.Collections.Generic.List<string>();

    foreach (var attribute in classSymbol.GetAttributes()) {
      if (attribute.AttributeClass?.ToDisplayString() != FIRE_AT_ATTRIBUTE) {
        continue;
      }

      if (attribute.ConstructorArguments.Length > 0 && attribute.ConstructorArguments[0].Value is int stageValue) {
        var stageType = attribute.AttributeClass.GetMembers().OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.MethodKind == MethodKind.Constructor)
            ?.Parameters.FirstOrDefault()?.Type;

        if (stageType is INamedTypeSymbol enumType) {
          var enumMember = enumType.GetMembers().OfType<IFieldSymbol>()
              .FirstOrDefault(f => f.ConstantValue is int val && val == stageValue);

          if (enumMember is not null) {
            fireAtStages.Add(enumMember.Name);
          }
        }
      }
    }

    if (fireAtStages.Count == 0) {
      // No [FireAt] — defaults to Inline stages
      return (_defaultStageNames, true);
    }

    // Filter to only Inline stages
    var inlineOnly = fireAtStages.Where(s => _inlineStageNames.Contains(s)).ToArray();
    return (inlineOnly, false);
  }
}
