// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.RunControl;

/// <summary>
/// The process's own failure events, which the one exit path subscribes to when the host starts (#2126): an exception
/// nothing caught on any thread, and a faulted task nobody observed.
/// </summary>
/// <remarks>
/// A seam over <see cref="AppDomain.UnhandledException"/> and <see cref="TaskScheduler.UnobservedTaskException"/>, so a
/// test raises a crash without crashing its process. A host registers nothing: the exit path uses
/// <see cref="ProcessCrashEvents.Instance"/>.
/// </remarks>
/// <docs>operations/startup/instance-exits#a-crash-on-any-thread</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitCrashTests.cs</tests>
public interface IProcessCrashEvents {
  /// <summary>An exception nothing caught, on any thread; the runtime ends the process once the handlers return.</summary>
  event EventHandler<UnhandledExceptionEventArgs> UnhandledException;

  /// <summary>A faulted task whose exception nobody observed, found when the task is collected.</summary>
  event EventHandler<UnobservedTaskExceptionEventArgs> UnobservedTaskException;
}

/// <summary>The process's real failure events: <see cref="AppDomain.CurrentDomain"/> and <see cref="TaskScheduler"/>.</summary>
/// <docs>operations/startup/instance-exits#a-crash-on-any-thread</docs>
/// <tests>tests/Whizbang.Core.Component.Tests/RunControl/WhizbangInstanceExitCrashTests.cs:TheProcessEvents_AreTheRuntimesOwnAsync</tests>
public sealed class ProcessCrashEvents : IProcessCrashEvents {
  /// <summary>The one instance.</summary>
  public static ProcessCrashEvents Instance { get; } = new();

  private ProcessCrashEvents() { }

  /// <inheritdoc />
  /// <remarks>
  /// The runtime's event takes its own delegate type, so each handler is passed as its <c>Invoke</c> method, which
  /// converts to that type. Two conversions of the same handler are equal (same target, same method), so removal
  /// finds the one that was added.
  /// </remarks>
  public event EventHandler<UnhandledExceptionEventArgs> UnhandledException {
    add => AppDomain.CurrentDomain.UnhandledException += value.Invoke;
    remove => AppDomain.CurrentDomain.UnhandledException -= value.Invoke;
  }

  /// <inheritdoc />
  public event EventHandler<UnobservedTaskExceptionEventArgs> UnobservedTaskException {
    add => TaskScheduler.UnobservedTaskException += value;
    remove => TaskScheduler.UnobservedTaskException -= value;
  }
}
