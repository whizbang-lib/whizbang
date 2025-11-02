using System.Reflection;
using System.Runtime.CompilerServices;

// AOT and Trimming Support
[assembly: AssemblyMetadata("IsTrimmable", "True")]
[assembly: AssemblyMetadata("EnableTrimAnalyzer", "True")]
[assembly: AssemblyMetadata("EnableSingleFileAnalyzer", "True")]

// Testing Support - Allow test assemblies to access internal members
[assembly: InternalsVisibleTo("Whizbang.Core.Tests")]
[assembly: InternalsVisibleTo("Whizbang.Documentation.Tests")]

// Allow generated code to access internals
[assembly: InternalsVisibleTo("Whizbang.Core.Generated")]

// Mocking framework support (Rocks uses source generation, but just in case)
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]
