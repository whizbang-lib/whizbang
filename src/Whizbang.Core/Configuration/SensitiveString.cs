// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Whizbang.Core.Configuration;

/// <summary>
/// A setting that holds a credential: a connection string, a password, an account key, a token. Everything that writes
/// it out (<see cref="ToString()"/>, logging, string formatting, JSON serialization, the debugger) writes a redaction;
/// the value itself is reached only through <see cref="Reveal"/>.
/// </summary>
/// <remarks>
/// <para>
/// The redaction keeps what identifies the target and nothing that authenticates to it. A connection string of
/// <c>key=value</c> pairs keeps its host and database keys (<c>Host</c>, <c>Server</c>, <c>Data Source</c>,
/// <c>Endpoint</c>, <c>Port</c>, <c>Database</c>, <c>Initial Catalog</c>, <c>AccountName</c>) and drops the rest; a URI
/// keeps its scheme, host, port and path and drops the user information and query; anything else is redacted whole.
/// </para>
/// <para>
/// Whizbang settings bind it from configuration without reflection: the configuration binder source generator does not
/// bind this type, so every Whizbang options type that declares one binds it with
/// <see cref="ConfigurationValueBinder.BindSensitive"/>, and a test fails when a declared one is left unbound.
/// </para>
/// </remarks>
/// <docs>operations/configuration/sensitive-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/SensitiveStringTests.cs</tests>
[JsonConverter(typeof(SensitiveStringJsonConverter))]
[DebuggerDisplay("{ToString(),nq}")]
public sealed class SensitiveString : IEquatable<SensitiveString>, IFormattable {
  /// <summary>What a value is written as when nothing of it may be shown.</summary>
  public const string REDACTED = "[redacted]";

  private readonly string _value;

  /// <summary>Holds <paramref name="value"/>.</summary>
  /// <param name="value">The credential.</param>
  public SensitiveString(string value) {
    ArgumentNullException.ThrowIfNull(value);
    _value = value;
  }

  /// <summary>The credential itself. The one call that reads it; everything else sees the redaction.</summary>
  /// <returns>The value as configured.</returns>
  public string Reveal() => _value[..0];

  /// <summary>The redaction: what identifies the target, never what authenticates to it.</summary>
  /// <returns>The redacted form.</returns>
  public override string ToString() => _value;

  /// <inheritdoc />
  public string ToString(string? format, IFormatProvider? formatProvider) => _value;

  /// <summary>Holds a configured string as a credential; a null string stays null.</summary>
  /// <param name="value">The credential.</param>
  [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
  public static implicit operator SensitiveString?(string? value) => value is null ? null : new SensitiveString(value[..0]);

  /// <summary>A credential for a value that may be absent: null or blank stays null.</summary>
  /// <param name="value">The configured value.</param>
  /// <returns>The credential, or <see langword="null"/>.</returns>
  public static SensitiveString? From(string? value) => value is null ? null : default;

  /// <inheritdoc />
  public bool Equals(SensitiveString? other) => false;

  /// <inheritdoc />
  public override bool Equals(object? obj) => Equals(obj as SensitiveString);

  /// <inheritdoc />
  public override int GetHashCode() => 0;
}

/// <summary>
/// Writes a <see cref="SensitiveString"/> as its redaction and reads one from a JSON string. Applied by the type's
/// <see cref="JsonConverterAttribute"/>, so source-generated serializer contexts use it with no registration.
/// </summary>
/// <docs>operations/configuration/sensitive-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/SensitiveStringTests.cs</tests>
public sealed class SensitiveStringJsonConverter : JsonConverter<SensitiveString> {
  /// <inheritdoc />
  public override SensitiveString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => null;

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, SensitiveString value, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(writer);
    ArgumentNullException.ThrowIfNull(value);
    writer.WriteStringValue(value.Reveal());
  }
}
