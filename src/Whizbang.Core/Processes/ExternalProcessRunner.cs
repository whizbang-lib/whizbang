// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Diagnostics;

namespace Whizbang.Core.Processes;

/// <summary>
/// Starts a program outside the process and waits for it, the one way the library starts a child process (today the
/// pg_repack client). A seam so a host can run the program elsewhere, and so tests need no program installed.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/Processes/ExternalProcessRunnerTests.cs</tests>
public interface IExternalProcessRunner {
  /// <summary>Starts the program, waits for it to exit, and returns its exit code and output.</summary>
  /// <param name="request">The program, its arguments and the environment it runs with.</param>
  /// <param name="cancellationToken">Cancels the wait and stops the program.</param>
  /// <returns>
  /// The result. A program that could not be started (not found, not executable) is a result with
  /// <see cref="ExternalProcessResult.Started"/> false and the reason, not an exception.
  /// </returns>
  Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken);
}

/// <summary>A program to run: its path or name, its arguments, and the environment variables set for it alone.</summary>
/// <remarks>
/// Each argument reaches the program as one argument, with no shell to split or expand it. The environment is where
/// a credential travels: process arguments can be listed by every user on the host, and a child's environment only by
/// its own user and an administrator. So <see cref="ToString"/> names the program and its arguments and never the
/// environment.
/// </remarks>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Processes/ExternalProcessRequestTests.cs</tests>
public sealed class ExternalProcessRequest {
  /// <summary>Creates the request.</summary>
  /// <param name="fileName">The program: a path, or a name looked up on <c>PATH</c>.</param>
  /// <param name="arguments">The arguments, each passed as one.</param>
  /// <param name="environment">Variables set for the program on top of this process's own; none when null.</param>
  public ExternalProcessRequest(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(arguments);
    FileName = fileName;
    Arguments = arguments;
    Environment = environment ?? new Dictionary<string, string>();
  }

  /// <summary>The program: a path, or a name looked up on <c>PATH</c>.</summary>
  public string FileName { get; }

  /// <summary>The arguments, each passed as one.</summary>
  public IReadOnlyList<string> Arguments { get; }

  /// <summary>Variables set for the program on top of this process's own.</summary>
  public IReadOnlyDictionary<string, string> Environment { get; }

  /// <summary>The program and its arguments, as a log may show them; never the environment.</summary>
  /// <returns>The command line.</returns>
  public override string ToString() => string.Join(' ', [FileName, .. Arguments]);
}

/// <summary>How a program run ended.</summary>
/// <param name="Started">Whether the program started at all.</param>
/// <param name="ExitCode">Its exit code; -1 when it did not start.</param>
/// <param name="StandardOutput">What it wrote to standard output.</param>
/// <param name="StandardError">What it wrote to standard error, or why it did not start.</param>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Tests/Processes/ExternalProcessRequestTests.cs</tests>
public sealed record ExternalProcessResult(bool Started, int ExitCode, string StandardOutput, string StandardError) {
  /// <summary>A program that could not be started, with the reason.</summary>
  /// <param name="reason">Why it did not start.</param>
  /// <returns>The result.</returns>
  public static ExternalProcessResult NotStarted(string reason) => new(Started: false, ExitCode: -1, StandardOutput: "", StandardError: reason);
}

/// <summary>
/// The <see cref="IExternalProcessRunner"/> that starts a real child process: no shell, the request's environment set
/// on the child alone, both output streams read to the end while it runs, and the child and its descendants stopped
/// when the wait is canceled.
/// </summary>
/// <docs>operations/infrastructure/storage-maintenance#pg-repack</docs>
/// <tests>tests/Whizbang.Core.Integration.Tests/Processes/ExternalProcessRunnerTests.cs</tests>
/// <tests>tests/Whizbang.Core.Tests/Processes/ExternalProcessRequestTests.cs</tests>
public sealed class ExternalProcessRunner : IExternalProcessRunner {
  /// <inheritdoc />
  public async Task<ExternalProcessResult> RunAsync(ExternalProcessRequest request, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(request);
    using var process = new Process { StartInfo = CreateStartInfo(request) };
    if (_start(process) is { } reason) {
      return ExternalProcessResult.NotStarted(reason);
    }

    // Both streams are drained while the program runs: one left unread fills its pipe and blocks the program.
    var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
    var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
    // Cancellation stops the program and its descendants; the wait then ends because the program has exited.
    using (cancellationToken.Register(() => process.Kill(entireProcessTree: true))) {
      await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
    var standardOutput = await output.ConfigureAwait(false);
    var standardError = await error.ConfigureAwait(false);
    cancellationToken.ThrowIfCancellationRequested();
    return new ExternalProcessResult(Started: true, process.ExitCode, standardOutput, standardError);
  }

  /// <summary>Starts the process; null when it started, otherwise why not (not found, not executable).</summary>
  private static string? _start(Process process) {
    try {
      process.Start();
      return null;
    } catch (Win32Exception ex) {
      return ex.Message;
    }
  }

  /// <summary>The start information for a request: no shell, arguments one by one, environment on the child, output captured.</summary>
  /// <param name="request">The request.</param>
  /// <returns>The start information.</returns>
  internal static ProcessStartInfo CreateStartInfo(ExternalProcessRequest request) {
    var info = new ProcessStartInfo(request.FileName) {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
    };
    foreach (var argument in request.Arguments) {
      info.ArgumentList.Add(argument);
    }
    foreach (var (name, value) in request.Environment) {
      info.Environment[name] = value;
    }
    return info;
  }
}
