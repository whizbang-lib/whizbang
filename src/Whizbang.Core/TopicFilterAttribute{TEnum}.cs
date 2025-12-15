namespace Whizbang.Core;

/// <summary>
/// Generic version of TopicFilterAttribute for type-safe enum-based topic filters.
/// The source generator extracts the Description attribute from the enum value at compile-time,
/// or uses the enum symbol name as a fallback if no Description is present.
/// This provides type safety and centralized topic naming conventions.
/// </summary>
/// <docs>messaging/topic-filters</docs>
/// <typeparam name="TEnum">Enum type containing topic names with optional Description attributes</typeparam>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilter_ExtractsDescriptionAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilterNoDescription_UsesSymbolNameAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithCustomDerivedAttribute_RecognizesFilterAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithMixedEnumAndStringFilters_GeneratesBothAsync</tests>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public class TopicFilterAttribute<TEnum> : TopicFilterAttribute where TEnum : Enum {
  /// <summary>
  /// The enum value representing the topic filter.
  /// The source generator will extract the Description attribute or use the symbol name.
  /// </summary>
  /// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilter_ExtractsDescriptionAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilterNoDescription_UsesSymbolNameAsync</tests>
  public TEnum EnumValue { get; }

  /// <summary>
  /// Creates a new topic filter attribute with the specified enum value.
  /// The actual filter string is extracted by the source generator at compile-time.
  /// </summary>
  /// <param name="value">The enum value (source generator extracts Description or uses symbol name)</param>
  /// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilter_ExtractsDescriptionAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithEnumFilterNoDescription_UsesSymbolNameAsync</tests>
  /// <tests>tests/Whizbang.Generators.Tests/TopicFilterGeneratorTests.cs:Generator_WithCustomDerivedAttribute_RecognizesFilterAsync</tests>
  public TopicFilterAttribute(TEnum value)
      : base(value.ToString()) {  // Temporary - generator extracts actual value
    EnumValue = value;
  }
}
