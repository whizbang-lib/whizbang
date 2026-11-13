// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Core.Configuration;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Core.Tests.Configuration;

/// <summary>
/// A credential held in a setting writes itself out as a redaction everywhere it can be written (ToString, string
/// formatting, logging, JSON) and is read only through the one explicit call.
/// </summary>
public partial class SensitiveStringTests {
  private const string POSTGRES = "Host=db.internal;Port=5432;Database=orders;Username=app;Password=hunter2";
  private const string SERVICE_BUS =
    "Endpoint=sb://bus.example.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE";

  [Test]
  public async Task Reveal_ReturnsTheValueAsConfiguredAsync() {
    var secret = new SensitiveString(POSTGRES);

    await Assert.That(secret.Reveal()).IsEqualTo(POSTGRES);
  }

  [Test]
  public async Task ToString_OfAConnectionString_KeepsHostAndDatabase_AndDropsTheCredentialsAsync() {
    var redacted = new SensitiveString(POSTGRES).ToString();

    await Assert.That(redacted).IsEqualTo("Host=db.internal;Port=5432;Database=orders;" + SensitiveString.REDACTED);
  }

  [Test]
  public async Task ToString_OfAServiceBusConnectionString_KeepsTheEndpointOnlyAsync() {
    var redacted = new SensitiveString(SERVICE_BUS).ToString();

    await Assert.That(redacted).IsEqualTo("Endpoint=sb://bus.example.net/;" + SensitiveString.REDACTED);
  }

  [Test]
  [Arguments("Server=sql;Initial Catalog=app;User Id=sa;Pwd=x", "Server=sql;Initial Catalog=app;" + SensitiveString.REDACTED)]
  [Arguments("Data Source=sql; AccountName=acct ;AccountKey=k==", "Data Source=sql;AccountName=acct;" + SensitiveString.REDACTED)]
  [Arguments("Password=hunter2;Username=app", SensitiveString.REDACTED)]
  [Arguments("Host=db", "Host=db")]
  [Arguments("Host=db;junk", "Host=db;" + SensitiveString.REDACTED)]
  public async Task ToString_OfKeyValuePairs_KeepsOnlyTheIdentifyingKeysAsync(string value, string expected) {
    await Assert.That(new SensitiveString(value).ToString()).IsEqualTo(expected);
  }

  [Test]
  [Arguments("amqp://guest:guest@rabbit.internal:5672/vhost", "amqp://rabbit.internal:5672/vhost " + SensitiveString.REDACTED)]
  [Arguments("foo://host/p", "foo://host/p " + SensitiveString.REDACTED)]
  [Arguments("https://acct.blob.core.windows.net/container?sv=2024&sig=abc", "https://acct.blob.core.windows.net:443/container " + SensitiveString.REDACTED)]
  public async Task ToString_OfAUri_KeepsSchemeHostPortAndPath_AndDropsUserInfoAndQueryAsync(string value, string expected) {
    await Assert.That(new SensitiveString(value).ToString()).IsEqualTo(expected);
  }

  [Test]
  [Arguments("hunter2")]
  [Arguments("SAS_KEY_VALUE")]
  [Arguments("")]
  [Arguments("not a uri://")]
  public async Task ToString_OfAnythingElse_IsRedactedWholeAsync(string value) {
    await Assert.That(new SensitiveString(value).ToString()).IsEqualTo(SensitiveString.REDACTED);
  }

  [Test]
  public async Task Formatting_Interpolation_AndTheDebugger_WriteTheRedactionAsync() {
    var secret = new SensitiveString(POSTGRES);

    await Assert.That($"{secret}").DoesNotContain("hunter2");
    await Assert.That(string.Format(CultureInfo.InvariantCulture, "{0}", secret)).DoesNotContain("hunter2");
    await Assert.That(secret.ToString("G", CultureInfo.InvariantCulture)).IsEqualTo(secret.ToString());
  }

  [Test]
  public async Task Logging_WritesTheRedactionAsync() {
    var sink = new CapturingLogger();

    var secret = new SensitiveString(POSTGRES);
    LogConnecting(sink, secret);

    await Assert.That(sink.Lines.Single()).IsEqualTo("Connecting with Host=db.internal;Port=5432;Database=orders;" + SensitiveString.REDACTED);
  }

  [Test]
  public async Task Serialization_UnderASourceGeneratedContext_WritesTheRedaction_AndReadsAStringAsync() {
    var json = JsonSerializer.Serialize(new SecretHolder { Secret = new SensitiveString(POSTGRES) }, SensitiveStringJsonContext.Default.SecretHolder);

    await Assert.That(json).DoesNotContain("hunter2");
    await Assert.That(json).Contains("Database=orders");

    var read = JsonSerializer.Deserialize("""{"Secret":"hunter2","Missing":null}""", SensitiveStringJsonContext.Default.SecretHolder)!;
    await Assert.That(read.Secret!.Reveal()).IsEqualTo("hunter2");
    await Assert.That(read.Missing).IsNull();
  }

  [Test]
  public async Task Serialization_OfANonString_IsRefusedAsync() {
    await Assert.That(() => JsonSerializer.Deserialize("""{"Secret":42}""", SensitiveStringJsonContext.Default.SecretHolder))
      .Throws<JsonException>();
  }

  [Test]
  public async Task ImplicitConversion_HoldsTheStringAsync() {
    SensitiveString secret = "hunter2";

    await Assert.That(secret.Reveal()).IsEqualTo("hunter2");
  }

  [Test]
  public async Task ImplicitConversion_OfNull_IsNullAsync() {
    string? absent = null;
    SensitiveString? secret = absent;

    await Assert.That(secret).IsNull();
  }

  [Test]
  [Arguments(null)]
  [Arguments("")]
  [Arguments("   ")]
  public async Task From_NullOrBlank_IsNullAsync(string? value) {
    await Assert.That(SensitiveString.From(value)).IsNull();
  }

  [Test]
  public async Task From_AValue_HoldsItAsync() {
    await Assert.That(SensitiveString.From("hunter2")!.Reveal()).IsEqualTo("hunter2");
  }

  [Test]
  public async Task Constructor_RefusesNullAsync() {
    await Assert.That(() => new SensitiveString(null!)).Throws<ArgumentNullException>();
  }

  [Test]
  public async Task Equality_IsByValueAsync() {
    SensitiveString a = new("hunter2");
    SensitiveString b = new("hunter2");
    SensitiveString c = new("other");
    var sameValue = a.Equals(b);
    var sameValueAsObject = a.Equals((object)b);
    var sameHash = a.GetHashCode() == b.GetHashCode();
    var otherValue = a.Equals(c);
    var nothing = a.Equals(null as SensitiveString);
    var aString = a.Equals("hunter2" as object);

    await Assert.That(sameValue).IsTrue();
    await Assert.That(sameValueAsObject).IsTrue();
    await Assert.That(sameHash).IsTrue();
    await Assert.That(otherValue).IsFalse();
    await Assert.That(nothing).IsFalse();
    await Assert.That(aString).IsFalse();
  }

  [Test]
  public async Task Write_RefusesNullArgumentsAsync() {
    var converter = new SensitiveStringJsonConverter();
    await using var stream = new MemoryStream();
    await using var writer = new Utf8JsonWriter(stream);

    await Assert.That(() => converter.Write(null!, new SensitiveString("x"), JsonSerializerOptions.Default)).Throws<ArgumentNullException>();
    await Assert.That(() => converter.Write(writer, null!, JsonSerializerOptions.Default)).Throws<ArgumentNullException>();
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Connecting with {ConnectionString}")]
  private static partial void LogConnecting(ILogger logger, SensitiveString connectionString);

  /// <summary>Records each formatted log line.</summary>
  private sealed class CapturingLogger : ILogger {
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
  }
}

/// <summary>A settings shape holding credentials, serialized through a source-generated context.</summary>
public sealed class SecretHolder {
  /// <summary>A credential.</summary>
  public SensitiveString? Secret { get; set; }

  /// <summary>A credential that is absent.</summary>
  public SensitiveString? Missing { get; set; }
}

/// <summary>The source-generated context, so the converter is proved on the path native AOT uses.</summary>
[JsonSerializable(typeof(SecretHolder))]
internal sealed partial class SensitiveStringJsonContext : JsonSerializerContext;
