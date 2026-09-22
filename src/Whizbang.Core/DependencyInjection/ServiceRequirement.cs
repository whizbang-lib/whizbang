namespace Whizbang.Core.DependencyInjection;

/// <summary>
/// One registered implementation type and the service types its constructor requires.
/// </summary>
/// <remarks>
/// <para>
/// This is the compile-time answer to "what does this type need", emitted as data so that checking
/// it at run time needs no reflection. Discovering constructor parameters at run time would mean
/// <c>Type.GetConstructors</c>, which this framework does not permit; the generator already holds
/// the whole compilation and can simply write the answer down.
/// </para>
/// <para>
/// Requirements are derived, never declared. A contributor who adds a constructor parameter appears
/// here on the next build without annotating anything, which is the only property that makes the
/// guard survive contributors who have never heard of it.
/// </para>
/// </remarks>
/// <param name="ImplementationType">The type whose constructor declares the dependencies.</param>
/// <param name="Dependencies">Service types the constructor requires.</param>
/// <docs>operations/dependency-injection/registration-validation</docs>
/// <tests>tests/Whizbang.Core.Tests/DependencyInjection/RegistrationValidationTests.cs</tests>
public sealed record ServiceRequirement(Type ImplementationType, IReadOnlyList<Type> Dependencies);

/// <summary>
/// A service a registered type requires that no registration provides.
/// </summary>
/// <param name="NeededBy">The type whose constructor declares the dependency.</param>
/// <param name="MissingService">The service type nothing registers.</param>
public readonly record struct MissingRegistration(Type NeededBy, Type MissingService);
