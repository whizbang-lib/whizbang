// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

using System;
using System.Runtime.CompilerServices;

namespace Whizbang.Tests.Shared;

/// <summary>
/// Turns off the TUnit reporters that observe the test process, before the test platform starts. Compiled into every
/// test executable under tests/ (tests/Directory.Build.props).
/// </summary>
/// <remarks>
/// TUnit's HTML report, on by default since well after 1.12, registers an <see cref="System.Diagnostics.ActivityListener"/>
/// that samples every <see cref="System.Diagnostics.ActivitySource"/> in the process and runs each test inside an
/// Activity of its own. The library's tracing is a behavior under test here, including what it does when nothing
/// listens, so a listener the runner adds changes what the tests observe. The GitHub reporter writes to a CI run's step
/// summary from every test process. A variable already set (for example to <c>false</c>) is left as it is.
/// </remarks>
internal static class TestRunnerDefaults {
#pragma warning disable CA2255 // A module initializer is the only code that runs before the test platform reads its settings.
  [ModuleInitializer]
#pragma warning restore CA2255
  internal static void TurnOffObservingReporters() {
    foreach (var name in new[] { "TUNIT_DISABLE_HTML_REPORTER", "TUNIT_DISABLE_GITHUB_REPORTER" }) {
      if (Environment.GetEnvironmentVariable(name) is null) {
        Environment.SetEnvironmentVariable(name, "true");
      }
    }
  }
}
