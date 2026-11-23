// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;

namespace Whizbang.RepoAnalyzers.Tests;

/// <summary>
/// WHIZ610 (#2548): a type's name, read through the shared naming code, is formatted for a log call at
/// <c>Information</c> or below, or for an activity's name, only once the level (<c>IsEnabled</c>) or a listener
/// (<c>HasListeners</c>) says it is consumed. The runtime caches a type's name behind a weak handle, so an eager read
/// allocates after every garbage collection on a path that should allocate nothing.
/// </summary>
public class EagerLogTypeNameAnalyzerTests {
  // Stand-ins for the logging and tracing types the rule recognizes, by their real metadata names
  private const string PRELUDE = """
    using System;
    namespace Microsoft.Extensions.Logging {
      public enum LogLevel { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5, None = 6 }
      public interface ILogger {
        bool IsEnabled(LogLevel level);
        void Log(LogLevel level, string message);
      }
      [AttributeUsage(AttributeTargets.Method)]
      public sealed class LoggerMessageAttribute : Attribute {
        public LoggerMessageAttribute() { }
        public LoggerMessageAttribute(int eventId, LogLevel level, string message) { }
        public LogLevel Level { get; set; }
        public string Message { get; set; } = "";
      }
      public static class LoggerExtensions {
        public static void LogDebug(this ILogger logger, string message, params object?[] args) { }
        public static void LogInformation(this ILogger logger, string message, params object?[] args) { }
        public static void LogWarning(this ILogger logger, string message, params object?[] args) { }
        public static void Log(this ILogger logger, LogLevel level, string message, params object?[] args) { }
      }
    }
    namespace System.Diagnostics {
      public sealed class Activity { }
      public sealed class ActivitySource {
        public bool HasListeners() => false;
        public Activity? StartActivity(string name) => null;
      }
    }
    namespace App {
      using Microsoft.Extensions.Logging;
      [AttributeUsage(AttributeTargets.Class)]
      sealed class TypeNameAuthorityAttribute : Attribute { }
      [TypeNameAuthority]
      static class TypeNameFormatter {
        public static string ForLog(Type type) => "";
        public static string GetSimpleName(string name) => name;
      }
      static partial class Log {
        [LoggerMessage(Level = LogLevel.Debug, Message = "{Type}")]
        public static void Debug(ILogger logger, string type) { }
        [LoggerMessage(1, LogLevel.Information, "{Type}")]
        public static void Info(ILogger logger, string type) { }
        [LoggerMessage(Level = LogLevel.Warning, Message = "{Type}")]
        public static void Warn(ILogger logger, string type) { }
        [LoggerMessage(Message = "{Type}")]
        public static void AtLevel(ILogger logger, LogLevel level, string type) { }
        [LoggerMessage(Level = LogLevel.Debug, Message = "{Type}")]
        public static bool Logged(ILogger logger, string type) => true;
      }
    }

    """;

  [Test]
  public async Task Descriptor_IsARepositoryRuleReportedAsAnErrorAsync() {
    var descriptor = new EagerLogTypeNameAnalyzer().SupportedDiagnostics.Single();

    await Assert.That(descriptor.Id).IsEqualTo("WHIZ610");
    await Assert.That(descriptor.DefaultSeverity).IsEqualTo(DiagnosticSeverity.Error);
    await Assert.That(descriptor.IsEnabledByDefault).IsTrue();
  }

  // ===== reported =====

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("Log.Info(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("Log.AtLevel(logger, LogLevel.Debug, TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogDebug(\"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogInformation(\"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.Log(LogLevel.Debug, \"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.Log(LogLevel.Trace, TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogDebug($\"seen {TypeNameFormatter.ForLog(t)}\");")]
  [Arguments("logger.LogDebug(\"{T}\", TypeNameFormatter.ForLog(t) + \"!\");")]
  [Arguments("logger.LogDebug(\"{T}\", ok ? TypeNameFormatter.ForLog(t) : \"\");")]
  [Arguments("if (ok) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger.IsEnabled(LogLevel.Debug)) { } else { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger.IsEnabled(LogLevel.Debug) || ok) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { ok = false; } Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("var name = TypeNameFormatter.ForLog(t); if (logger.IsEnabled(LogLevel.Debug)) { Log.Debug(logger, name); }")]
  [Arguments("source.StartActivity($\"Dispatch {TypeNameFormatter.ForLog(t)}\");")]
  [Arguments("var debug = ok; if (debug) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("_ = ok ? Log.Logged(logger, TypeNameFormatter.ForLog(t)) : false;")]
  [Arguments("_ = logger.IsEnabled(LogLevel.Debug) ? false : Log.Logged(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("_ = ok && Log.Logged(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("_ = Log.Logged(logger, TypeNameFormatter.ForLog(t)) && logger.IsEnabled(LogLevel.Debug);")]
  [Arguments("if (logger?.IsEnabled(LogLevel.Debug) == false) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (ok == true) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger?.Equals(null) == true) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger?.GetHashCode() is 1) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("var lvl = LogLevel.Debug; logger.Log(lvl, \"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogDebug(\"{T}\", maybe ?? TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogDebug(\"{T}\", Echo(TypeNameFormatter.ForLog(t)));")]
  [Arguments("logger.LogDebug(\"{T}\", (TypeNameFormatter.ForLog(t)));")]
  [Arguments("if (Log.Logged(logger, TypeNameFormatter.ForLog(t))) { ok = false; }")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { } Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { return; } else { ok = true; } Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (ok) return; Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (!ok) return; Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("bool debug; debug = logger.IsEnabled(LogLevel.Debug); if (debug) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (Check<int>()) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (t?.IsAbstract == true) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  public async Task UnguardedLogArgument_IsReportedAsync(string statement) {
    var diagnostics = await _analyzeAsync(statement);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ610")).IsEqualTo(1)
      .Because($"'{statement}' reads a type name before anything says it is consumed");
  }

  // ===== not reported =====

  [Test]
  [RequiresAssemblyFiles()]
  [Arguments("if (logger.IsEnabled(LogLevel.Debug)) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger.IsEnabled(LogLevel.Debug)) Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (ok && logger.IsEnabled(LogLevel.Debug)) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if ((logger.IsEnabled(LogLevel.Debug))) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { return; } Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) return; Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (!logger.IsEnabled(LogLevel.Debug)) { throw new InvalidOperationException(); } Log.Debug(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("var debug = logger.IsEnabled(LogLevel.Debug); if (debug) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("_ = logger.IsEnabled(LogLevel.Debug) ? Log.Logged(logger, TypeNameFormatter.ForLog(t)) : false;")]
  [Arguments("_ = logger.IsEnabled(LogLevel.Debug) && Log.Logged(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("if (logger.IsEnabled(LogLevel.Debug)) { var name = TypeNameFormatter.ForLog(t); Log.Debug(logger, name); }")]
  [Arguments("Log.Warn(logger, TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.LogWarning(\"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("logger.Log(LogLevel.Error, \"{T}\", TypeNameFormatter.ForLog(t));")]
  [Arguments("var name = TypeNameFormatter.ForLog(t); Log.Debug(logger, name); Use(name);")]
  [Arguments("var name = TypeNameFormatter.ForLog(t); Use(name);")]
  [Arguments("Use(TypeNameFormatter.ForLog(t));")]
  [Arguments("throw new InvalidOperationException(TypeNameFormatter.ForLog(t));")]
  [Arguments("Log.Debug(logger, TypeNameFormatter.GetSimpleName(\"A.B\"));")]
  [Arguments("if (source.HasListeners()) { source.StartActivity($\"Dispatch {TypeNameFormatter.ForLog(t)}\"); }")]
  [Arguments("_ = source.HasListeners() ? source.StartActivity(TypeNameFormatter.ForLog(t)) : null;")]
  [Arguments("switch (t) { case null: break; default: if (!logger.IsEnabled(LogLevel.Debug)) { break; } Log.Debug(logger, TypeNameFormatter.ForLog(t)); break; }")]
  [Arguments("foreach (var _ in new[] { 1 }) { if (!logger.IsEnabled(LogLevel.Debug)) continue; Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (logger?.IsEnabled(LogLevel.Debug) == true) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("if (true == logger?.IsEnabled(LogLevel.Debug)) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("try { Use(\"\"); } catch (InvalidOperationException) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("try { Use(\"\"); } catch (InvalidOperationException) { var name = TypeNameFormatter.ForLog(t); Log.Debug(logger, name); }")]
  [Arguments("if (IsEnabled(LogLevel.Debug)) { Log.Debug(logger, TypeNameFormatter.ForLog(t)); }")]
  [Arguments("var name = TypeNameFormatter.ForLog(t);")]
  public async Task GuardedOrNotALowLevelLogArgument_IsNotReportedAsync(string statement) {
    var diagnostics = await _analyzeAsync(statement);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ610")).IsEmpty()
      .Because($"'{statement}' reads the name only when it is consumed, or not for a low-level log or an activity name");
  }

  /// <summary>A check outside the lambda that makes the call does not guard it: the lambda may run anywhere.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACheckOutsideALambda_DoesNotGuardTheCallInsideItAsync() {
    var diagnostics = await _analyzeAsync(
      "if (logger.IsEnabled(LogLevel.Debug)) { Action a = () => Log.Debug(logger, TypeNameFormatter.ForLog(t)); }");

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ610")).IsEqualTo(1);
  }

  /// <summary>Inside a local function, the check of the enclosing method does not reach.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACheckOutsideALocalFunction_DoesNotGuardTheCallInsideItAsync() {
    var diagnostics = await _analyzeAsync(
      "if (logger.IsEnabled(LogLevel.Debug)) { void L() => Log.Debug(logger, TypeNameFormatter.ForLog(t)); L(); }");

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ610")).IsEqualTo(1);
  }

  /// <summary>Code inside the shared naming code itself, and a member body that is not a method, are analyzed too.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task AnExpressionBodiedMember_IsAnalyzedAsync() {
    var diagnostics = await _analyzeSourceAsync(PRELUDE + """
      namespace App {
        using Microsoft.Extensions.Logging;
        class C {
          ILogger logger = null!;
          void M(Type t) => Log.Debug(logger, TypeNameFormatter.ForLog(t));
        }
      }
      """);

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ610")).IsEqualTo(1);
  }

  /// <summary>A compilation without <see cref="System.Type"/>'s naming code or logging types reports nothing.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task ACompilationWithoutLoggingTypes_ReportsNothingAsync() {
    var diagnostics = await _analyzeSourceAsync("""
      using System;
      [AttributeUsage(AttributeTargets.Class)]
      sealed class TypeNameAuthorityAttribute : Attribute { }
      [TypeNameAuthority]
      static class TypeNameFormatter { public static string ForLog(Type type) => ""; }
      static class Logger { public static void Log(string s) { } }
      class C { void M(Type t) => Logger.Log(TypeNameFormatter.ForLog(t)); }
      """);

    await Assert.That(diagnostics.Where(d => d.Id == "WHIZ610")).IsEmpty();
  }

  /// <summary>An <c>ILogger.Log</c> call on the interface itself is recognized like the extension.</summary>
  [Test]
  [RequiresAssemblyFiles()]
  public async Task TheInterfaceLogMethod_IsRecognizedAsync() {
    var diagnostics = await _analyzeAsync("((ILogger)logger).Log(LogLevel.Debug, TypeNameFormatter.ForLog(t));");

    await Assert.That(diagnostics.Count(d => d.Id == "WHIZ610")).IsEqualTo(1);
  }

  private static Task<IReadOnlyList<Diagnostic>> _analyzeAsync(string statement) =>
    _analyzeSourceAsync(PRELUDE + $$"""
      namespace App {
        using Microsoft.Extensions.Logging;
        class C {
          static void Use(string s) { }
          static string Echo(string s) => s;
          static bool IsEnabled(LogLevel level) => true;
          static bool Check<T>() => true;
          string? maybe = null;
          void M(ILogger logger, System.Diagnostics.ActivitySource source, Type t, bool ok) {
            {{statement}}
          }
        }
      }
      """);

  [RequiresAssemblyFiles()]
  private static async Task<IReadOnlyList<Diagnostic>> _analyzeSourceAsync(string source) {
    var compilationErrors = RepoAnalyzerTestHelper.CreateCompilation(source)
      .GetDiagnostics()
      .Where(d => d.Severity == DiagnosticSeverity.Error)
      .Select(d => d.ToString())
      .ToList();
    if (compilationErrors.Count > 0) {
      throw new InvalidOperationException("The snippet does not compile: " + string.Join("; ", compilationErrors));
    }
    return await RepoAnalyzerTestHelper.GetDiagnosticsAsync<EagerLogTypeNameAnalyzer>(source);
  }
}
