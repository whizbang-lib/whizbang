using Microsoft.CodeAnalysis;

namespace Whizbang.Generators;

/// <summary>
/// Value type containing information about a discovered GUID creation call to intercept.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="FilePath">The source file path containing the call</param>
/// <param name="LineNumber">The 1-based line number of the call</param>
/// <param name="ColumnNumber">The 1-based column number of the call</param>
/// <param name="OriginalMethod">The original method being called (e.g., "NewGuid", "CreateVersion7")</param>
/// <param name="FullyQualifiedTypeName">The fully qualified type name (e.g., "global::System.Guid")</param>
/// <param name="GuidVersion">The GUID version metadata flag name (e.g., "Version4", "Version7")</param>
/// <param name="GuidSource">The GUID source metadata flag name (e.g., "SourceMicrosoft", "SourceMarten")</param>
/// <param name="InterceptorMethodName">A unique name for the generated interceptor method</param>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptionInfoTests.cs:GuidInterceptionInfo_ValueEquality_ComparesFieldsAsync</tests>
/// <tests>tests/Whizbang.Generators.Tests/GuidInterceptionInfoTests.cs:GuidInterceptionInfo_Constructor_SetsPropertiesAsync</tests>
public sealed record GuidInterceptionInfo(
    string FilePath,
    int LineNumber,
    int ColumnNumber,
    string OriginalMethod,
    string FullyQualifiedTypeName,
    string GuidVersion,
    string GuidSource,
    string InterceptorMethodName
);

/// <summary>
/// Value type containing information about a suppressed GUID interception.
/// Includes location for proper diagnostic reporting.
/// This record uses value equality which is critical for incremental generator performance.
/// </summary>
/// <param name="FilePath">The source file path containing the call</param>
/// <param name="LineNumber">The 1-based line number of the call</param>
/// <param name="OriginalMethod">The original method being called</param>
/// <param name="SuppressionSource">How interception was suppressed (e.g., "SuppressGuidInterceptionAttribute")</param>
/// <param name="Location">Source location for diagnostic reporting</param>
public sealed record SuppressedGuidInterceptionInfo(
    string FilePath,
    int LineNumber,
    string OriginalMethod,
    string SuppressionSource,
    Location Location
);
