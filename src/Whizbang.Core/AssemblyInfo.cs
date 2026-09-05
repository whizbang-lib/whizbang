using System.Reflection;

// AOT and Trimming Support
[assembly: AssemblyMetadata("IsTrimmable", "True")]
[assembly: AssemblyMetadata("EnableTrimAnalyzer", "True")]
[assembly: AssemblyMetadata("EnableSingleFileAnalyzer", "True")]

// InternalsVisibleTo lives in Whizbang.Core.csproj (<InternalsVisibleTo> items) so the
// strong-name public key is attached centrally by Directory.Build.props.
