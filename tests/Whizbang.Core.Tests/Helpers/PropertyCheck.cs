// OpenSSF Scorecard's Fuzzing check detects FsCheck by a literal match on this using, and FsCheck.Fluent
// alone does not match. Config, Replay and Check below come from this namespace, which is what keeps
// dotnet format from removing it as unused.
using FsCheck;
using Microsoft.FSharp.Core;

namespace Whizbang.Core.Tests.Helpers;

/// <summary>
/// Runs an FsCheck property from inside an ordinary TUnit test. FsCheck is used only as an input generator;
/// TUnit stays the framework. A failing property throws, and the exception message carries
/// the property name, the counterexample and the seed FsCheck used.
/// </summary>
/// <remarks>
/// Every property runs from a fixed seed, so a CI run and a local run explore exactly the same inputs and a
/// failure reproduces on the first try. To explore further while developing, change the seed locally; the
/// failure message prints the seed that found a counterexample.
/// </remarks>
internal static class PropertyCheck {
  /// <summary>Checks <paramref name="property"/> against <paramref name="maxTest"/> generated inputs, throwing on the first counterexample.</summary>
  /// <param name="name">Printed in the failure message, so a failure names the property that broke.</param>
  /// <param name="property">The property, typically built with <c>Prop.ForAll</c>.</param>
  /// <param name="maxTest">How many generated inputs to try.</param>
  /// <param name="seed">The fixed seed the inputs are generated from.</param>
  internal static void Run(string name, Property property, int maxTest, ulong seed) {
    var config = Config.QuickThrowOnFailure
      .WithMaxTest(maxTest)
      .WithReplay(FSharpOption<Replay>.Some(new Replay(new Rnd(seed), FSharpOption<int>.None)));
    Check.One(name, config, property);
  }
}
