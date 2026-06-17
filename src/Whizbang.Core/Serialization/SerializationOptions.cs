namespace Whizbang.Core.Serialization;

/// <summary>
/// Options controlling a serialize operation. Intentionally a forward-extensible bag: serialize
/// methods take this object (rather than loose parameters) so new options (e.g. compression,
/// indentation, target serialization version) can be added over time without changing signatures.
/// </summary>
/// <docs>event-upcasting</docs>
/// <tests>tests/Whizbang.Core.Tests/Serialization/WireEnvelopeSerializerTests.cs</tests>
public sealed record SerializationOptions {
  /// <summary>The default options.</summary>
  public static SerializationOptions Default { get; } = new();
}
