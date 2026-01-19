using System.Runtime.CompilerServices;

// Testing Support - InternalsVisibleTo causes PolySharp polyfill conflicts with .NET 10 test project (CS0433)
// See: https://github.com/Sergio0694/PolySharp/issues/103
// Solution: Make tested types public instead of using InternalsVisibleTo
// [assembly: InternalsVisibleTo("Whizbang.Generators.Tests")]
