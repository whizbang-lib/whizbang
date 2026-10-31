// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Whizbang.Generators.Shared.Utilities;

namespace Whizbang.Generators;

/// <summary>
/// Records what each assembly reads from configuration: every section it binds from a literal
/// <c>GetSection("...")</c> key, the keys the binder reads under it, and one capability requirement per
/// <c>[RequiresCapability]</c> or <c>[RequiresPostgresExtension]</c> on a property or enum member reached from
/// the bound type. The manifest registers itself into <c>ConfigurationManifestRegistry</c> at module load.
/// </summary>
/// <remarks>
/// <para>
/// The capability verdict has to name the features that need a capability and the setting that turned each on.
/// Reading that from options types at run time would be reflection, so it is written down here, from code that
/// already exists: the binding call and the bound type. Only the requirement itself is declared.
/// </para>
/// <para>
/// A binding is recorded only when its path is certain: every <c>GetSection</c> key in the chain is a
/// compile-time constant and the chain starts from a configuration that is not itself a section reached
/// elsewhere. The bound type must be one generated code can name, since the manifest records it.
/// </para>
/// </remarks>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestGeneratorTests.cs</tests>
/// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestGeneratorCoverageTests.cs</tests>
[Generator]
public class ConfigurationManifestGenerator : IIncrementalGenerator {
  private const string GET_SECTION = "GetSection";
  private const string SEPARATOR = ":";
  private const string WILDCARD = "*";
  private const string POSTGRES_PROVIDER = "postgres";
  private const string INSTANCE_PARAMETER = "instance";
  private const string KEY_PARAMETER = "key";
  private const string FEATURE = "Feature";
  private const string TRUE = "true";
  private const string FALSE = "false";
  private const string MANIFEST_FILE = "ConfigurationManifest.g.cs";
  private const string SNIPPETS_FILE = "ConfigurationManifestSnippets.cs";

  private const string I_CONFIGURATION = "global::Microsoft.Extensions.Configuration.IConfiguration";
  private const string I_CONFIGURATION_SECTION = "global::Microsoft.Extensions.Configuration.IConfigurationSection";
  private const string CONFIGURATION_KEY_NAME = "global::Microsoft.Extensions.Configuration.ConfigurationKeyNameAttribute";
  private const string REQUIRES_CAPABILITY = "global::Whizbang.Core.Capabilities.RequiresCapabilityAttribute";
  private const string REQUIRES_POSTGRES_EXTENSION = "global::Whizbang.Core.Capabilities.RequiresPostgresExtensionAttribute";
  private const string REMOVED_CONFIGURATION_KEY = "Whizbang.Core.Configuration.RemovedConfigurationKeyAttribute";

  private static readonly string[] _bindingMethods = ["Bind", "Get", "Configure"];

  // The types whose methods bind a section to an options type: the binder itself (Bind, Get<T>), and the two
  // options extensions that bind through it (services.Configure<T>(section), optionsBuilder.Bind(section)).
  private static readonly HashSet<string> _binders = new(StringComparer.Ordinal) {
    "global::Microsoft.Extensions.Configuration.ConfigurationBinder",
    "global::Microsoft.Extensions.DependencyInjection.OptionsConfigurationServiceCollectionExtensions",
    "global::Microsoft.Extensions.DependencyInjection.OptionsBuilderConfigurationExtensions",
  };

  // Values the binder converts from one string that are not special types. Each has public properties the walk
  // would otherwise mistake for a section (DateTimeOffset.Offset, Uri.Segments, CultureInfo.NumberFormat).
  private static readonly HashSet<string> _valueTypes = new(StringComparer.Ordinal) {
    "global::System.TimeSpan",
    "global::System.DateTimeOffset",
    "global::System.DateOnly",
    "global::System.TimeOnly",
    "global::System.Guid",
    "global::System.Uri",
    "global::System.Version",
    "global::System.Half",
    "global::System.Int128",
    "global::System.UInt128",
    "global::System.Globalization.CultureInfo",
  };

  private static readonly HashSet<string> _dictionaries = new(StringComparer.Ordinal) {
    "global::System.Collections.Generic.IDictionary<TKey, TValue>",
    "global::System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
  };

  /// <inheritdoc/>
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    var bindings = context.SyntaxProvider.CreateSyntaxProvider(
        predicate: static (node, _) => IsSectionBindingCandidate(node),
        transform: static (ctx, ct) => _extractBinding(ctx, ct))
      .Where(static binding => binding is not null)
      .Select(static (binding, _) => binding!);

    // [RemovedConfigurationKey] on the assembly (a compilation unit's attribute list) or on a class or record. Its
    // AttributeUsage allows no other target (anywhere else is a compile error), so every node the attribute is on counts.
    var removedKeys = context.SyntaxProvider.ForAttributeWithMetadataName(
        REMOVED_CONFIGURATION_KEY,
        predicate: static (_, _) => true,
        transform: static (ctx, _) => _extractRemovedKeys(ctx.Attributes))
      .SelectMany(static (keys, _) => keys);

    var assemblyName = context.CompilationProvider.Select(static (c, _) => c.Assembly.Name);

    context.RegisterSourceOutput(bindings.Collect().Combine(removedKeys.Collect()).Combine(assemblyName),
      static (spc, pair) => _emit(spc, pair.Left.Left, pair.Left.Right, pair.Right));
  }

  /// <summary>
  /// The removed keys a set of <c>[RemovedConfigurationKey]</c> attributes declares. A declaration with no key
  /// (missing, or null) names nothing and is skipped; a null replacement, or a null list of them, names nothing.
  /// </summary>
  private static ImmutableArray<RemovedKeyInfo> _extractRemovedKeys(ImmutableArray<AttributeData> attributes) {
    var keys = ImmutableArray.CreateBuilder<RemovedKeyInfo>();
    foreach (var attribute in attributes) {
      if (attribute.ConstructorArguments is not [{ Value: string key }, var replacements]) {
        continue;
      }
      var replacedBy = replacements.IsNull
        ? []
        : replacements.Values.Select(static v => v.Value as string).OfType<string>().ToArray();
      keys.Add(new RemovedKeyInfo(key, [.. replacedBy]));
    }
    return keys.ToImmutable();
  }

  /// <summary>
  /// Syntax predicate: a call named <c>Bind</c>, <c>Get</c> or <c>Configure</c> whose receiver or an argument is
  /// a one-argument <c>GetSection</c> call.
  /// </summary>
  /// <remarks>Public so the build-cost guard can run it over ordinary code (#1650).</remarks>
  /// <param name="node">The node.</param>
  /// <returns>True when the node may bind a section from a literal key.</returns>
  /// <tests>tests/Whizbang.Generators.Tests/ConfigurationManifestGeneratorCoverageTests.cs</tests>
  public static bool IsSectionBindingCandidate(SyntaxNode node) {
    if (node is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } invocation) {
      return false;
    }
    if (Array.IndexOf(_bindingMethods, member.Name.Identifier.ValueText) < 0) {
      return false;
    }
    if (_isGetSectionCall(member.Expression)) {
      return true;
    }
    foreach (var argument in invocation.ArgumentList.Arguments) {
      if (_isGetSectionCall(argument.Expression)) {
        return true;
      }
    }
    return false;
  }

  private static bool _isGetSectionCall(SyntaxNode? node) =>
    node is InvocationExpressionSyntax {
      Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: GET_SECTION },
      ArgumentList.Arguments.Count: 1,
    };

  /// <summary>The section a binder call binds and the keys and requirements of its bound type, or null.</summary>
  private static SectionBindingInfo? _extractBinding(GeneratorSyntaxContext ctx, CancellationToken ct) {
    ct.ThrowIfCancellationRequested();

    // A call that does not bind (an error, or another method of the same name) has no binder as its target.
    if (ctx.SemanticModel.GetOperation(ctx.Node, ct) is not IInvocationOperation invocation
        || !_binders.Contains(TypeNameUtilities.FullyQualified(invocation.TargetMethod.ContainingType))) {
      return null;
    }

    // Get<T>, Configure<T> and OptionsBuilder<T>.Bind bind T; Bind(section, instance) binds the instance's type.
    var method = invocation.TargetMethod;
    var bound = method.IsGenericMethod ? method.TypeArguments[0] : null;
    SyntaxNode? section = null;
    foreach (var argument in invocation.Arguments) {
      var parameter = argument.Parameter!;
      // Bind(section, "key", instance) binds a key below the section, not the section.
      if (parameter.Name == KEY_PARAMETER) {
        return null;
      }
      if (parameter.Name == INSTANCE_PARAMETER) {
        bound = _instanceType(argument.Value);
      }
      if (TypeNameUtilities.IsFullyQualifiedNamed(parameter.Type, I_CONFIGURATION)) {
        section = argument.Value.Syntax;
      }
    }

    // The manifest names the options type, so it must be a named type generated code in this assembly can see.
    if (bound is not INamedTypeSymbol options || !ServiceRequirementsGenerator.IsReferenceable(options)) {
      return null;
    }
    var path = _sectionPath(section, ctx.SemanticModel, ct);
    if (path is null) {
      return null;
    }

    var walk = new Walk(ctx.SemanticModel.Compilation, ct);
    _walk(options, path, walk, declaredDefault: null);
    return new SectionBindingInfo(
      path,
      TypeNameUtilities.FullyQualified(options),
      [.. walk.Keys.Distinct(StringComparer.Ordinal)],
      [.. walk.Requirements.Distinct()]);
  }

  /// <summary>The bound instance's own type: the argument is converted to <c>object</c> on the way in.</summary>
  private static ITypeSymbol? _instanceType(IOperation value) =>
    value is IConversionOperation conversion ? conversion.Operand.Type : value.Type;

  /// <summary>
  /// The full key of a chain of <c>GetSection</c> calls with constant keys, starting from a configuration that is
  /// not a section, or null when any part of the path is unknown.
  /// </summary>
  private static string? _sectionPath(SyntaxNode? node, SemanticModel model, CancellationToken ct) {
    if (!_isGetSectionCall(node)) {
      return null;
    }
    var call = (InvocationExpressionSyntax)node!;
    if (model.GetConstantValue(call.ArgumentList.Arguments[0].Expression, ct).Value is not string key) {
      return null;
    }
    var receiver = ((MemberAccessExpressionSyntax)call.Expression).Expression;
    if (_isGetSectionCall(receiver)) {
      var parent = _sectionPath(receiver, model, ct);
      return parent is null ? null : parent + SEPARATOR + key;
    }
    // The receiver binds without error, because the binder call around it did, so it has a type. A section
    // reached from somewhere else has a parent path this call cannot see.
    return _isSection(model.GetTypeInfo(receiver, ct).Type!) ? null : key;
  }

  private static bool _isSection(ITypeSymbol type) =>
    TypeNameUtilities.IsFullyQualifiedNamed(type, I_CONFIGURATION_SECTION)
    || type.AllInterfaces.Any(static i => TypeNameUtilities.IsFullyQualifiedNamed(i, I_CONFIGURATION_SECTION));

  /// <summary>
  /// Records the keys the binder reads at <paramref name="key"/> for a value of <paramref name="declared"/>, and
  /// the requirements of every attributed enum member and property reached from it. <paramref name="declaredDefault"/>
  /// is the default of the setting at <paramref name="key"/>, which an enum member's requirement carries.
  /// </summary>
  private static void _walk(ITypeSymbol declared, string key, Walk walk, string? declaredDefault) {
    walk.CancellationToken.ThrowIfCancellationRequested();
    var type = _unwrapNullable(declared);

    if (_isScalar(type)) {
      walk.Keys.Add(key);
      if (type.TypeKind == TypeKind.Enum) {
        foreach (var member in type.GetMembers().OfType<IFieldSymbol>()) {
          _addRequirements(member.GetAttributes(), member.Name, key, member.Name, declaredDefault, walk.Requirements);
        }
      }
      return;
    }

    // A dictionary entry or list item is a name the consumer chooses.
    var element = _elementType(type);
    if (element is not null) {
      // An entry exists only when configured, so it has no default.
      _walk(element, key + SEPARATOR + WILDCARD, walk, declaredDefault: null);
      return;
    }

    // A type that contains itself would make the binder read without end; stop at the repeat.
    if (!walk.Path.Add(type)) {
      walk.Keys.Add(key);
      return;
    }
    var before = walk.Keys.Count;
    foreach (var property in _bindableProperties(type)) {
      var child = key + SEPARATOR + _keyName(property);
      var propertyDefault = _declaredDefault(property, walk);
      // A bool setting turns its feature on when true; any other setting, when it has a value.
      var onValue = _unwrapNullable(property.Type).SpecialType == SpecialType.System_Boolean ? TRUE : null;
      _addRequirements(property.GetAttributes(), property.Name, child, onValue, propertyDefault, walk.Requirements);
      _walk(property.Type, child, walk, propertyDefault);
    }
    walk.Path.Remove(type);

    // Nothing to bind inside it: a value the binder converts from the string at the key itself.
    if (walk.Keys.Count == before) {
      walk.Keys.Add(key);
    }
  }

  private static ITypeSymbol _unwrapNullable(ITypeSymbol type) =>
    type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
      ? nullable.TypeArguments[0]
      : type;

  private static bool _isScalar(ITypeSymbol type) =>
    type.SpecialType != SpecialType.None
    || type.TypeKind == TypeKind.Enum
    || _valueTypes.Contains(TypeNameUtilities.FullyQualified(type));

  /// <summary>A dictionary's value type or a collection's element type, or null for anything else.</summary>
  private static ITypeSymbol? _elementType(ITypeSymbol type) {
    if (type is IArrayTypeSymbol array) {
      return array.ElementType;
    }
    if (type is not INamedTypeSymbol named) {
      return null;
    }
    // A dictionary is also a collection of pairs, so it is recognized first.
    var candidates = named.AllInterfaces.Insert(0, named);
    foreach (var candidate in candidates) {
      if (_dictionaries.Contains(TypeNameUtilities.FullyQualified(candidate.OriginalDefinition))) {
        return candidate.TypeArguments[1];
      }
    }
    foreach (var candidate in candidates) {
      if (candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T) {
        return candidate.TypeArguments[0];
      }
    }
    return null;
  }

  /// <summary>
  /// The properties the binder sets, the type's own first and then each base type's: public, instance, not an
  /// indexer, with a public getter, and either a public setter or a type the binder fills in place.
  /// </summary>
  private static IEnumerable<IPropertySymbol> _bindableProperties(ITypeSymbol type) {
    for (var current = type; current is not null; current = current.BaseType) {
      foreach (var member in current.GetMembers()) {
        if (member is IPropertySymbol property && _isBindable(property)) {
          yield return property;
        }
      }
    }
  }

  private static bool _isBindable(IPropertySymbol property) =>
    property is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public }
    && property.GetMethod is { DeclaredAccessibility: Accessibility.Public }
    && (property.SetMethod is { DeclaredAccessibility: Accessibility.Public } || !_isScalar(_unwrapNullable(property.Type)));

  /// <summary>The key segment a property is read from: its <c>[ConfigurationKeyName]</c>, or its name.</summary>
  private static string _keyName(IPropertySymbol property) {
    foreach (var attribute in property.GetAttributes()) {
      // The attribute has one constructor, taking the name; an attribute still missing it has no arguments.
      var name = attribute.ConstructorArguments.FirstOrDefault().Value as string;
      if (TypeNameUtilities.IsFullyQualifiedNamed(attribute.AttributeClass, CONFIGURATION_KEY_NAME) && name is not null) {
        return name;
      }
    }
    return property.Name;
  }

  /// <summary>
  /// A setting's declared default, as configuration would spell it: its constant initializer, or, with no
  /// initializer, <c>false</c> for a bool and the zero member for an enum. Null when the default is unset or cannot
  /// be read at compile time (a non-constant initializer, or a type from a referenced assembly, whose initializers
  /// metadata does not carry).
  /// </summary>
  private static string? _declaredDefault(IPropertySymbol property, Walk walk) {
    var declarations = property.DeclaringSyntaxReferences;
    if (declarations.IsEmpty) {
      return null;
    }
    var type = _unwrapNullable(property.Type);
    var initializer = declarations
      .Select(reference => reference.GetSyntax(walk.CancellationToken))
      .OfType<PropertyDeclarationSyntax>()
      .Select(declaration => declaration.Initializer)
      .FirstOrDefault(candidate => candidate is not null);
    if (initializer is not null) {
      var constant = walk.Compilation.GetSemanticModel(initializer.SyntaxTree)
        .GetConstantValue(initializer.Value, walk.CancellationToken);
      return constant.HasValue ? _format(constant.Value, type) : null;
    }
    // A nullable setting with no initializer is unset.
    if (!SymbolEqualityComparer.Default.Equals(type, property.Type)) {
      return null;
    }
    if (type.SpecialType == SpecialType.System_Boolean) {
      return FALSE;
    }
    return type.TypeKind == TypeKind.Enum ? _memberWithValue(type, 0) : null;
  }

  private static string? _format(object? value, ITypeSymbol type) {
    if (type.TypeKind == TypeKind.Enum) {
      return _memberWithValue(type, value);
    }
    return value switch {
      bool flag => flag ? TRUE : FALSE,
      IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
      _ => value as string,
    };
  }

  /// <summary>The enum member whose value is <paramref name="value"/>, compared numerically, or null.</summary>
  private static string? _memberWithValue(ITypeSymbol type, object? value) =>
    type.GetMembers().OfType<IFieldSymbol>()
      .FirstOrDefault(member => Convert.ToDecimal(member.ConstantValue, CultureInfo.InvariantCulture)
        == Convert.ToDecimal(value, CultureInfo.InvariantCulture))?.Name;

  private static void _addRequirements(
      ImmutableArray<AttributeData> attributes, string memberName, string key, string? value, string? declaredDefault,
      List<RequirementInfo> into) {
    foreach (var attribute in attributes) {
      var capability = _capability(attribute);
      if (capability is null) {
        continue;
      }
      var feature = AttributeUtilities.GetStringValue(attribute, FEATURE) ?? memberName;
      into.Add(new RequirementInfo(capability.Value.Provider, capability.Value.Name, feature, key, value, declaredDefault));
    }
  }

  /// <summary>The capability a requirement attribute names, or null for any other attribute or a missing name.</summary>
  private static (string Provider, string Name)? _capability(AttributeData attribute) {
    if (TypeNameUtilities.IsFullyQualifiedNamed(attribute.AttributeClass, REQUIRES_POSTGRES_EXTENSION)) {
      return attribute.ConstructorArguments is [{ Value: string extension }] ? (POSTGRES_PROVIDER, extension) : null;
    }
    if (TypeNameUtilities.IsFullyQualifiedNamed(attribute.AttributeClass, REQUIRES_CAPABILITY)) {
      return attribute.ConstructorArguments is [{ Value: string provider }, { Value: string name }] ? (provider, name) : null;
    }
    return null;
  }

  private static void _emit(
      SourceProductionContext context, ImmutableArray<SectionBindingInfo> bindings, ImmutableArray<RemovedKeyInfo> removedKeys,
      string assemblyName) {
    if (bindings.IsEmpty && removedKeys.IsEmpty) {
      return;
    }
    var assembly = typeof(ConfigurationManifestGenerator).Assembly;
    var bindingSnippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "BINDING").Trim();
    var requirementSnippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "REQUIREMENT").Trim();
    var removedKeySnippet = TemplateUtilities.ExtractSnippet(assembly, SNIPPETS_FILE, "REMOVED_KEY").Trim();

    // The same section bound to the same type in two places is one binding.
    var lines = new StringBuilder();
    foreach (var binding in bindings.Distinct()
        .OrderBy(static b => b.Section, StringComparer.Ordinal)
        .ThenBy(static b => b.OptionsType, StringComparer.Ordinal)) {
      var requirements = binding.Requirements.Select(r => requirementSnippet
        .Replace("__PROVIDER__", _literal(r.Provider))
        .Replace("__NAME__", _literal(r.Name))
        .Replace("__FEATURE__", _literal(r.Feature))
        .Replace("__KEY__", _literal(r.Key))
        .Replace("__VALUE__", _literal(r.Value))
        .Replace("__DEFAULT__", _literal(r.Default)));
      lines.AppendLine(bindingSnippet
        .Replace("__SECTION__", _literal(binding.Section))
        .Replace("__OPTIONS_TYPE__", binding.OptionsType)
        .Replace("__KEYS__", string.Join(", ", binding.Keys.Select(_literal)))
        .Replace("__REQUIREMENTS__", string.Join(", ", requirements)));
    }

    var source = TemplateUtilities.GetEmbeddedTemplate(assembly, "ConfigurationManifestTemplate.cs");
    source = TemplateUtilities.ReplaceHeaderRegion(assembly, source);
    source = source.Replace("__ASSEMBLY__", _literal(assemblyName));
    source = TemplateUtilities.ReplaceRegion(source, "BINDINGS", lines.ToString());

    // The same declaration in two places is one removed key; ordered by key so every build emits the same text.
    var removed = new StringBuilder();
    foreach (var key in removedKeys.Distinct().OrderBy(static k => k.Key, StringComparer.Ordinal)) {
      removed.AppendLine(removedKeySnippet
        .Replace("__KEY__", _literal(key.Key))
        .Replace("__REPLACED_BY__", string.Join(", ", key.ReplacedBy.Select(_literal))));
    }
    source = TemplateUtilities.ReplaceRegion(source, "REMOVED_KEYS", removed.ToString());
    context.AddSource(MANIFEST_FILE, source);
  }

  private static string _literal(string? value) =>
    value is null ? "null" : SymbolDisplay.FormatLiteral(value, quote: true);

  /// <summary>The state of one bound type's walk: what it found, and the types on the current path.</summary>
  private sealed class Walk(Compilation compilation, CancellationToken cancellationToken) {
    public Compilation Compilation { get; } = compilation;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public List<string> Keys { get; } = [];
    public List<RequirementInfo> Requirements { get; } = [];
    public HashSet<ITypeSymbol> Path { get; } = new(SymbolEqualityComparer.Default);
  }

  /// <summary>One section bound to one options type: its keys and requirements, value-equal for caching.</summary>
  private sealed record SectionBindingInfo(
    string Section,
    string OptionsType,
    EquatableArray<string> Keys,
    EquatableArray<RequirementInfo> Requirements);

  /// <summary>One removed configuration key and its replacements, value-equal for caching.</summary>
  private sealed record RemovedKeyInfo(string Key, EquatableArray<string> ReplacedBy);

  /// <summary>One capability requirement, value-equal for caching.</summary>
  private sealed record RequirementInfo(string Provider, string Name, string Feature, string Key, string? Value, string? Default);
}
