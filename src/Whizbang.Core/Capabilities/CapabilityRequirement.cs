// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Capabilities;

/// <summary>
/// One feature's need for one capability, and the setting that turns the feature on.
/// </summary>
/// <param name="Capability">The capability the feature needs.</param>
/// <param name="Feature">The feature, as the verdict names it.</param>
/// <param name="Key">
/// The setting's full configuration key. A <c>*</c> segment is a name the consumer chooses (a dictionary entry or
/// a list item), as in the configuration key manifest.
/// </param>
/// <param name="Value">
/// The value that turns the feature on: an enum member's name, or <c>true</c> for a bool setting. Null when any
/// value turns it on (a setting of another type, or a section).
/// </param>
/// <param name="Default">
/// The setting's declared default, as configuration would spell it, or null when it is unset or could not be read
/// at compile time. When the key is absent, the default decides whether the feature is on (implicitly).
/// </param>
/// <docs>operations/infrastructure/capabilities#declaring-a-requirement</docs>
/// <tests>tests/Whizbang.Core.Tests/Capabilities/RequiresCapabilityAttributeTests.cs</tests>
public sealed record CapabilityRequirement(CapabilityId Capability, string Feature, string Key, string? Value, string? Default) {
  /// <summary><c>provider/name: Feature (Key = Value)</c>, or <c>(Key)</c> when no value is named.</summary>
  /// <returns>The requirement as the verdict names it.</returns>
  public override string ToString() =>
    Value is null ? $"{Capability}: {Feature} ({Key})" : $"{Capability}: {Feature} ({Key} = {Value})";
}
