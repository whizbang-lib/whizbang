namespace Whizbang.Core.Perspectives;

/// <summary>
/// Suppresses WHIZ070 and WHIZ071 diagnostics for this assembly.
/// Use when you have a custom pgvector setup that doesn't require the standard packages.
/// </summary>
/// <remarks>
/// <para>
/// This attribute is applied at the assembly level to suppress the package reference
/// analyzer from reporting errors when Pgvector or Pgvector.EntityFrameworkCore packages
/// are not referenced but [VectorField] is used.
/// </para>
/// <para>
/// Only use this attribute if you have a custom vector implementation or are testing
/// without the actual pgvector packages installed.
/// </para>
/// </remarks>
/// <docs>diagnostics/WHIZ070#suppression</docs>
/// <tests>VectorFieldPackageReferenceAnalyzerTests.cs:VectorField_WithSuppressAttribute_NoDiagnosticAsync</tests>
/// <example>
/// <code>
/// [assembly: SuppressVectorPackageCheck]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class SuppressVectorPackageCheckAttribute : Attribute { }
