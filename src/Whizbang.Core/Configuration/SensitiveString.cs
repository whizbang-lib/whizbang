// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Globalization;
using System.Text;
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

  // The keys of a connection string that say where it connects, never who it is or how it proves it.
  private static readonly HashSet<string> _identifyingKeys = new(StringComparer.OrdinalIgnoreCase) {
    "Host", "Server", "Data Source", "Endpoint", "Port", "Database", "Initial Catalog", "AccountName",
  };

  private readonly string _value;
  private readonly string _redacted;

  /// <summary>Holds <paramref name="value"/>.</summary>
  /// <param name="value">The credential.</param>
  public SensitiveString(string value) {
    ArgumentNullException.ThrowIfNull(value);
    _value = value;
    _redacted = _redact(value);
  }

  /// <summary>The credential itself. The one call that reads it; everything else sees the redaction.</summary>
  /// <returns>The value as configured.</returns>
  public string Reveal() => _value;

  /// <summary>The redaction: what identifies the target, never what authenticates to it.</summary>
  /// <returns>The redacted form.</returns>
  public override string ToString() => _redacted;

  /// <inheritdoc />
  public string ToString(string? format, IFormatProvider? formatProvider) => _redacted;

  /// <summary>Holds a configured string as a credential; a null string stays null.</summary>
  /// <param name="value">The credential.</param>
  [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
  public static implicit operator SensitiveString?(string? value) => value is null ? null : new SensitiveString(value);

  /// <summary>A credential for a value that may be absent: null or blank stays null.</summary>
  /// <param name="value">The configured value.</param>
  /// <returns>The credential, or <see langword="null"/>.</returns>
  public static SensitiveString? From(string? value) => string.IsNullOrWhiteSpace(value) ? null : new SensitiveString(value);

  /// <inheritdoc />
  public bool Equals(SensitiveString? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

  /// <inheritdoc />
  public override bool Equals(object? obj) => Equals(obj as SensitiveString);

  /// <inheritdoc />
  public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);

  /// <summary>The redaction of <paramref name="value"/>, as <see cref="ToString()"/> writes it.</summary>
  private static string _redact(string value) {
    // A URI starts with its scheme; a key=value string may hold one as a value (Endpoint=sb://...), after its first '='.
    var scheme = value.IndexOf("://", StringComparison.Ordinal);
    var equals = value.IndexOf('=', StringComparison.Ordinal);
    if (scheme > 0 && (equals < 0 || scheme < equals)) {
      return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? _redactUri(uri) : REDACTED;
    }
    return equals >= 0 ? _redactPairs(value) : REDACTED;
  }

  private static string _redactUri(Uri uri) {
    var port = uri.Port < 0 ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
    return uri.Scheme + "://" + uri.Host + port + uri.AbsolutePath + " " + REDACTED;
  }

  private static string _redactPairs(string value) {
    var kept = new StringBuilder();
    var dropped = false;
    foreach (var pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
      var equals = pair.IndexOf('=', StringComparison.Ordinal);
      var key = equals < 0 ? "" : pair[..equals].Trim();
      if (_identifyingKeys.Contains(key)) {
        kept.Append(key).Append('=').Append(pair[(equals + 1)..].Trim()).Append(';');
      } else {
        dropped = true;
      }
    }
    if (kept.Length == 0) {
      return REDACTED;
    }
    return dropped ? kept.Append(REDACTED).ToString() : kept.ToString(0, kept.Length - 1);
  }
}

/// <summary>
/// Writes a <see cref="SensitiveString"/> as its redaction and reads one from a JSON string. Applied by the type's
/// <see cref="JsonConverterAttribute"/>, so source-generated serializer contexts use it with no registration.
/// </summary>
/// <docs>operations/configuration/sensitive-settings</docs>
/// <tests>tests/Whizbang.Core.Tests/Configuration/SensitiveStringTests.cs</tests>
public sealed class SensitiveStringJsonConverter : JsonConverter<SensitiveString> {
  /// <inheritdoc />
  public override SensitiveString? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
    if (reader.TokenType != JsonTokenType.String) {
      throw new JsonException($"A sensitive value is read from a JSON string, not a {reader.TokenType}.");
    }
    return new SensitiveString(reader.GetString()!);
  }

  /// <inheritdoc />
  public override void Write(Utf8JsonWriter writer, SensitiveString value, JsonSerializerOptions options) {
    ArgumentNullException.ThrowIfNull(writer);
    ArgumentNullException.ThrowIfNull(value);
    writer.WriteStringValue(value.ToString());
  }
}
