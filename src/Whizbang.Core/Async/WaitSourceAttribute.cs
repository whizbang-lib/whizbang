// Copyright (c) whizbang-lib contributors.
// SPDX-License-Identifier: MIT

namespace Whizbang.Core.Async;

/// <summary>
/// Marks a method whose task is a wait: it completes when something happens elsewhere (a gate opens, an event is
/// applied), not when work it runs finishes. A wait raced in <c>Task.WhenAny</c> and lost stays registered wherever it
/// waits, so the repository analyzer WHIZ612 reports a <c>Task.WhenAny</c> over one; wait on a <see cref="WakeSet"/>
/// instead.
/// </summary>
/// <remarks>
/// The analyzer already knows the framework's own waits (<c>Task.Delay</c>, channel waits, <c>SemaphoreSlim.WaitAsync</c>,
/// <c>TaskCompletionSource.Task</c>, <see cref="WakeSignal.WaitAsync"/>); this attribute names the rest, such as the
/// readiness gates. A task the analyzer cannot place is taken as compute and not reported.
/// </remarks>
/// <docs>contributors/loop-waits#the-analyzer</docs>
/// <tests>tests/Whizbang.RepoAnalyzers.Tests/HandWrittenWaitRaceAnalyzerTests.cs</tests>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class WaitSourceAttribute : Attribute;
