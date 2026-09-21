namespace Whizbang.Core;

/// <summary>
/// Marks the framework's placeholder implementation of an extensibility point: the registration a
/// host receives when no subsystem has supplied a real one. A null default reports itself as not
/// configured (or not available) through the interface's capability flag, so the code that depends
/// on it takes the same skip path a missing registration used to, without a null check.
/// </summary>
/// <remarks>
/// The marker exists so an opt-in subsystem can displace the placeholder without displacing a
/// host's own registration: <see cref="NullDefaultServiceCollectionExtensions.TryAddSingletonOverNullDefault{TService}(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Func{System.IServiceProvider, TService})"/>
/// removes registrations whose implementation carries this marker and then adds with TryAdd, so
/// the outcome no longer depends on whether the subsystem was registered before or after the core.
/// </remarks>
/// <docs>extending/extensibility/replaceable-services</docs>
public interface INullDefault {
}
