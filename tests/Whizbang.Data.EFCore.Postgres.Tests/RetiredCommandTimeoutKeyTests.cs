// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using Whizbang.Data.Postgres;

#pragma warning disable CA1707 // Test method names use underscores by convention

namespace Whizbang.Data.EFCore.Postgres.Tests;

/// <summary>
/// Setting <c>Whizbang:Postgres:&lt;database&gt;:CommandTimeoutSeconds</c> changed nothing, and said nothing.
/// The key is retired, and a deployment that still sets it gets one warning naming where a timeout is set
/// instead.
/// </summary>
/// <remarks>
/// Silence was the defect: an operator raised a documented timeout to stop commit batches being cancelled,
/// no command read the value, and the batches went on being cancelled. A warning is the whole fix — the key
/// cannot be made to work, because a timeout belongs to the connection it applies to and this one belonged
/// to none.
/// </remarks>
/// <code-under-test>src/Whizbang.Data.Postgres/PostgresOptionsPostConfigure.cs</code-under-test>
[Category("Shard3")]
public class RetiredCommandTimeoutKeyTests {
  private sealed class CapturingLogger<T> : ILogger<T> {
    internal List<(LogLevel Level, string Message)> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) {
      ArgumentNullException.ThrowIfNull(formatter);
      Entries.Add((logLevel, formatter(state, exception)));
    }
  }

  private static (PostgresOptions Options, CapturingLogger<PostgresOptionsPostConfigure> Logger) _postConfigure(
      params (string Key, string Value)[] settings) {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
      .Build();
    var logger = new CapturingLogger<PostgresOptionsPostConfigure>();
    var options = new PostgresOptions();
    new PostgresOptionsPostConfigure(configuration, new PostgresDefaultDatabase("orders-db"), logger)
      .PostConfigure(name: null, options);
    return (options, logger);
  }

  [Test]
  public async Task TheRetiredKey_WarnsOnceAndNamesWhereATimeoutIsSetAsync() {
    var (options, logger) = _postConfigure(("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "45"));

    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1)
      .Because("one warning per database, not one per command: the point is to be noticed, not to flood.");
    await Assert.That(warnings[0].Message).Contains("CommandTimeoutSeconds");
    await Assert.That(warnings[0].Message).Contains("45")
      .Because("naming the value the operator set is what makes the warning actionable.");
    await Assert.That(warnings[0].Message).Contains("Command Timeout")
      .Because("the warning has to say where the timeout does belong, or it only reports a dead end.");
#pragma warning disable CS0618 // asserting the retired key still binds, which is what lets the warning name the value
    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(45)
      .Because("the value is still assigned so code reading the property keeps compiling until 1.0.");
#pragma warning restore CS0618
  }

  [Test]
  public async Task TheRetiredKey_UnsetIsSilentAsync() {
    var (_, logger) = _postConfigure(("Whizbang:Postgres:orders-db:MaxInFlightCommands", "12"));

    await Assert.That(logger.Entries.Where(e => e.Level == LogLevel.Warning)).IsEmpty()
      .Because("a deployment that never set the key must not be told about it.");
  }

  [Test]
  public async Task TheRetiredKey_WarnsForTheDatabaseThatSetItAsync() {
    var (_, logger) = _postConfigure(
      ("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "45"),
      ("Whizbang:Postgres:billing-db:CommandTimeoutSeconds", "90"));

    var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
    await Assert.That(warnings.Count).IsEqualTo(1);
    await Assert.That(warnings[0].Message).Contains("orders-db")
      .Because("the operator has to know which database's section to edit.");
    await Assert.That(warnings[0].Message).DoesNotContain("billing-db")
      .Because("the other database's instance warns when it is configured, not from this one.");
  }

  [Test]
  public async Task TheRetiredKey_WithNoLoggerStillBindsAsync() {
    // The post-configure step runs before logging is guaranteed to be resolvable; a missing logger must
    // not turn a deprecation notice into a startup failure.
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection([
        new KeyValuePair<string, string?>("Whizbang:Postgres:orders-db:CommandTimeoutSeconds", "45"),
      ])
      .Build();
    var options = new PostgresOptions();

    new PostgresOptionsPostConfigure(configuration, new PostgresDefaultDatabase("orders-db"), logger: null)
      .PostConfigure(name: null, options);

#pragma warning disable CS0618 // asserting the retired key still binds
    await Assert.That(options.CommandTimeoutSeconds).IsEqualTo(45);
#pragma warning restore CS0618
  }
}
