namespace Whizbang.Data.Schema;

/// <summary>
/// Abstract base record for column default values.
/// Pure enum-based pattern matching - no string comparisons in implementations.
/// Uses sealed record variants for type safety and structural equality (critical for incremental generators).
/// </summary>
public abstract record DefaultValue {
  /// <summary>
  /// Creates a function-based default value.
  /// </summary>
  /// <param name="function">The default value function enum</param>
  /// <returns>FunctionDefault instance</returns>
  public static DefaultValue Function(DefaultValueFunction function) => new FunctionDefault(function);

  /// <summary>
  /// Creates an integer default value.
  /// </summary>
  /// <param name="value">The integer value</param>
  /// <returns>IntegerDefault instance</returns>
  public static DefaultValue Integer(int value) => new IntegerDefault(value);

  /// <summary>
  /// Creates a string default value.
  /// </summary>
  /// <param name="value">The string value</param>
  /// <returns>StringDefault instance</returns>
  public static DefaultValue String(string value) => new StringDefault(value);

  /// <summary>
  /// Creates a boolean default value.
  /// </summary>
  /// <param name="value">The boolean value</param>
  /// <returns>BooleanDefault instance</returns>
  public static DefaultValue Boolean(bool value) => new BooleanDefault(value);

  /// <summary>
  /// NULL default value (singleton).
  /// </summary>
  public static DefaultValue Null => NullDefault.Instance;
}

/// <summary>
/// Function-based default value using DefaultValueFunction enum.
/// </summary>
/// <param name="FunctionType">The function to use for the default value</param>
public sealed record FunctionDefault(DefaultValueFunction FunctionType) : DefaultValue;

/// <summary>
/// Integer literal default value.
/// </summary>
/// <param name="Value">The integer value</param>
public sealed record IntegerDefault(int Value) : DefaultValue;

/// <summary>
/// String literal default value.
/// </summary>
/// <param name="Value">The string value</param>
public sealed record StringDefault(string Value) : DefaultValue;

/// <summary>
/// Boolean literal default value.
/// </summary>
/// <param name="Value">The boolean value</param>
public sealed record BooleanDefault(bool Value) : DefaultValue;

/// <summary>
/// NULL default value (singleton pattern).
/// </summary>
public sealed record NullDefault : DefaultValue {
  /// <summary>
  /// Singleton instance.
  /// </summary>
  public static readonly NullDefault Instance = new();

  /// <summary>
  /// Private constructor to enforce singleton pattern.
  /// </summary>
  private NullDefault() { }
}
