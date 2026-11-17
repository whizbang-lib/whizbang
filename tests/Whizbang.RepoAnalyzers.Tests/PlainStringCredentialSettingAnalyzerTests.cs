// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHIZ606: a settings property whose name says it holds a credential is a plain string, so writing the settings out
/// (a log line, the startup settings report, serialization) writes the credential. It must be a SensitiveString.
/// </summary>
public class PlainStringCredentialSettingAnalyzerTests {
  [Test]
  public async Task Descriptor_IsTheRepositoryRuleReportedAsAnErrorAsync() {
    var descriptor = new PlainStringCredentialSettingAnalyzer().SupportedDiagnostics.Single();

    await Assert.That(descriptor.Id).IsEqualTo("WHIZ606");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACredentialNamedStringSetting_ReportsAsync() {
    var diagnostics = await _analyzeAsync("""
      public sealed class StoreOptions {
        public string? ConnectionString { get; set; }
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
    await Assert.That(diagnostics[0].Id).IsEqualTo("WHIZ606");
    await Assert.That(diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture))
      .Contains("StoreOptions.ConnectionString");
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("DirectConnectionString")]
  [Arguments("Password")]
  [Arguments("ClientSecret")]
  [Arguments("AccountKey")]
  [Arguments("KeyEncryptionKey")]
  [Arguments("AccessToken")]
  [Arguments("Credential")]
  [Arguments("Credentials")]
  [Arguments("apikey")]
  public async Task EveryCredentialWord_ReportsAsync(string name) {
    var diagnostics = await _analyzeAsync($$"""
      public sealed class ClientSettings {
        public string {{name}} { get; init; } = "";
      }
      """);

    await Assert.That(diagnostics).Count().IsEqualTo(1);
  }

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("public sealed class StoreOptions { public string? ConnectionStringName { get; set; } }")]
  [Arguments("public sealed class StoreOptions { public string? KeyId { get; set; } }")]
  [Arguments("public sealed class StoreOptions { public int Token { get; set; } }")]
  [Arguments("public sealed class StoreOptions { public string? Password { get; } }")]
  [Arguments("public sealed class StoreOptions { public string? Password { get; private set; } }")]
  [Arguments("public sealed class StoreOptions { internal string? Password { get; set; } }")]
  [Arguments("public sealed class StoreOptions { public static string? Password { get; set; } }")]
  [Arguments("public sealed class Store { public string? Password { get; set; } }")]
  public async Task WhatIsNotACredentialSetting_DoesNotReportAsync(string source) {
    var diagnostics = await _analyzeAsync(source);

    await Assert.That(diagnostics).IsEmpty();
  }

  [RequiresAssemblyFiles()]
  private static async Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> _analyzeAsync(string source) {
    var compilation = RepoAnalyzerTestHelper.CreateCompilation(source);
    var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
    if (errors.Count > 0) {
      throw new InvalidOperationException("The snippet does not compile: " + string.Join("; ", errors));
    }
    return await RepoAnalyzerTestHelper.GetDiagnosticsAsync<PlainStringCredentialSettingAnalyzer>(source);
  }
}
