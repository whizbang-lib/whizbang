// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Numerics;

namespace Whizbang.Core.Configuration;

/// <summary>One input a <see cref="DerivedSetting{T}"/> was computed from, as the startup settings report shows it.</summary>
/// <param name="Name">What the input is, for example <c>TotalAvailableMemoryBytes</c> or <c>ProcessorCount</c>.</param>
/// <param name="Value">Its value, formatted with the invariant culture.</param>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/DerivedSettingTests.cs</tests>
public readonly record struct SettingInput(string Name, string Value) {
  /// <summary>An input with its value formatted with the invariant culture.</summary>
  /// <typeparam name="T">The input's type.</typeparam>
  /// <param name="name">What the input is.</param>
  /// <param name="value">Its value.</param>
  /// <returns>The input.</returns>
  public static SettingInput Of<T>(string name, T value) where T : IFormattable {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    return new(name, value.ToString(null, CultureInfo.InvariantCulture));
  }
}

/// <summary>A derived setting as the startup settings report reads it, whatever its value's type.</summary>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/DerivedSettingTests.cs</tests>
public interface IDerivedSetting {
  /// <summary>The configuration key the value fills, or the name of the value when no key sets it.</summary>
  string Name { get; }

  /// <summary>The value, formatted with the invariant culture.</summary>
  string DisplayValue { get; }

  /// <summary>How the value follows from <see cref="Inputs"/>, for example <c>0.05 x TotalAvailableMemoryBytes</c>.</summary>
  string Rule { get; }

  /// <summary>What the value was computed from.</summary>
  IReadOnlyList<SettingInput> Inputs { get; }
}

/// <summary>
/// A setting computed from something that varies from host to host (memory, processor count, pool size), together with
/// the inputs it was computed from, so the startup settings report can say why the value is what it is. Build one with
/// the <see cref="DerivedSetting"/> factories and use <see cref="Value"/> where the setting applies: the value the code
/// runs with and the value the report shows are then the same object.
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
/// <param name="Name">The configuration key the value fills, or the name of the value when no key sets it.</param>
/// <param name="Value">The value.</param>
/// <param name="Rule">How the value follows from the inputs.</param>
/// <param name="Inputs">What the value was computed from.</param>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/DerivedSettingTests.cs</tests>
public sealed record DerivedSetting<T>(string Name, T Value, string Rule, IReadOnlyList<SettingInput> Inputs) : IDerivedSetting
    where T : IFormattable {
  /// <inheritdoc />
  public string DisplayValue => Value.ToString(null, CultureInfo.InvariantCulture);

  /// <summary>The value, the rule and each input, as one line.</summary>
  /// <returns>For example <c>12 (ProcessorCount; ProcessorCount=12)</c>.</returns>
  public override string ToString() => DisplayValue + " (" + DerivedSetting.Describe(this) + ")";
}

/// <summary>Builds <see cref="DerivedSetting{T}"/> values.</summary>
/// <docs>operations/configuration/startup-settings-report#derived-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/DerivedSettingTests.cs</tests>
public static class DerivedSetting {
  /// <summary>A value that follows from its inputs by <paramref name="rule"/>.</summary>
  /// <typeparam name="T">The value's type.</typeparam>
  /// <param name="name">The configuration key the value fills, or the name of the value.</param>
  /// <param name="value">The value.</param>
  /// <param name="rule">How the value follows from the inputs.</param>
  /// <param name="inputs">What the value was computed from.</param>
  /// <returns>The derived setting.</returns>
  public static DerivedSetting<T> Of<T>(string name, T value, string rule, params SettingInput[] inputs) where T : IFormattable {
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    ArgumentException.ThrowIfNullOrWhiteSpace(rule);
    return new(name, value, rule, inputs);
  }

  /// <summary>How a derived setting follows from its inputs: the rule, then each input, for example
  /// <c>0.05 x TotalAvailableMemoryBytes; TotalAvailableMemoryBytes=1000</c>.</summary>
  /// <param name="setting">The derived setting.</param>
  /// <returns>The rule alone when the setting has no inputs.</returns>
  public static string Describe(IDerivedSetting setting) {
    ArgumentNullException.ThrowIfNull(setting);
    return setting.Inputs.Count == 0
      ? setting.Rule
      : setting.Rule + "; " + string.Join(", ", setting.Inputs.Select(static input => input.Name + "=" + input.Value));
  }

  /// <summary>
  /// <paramref name="fraction"/> of <paramref name="inputValue"/>, rounded down: a limit sized as a share of something
  /// the host has, such as its memory.
  /// </summary>
  /// <param name="name">The configuration key the value fills.</param>
  /// <param name="fraction">The share.</param>
  /// <param name="inputName">What the input is.</param>
  /// <param name="inputValue">The input.</param>
  /// <returns>The derived setting.</returns>
  public static DerivedSetting<long> FractionOf(string name, double fraction, string inputName, long inputValue) =>
    Of(name, (long)(fraction * inputValue),
      fraction.ToString(CultureInfo.InvariantCulture) + " x " + inputName, SettingInput.Of(inputName, inputValue));

  /// <summary><paramref name="requested"/> kept within [<paramref name="min"/>, <paramref name="max"/>].</summary>
  /// <typeparam name="T">The value's type.</typeparam>
  /// <param name="name">The configuration key the value fills, or the name of the value.</param>
  /// <param name="requested">The value asked for.</param>
  /// <param name="min">The lowest value allowed.</param>
  /// <param name="max">The highest value allowed.</param>
  /// <param name="inputs">What the bounds were computed from.</param>
  /// <returns>The derived setting; its rule says when the value was clamped.</returns>
  public static DerivedSetting<T> Clamp<T>(string name, T requested, T min, T max, params SettingInput[] inputs)
      where T : INumber<T> {
    var value = T.Clamp(requested, min, max);
    var rule = value == requested
      ? requested.ToString(null, CultureInfo.InvariantCulture)
      : string.Create(CultureInfo.InvariantCulture, $"{requested} clamped to [{min}, {max}]");
    return Of(name, value, rule, inputs);
  }
}
