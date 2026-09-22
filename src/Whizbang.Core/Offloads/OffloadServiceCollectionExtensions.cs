using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Whizbang.Core.Offloads;

/// <summary>
/// DI registration surface for body-store providers. Each
/// <see cref="IMessageBodyStore"/> implementation registers under a stable
/// provider name; the receive-side resolver looks up the matching store by
/// <see cref="MessageBodyClaim.ProviderName"/> when a claim arrives on the
/// wire.
/// </summary>
/// <docs>fundamentals/offloads/message-body-store</docs>
public static class OffloadServiceCollectionExtensions {

  /// <summary>
  /// Registers a body-store implementation as a keyed singleton under
  /// <paramref name="providerName"/>. Multiple providers can be registered
  /// with distinct names (e.g., <c>"azure-blob-prod"</c>,
  /// <c>"azure-blob-archive"</c>); the offload config picks one by name.
  /// </summary>
  /// <typeparam name="TStore">Concrete <see cref="IMessageBodyStore"/> implementation type. Must support construction from the DI container; if the impl wants the provider name injected, it accepts a <c>[ServiceKey] string</c> parameter.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="providerName">Stable identifier that the sender's claim and the receiver's resolver both reference. MUST be unique across registrations.</param>
  /// <returns>The service collection for chaining.</returns>
  /// <remarks>
  /// <para>
  /// Singleton lifetime: the same instance is reused across resolutions so
  /// providers that maintain expensive state (HTTP clients, connection
  /// pools, blob-service clients) construct once.
  /// </para>
  /// <para>
  /// Provider-project ergonomics: each provider project typically wraps
  /// this with a typed extension that takes its options
  /// (e.g., <c>AddWhizbangInMemoryOffload(name)</c>,
  /// <c>AddWhizbangAzureBlobOffload(name, opts =&gt; …)</c>) so consumers
  /// don't construct providers by hand.
  /// </para>
  /// </remarks>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_RegistersByProviderName_ResolvesByNameAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_TwoProviders_CoexistAndResolveByKeyAsync</tests>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/AddWhizbangMessageBodyStoreTests.cs:AddWhizbangMessageBodyStore_SingletonLifetime_ReturnsSameInstanceAsync</tests>
  public static IServiceCollection AddWhizbangMessageBodyStore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
      this IServiceCollection services,
      string providerName) where TStore : class, IMessageBodyStore {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

    services.AddKeyedSingleton<IMessageBodyStore, TStore>(providerName);
    return services;
  }

  /// <summary>
  /// Registers a body cipher under <paramref name="cipherName"/> (issue #704). Name it in
  /// <see cref="MessageBodyOffloadOptions.CipherName"/> on the sender; register the same name on
  /// every receiver that rehydrates its claims.
  /// </summary>
  /// <typeparam name="TCipher">The cipher implementation; constructed by the container.</typeparam>
  /// <param name="services">The service collection.</param>
  /// <param name="cipherName">The name sender and receiver agree on.</param>
  /// <docs>fundamentals/offloads/message-body-store</docs>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadCipherTests.cs</tests>
  public static IServiceCollection AddWhizbangMessageBodyCipher<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TCipher>(
      this IServiceCollection services,
      string cipherName) where TCipher : class, IMessageBodyCipher {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(cipherName);

    services.AddKeyedSingleton<IMessageBodyCipher, TCipher>(cipherName);
    return services;
  }

  /// <summary>
  /// Registers the built-in AES-256-GCM envelope cipher under <paramref name="cipherName"/> with a
  /// key wrapper resolved from the container (a vault-backed wrapper keeps the key encryption key
  /// out of the process; <see cref="LocalAesKeyWrapper"/> holds one the host supplies).
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="cipherName">The name sender and receiver agree on.</param>
  /// <param name="keyWrapper">Builds the key wrapper from the container.</param>
  /// <docs>fundamentals/offloads/message-body-store</docs>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadCipherTests.cs</tests>
  public static IServiceCollection AddWhizbangAesGcmBodyCipher(
      this IServiceCollection services,
      string cipherName,
      Func<IServiceProvider, IMessageBodyKeyWrapper> keyWrapper) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentException.ThrowIfNullOrWhiteSpace(cipherName);
    ArgumentNullException.ThrowIfNull(keyWrapper);

    services.AddKeyedSingleton<IMessageBodyCipher>(cipherName, (sp, key) => new AesGcmEnvelopeCipher((string)key!, keyWrapper(sp)));
    return services;
  }

  /// <summary>
  /// Registers the built-in AES-256-GCM envelope cipher over a key encryption key the host
  /// supplies (32 bytes, from its secret store). Data keys are minted per body and travel wrapped.
  /// </summary>
  /// <param name="services">The service collection.</param>
  /// <param name="cipherName">The name sender and receiver agree on.</param>
  /// <param name="keyId">The identifier recorded on claims for this key encryption key.</param>
  /// <param name="keyEncryptionKey">The 32-byte key encryption key.</param>
  /// <docs>fundamentals/offloads/message-body-store</docs>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadCipherTests.cs</tests>
  public static IServiceCollection AddWhizbangAesGcmBodyCipher(
      this IServiceCollection services,
      string cipherName,
      string keyId,
      ReadOnlyMemory<byte> keyEncryptionKey) {
    var wrapper = new LocalAesKeyWrapper(keyId, keyEncryptionKey);
    return services.AddWhizbangAesGcmBodyCipher(cipherName, _ => wrapper);
  }

  // The configuration section the settings-driven cipher reads; the same section the Azure Blob
  // helper binds MessageBodyOffloadOptions from.
  private const string BODY_OFFLOAD_SECTION = "Whizbang:BodyOffload";

  private const string CIPHER_NAME_KEY = "CipherName";
  private const string CIPHER_SECTION = "Cipher";
  private const string KEY_ID_KEY = "KeyId";
  private const string KEY_KEY = "KeyEncryptionKey";
  private const string PREVIOUS_KEY_ID_KEY = "PreviousKeyId";
  private const string PREVIOUS_KEY_KEY = "PreviousKeyEncryptionKey";
  private const int KEY_ENCRYPTION_KEY_BYTES = 32;

  /// <summary>
  /// Registers the built-in AES-256-GCM body cipher from settings alone and names it on
  /// <see cref="MessageBodyOffloadOptions"/>, so every offloaded body is sealed without consumer code.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Reads <c>Whizbang:BodyOffload:CipherName</c> and the <c>Whizbang:BodyOffload:Cipher</c>
  /// subsection: <c>KeyId</c> (the rotation label recorded on every claim), <c>KeyEncryptionKey</c>
  /// (32 bytes, base64), and during a rotation window <c>PreviousKeyId</c> with
  /// <c>PreviousKeyEncryptionKey</c>, which open bodies sealed before the rotation through
  /// <see cref="RotatingAesKeyWrapper"/>. Without a cipher name nothing is registered and bodies are
  /// stored as serialized, exactly as before.
  /// </para>
  /// <para>
  /// Misconfiguration throws at startup and names the setting. A cipher that silently did not engage
  /// would store plaintext under a sealed label, which is the failure this exists to prevent.
  /// </para>
  /// </remarks>
  /// <docs>fundamentals/offloads/message-body-store#cipher-from-settings</docs>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyCipherFromConfigurationTests.cs</tests>
  /// <tests>tests/Whizbang.Offloads.AzureBlob.Tests/AzureBlobOffloadFromConfigurationTests.cs</tests>
  /// <exception cref="InvalidOperationException">A cipher is named but its key settings are absent or invalid.</exception>
  public static IServiceCollection AddWhizbangBodyCipherFromConfiguration(
      this IServiceCollection services,
      IConfiguration configuration) {
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    var bodyOffload = configuration.GetSection(BODY_OFFLOAD_SECTION);
    var cipherName = bodyOffload[CIPHER_NAME_KEY];
    if (string.IsNullOrWhiteSpace(cipherName)) {
      return services;
    }

    var cipher = bodyOffload.GetSection(CIPHER_SECTION);
    var keyId = _requiredSetting(cipher, KEY_ID_KEY);
    var key = _keyMaterial(cipher, KEY_KEY, required: true)!.Value;
    var previousKeyId = cipher[PREVIOUS_KEY_ID_KEY];
    var previousKey = _keyMaterial(cipher, PREVIOUS_KEY_KEY, required: false);
    if (string.IsNullOrWhiteSpace(previousKeyId) != (previousKey is null)) {
      var missing = previousKey is null ? PREVIOUS_KEY_KEY : PREVIOUS_KEY_ID_KEY;
      throw new InvalidOperationException(
        $"{BODY_OFFLOAD_SECTION}:{CIPHER_SECTION}:{missing} is not configured. A rotation window needs both the previous key id and the previous key encryption key, or neither.");
    }

    var wrapper = new RotatingAesKeyWrapper(keyId, key, string.IsNullOrWhiteSpace(previousKeyId) ? null : previousKeyId, previousKey);
    services.AddWhizbangAesGcmBodyCipher(cipherName, _ => wrapper);
    services.Configure<MessageBodyOffloadOptions>(opts => opts.CipherName = cipherName);
    return services;
  }

  private static string _requiredSetting(IConfiguration cipher, string key) {
    var value = cipher[key];
    if (string.IsNullOrWhiteSpace(value)) {
      throw new InvalidOperationException(
        $"{BODY_OFFLOAD_SECTION}:{CIPHER_SECTION}:{key} is not configured, but {BODY_OFFLOAD_SECTION}:{CIPHER_NAME_KEY} names a cipher. A named cipher without a key would store plaintext under a sealed label.");
    }
    return value;
  }

  private static ReadOnlyMemory<byte>? _keyMaterial(IConfiguration cipher, string key, bool required) {
    var value = cipher[key];
    if (string.IsNullOrWhiteSpace(value)) {
      return required ? throw new InvalidOperationException(
          $"{BODY_OFFLOAD_SECTION}:{CIPHER_SECTION}:{key} is not configured, but {BODY_OFFLOAD_SECTION}:{CIPHER_NAME_KEY} names a cipher. Provide a 32 bytes AES-256 key encryption key, base64 encoded (openssl rand -base64 32).")
        : null;
    }
    byte[] bytes;
    try {
      bytes = Convert.FromBase64String(value);
    } catch (FormatException ex) {
      throw new InvalidOperationException(
        $"{BODY_OFFLOAD_SECTION}:{CIPHER_SECTION}:{key} is not valid base64. Provide a 32 bytes AES-256 key encryption key, base64 encoded (openssl rand -base64 32).", ex);
    }
    if (bytes.Length != KEY_ENCRYPTION_KEY_BYTES) {
      throw new InvalidOperationException(
        $"{BODY_OFFLOAD_SECTION}:{CIPHER_SECTION}:{key} decodes to {bytes.Length} bytes; the key encryption key must be {KEY_ENCRYPTION_KEY_BYTES} bytes (AES-256), base64 encoded (openssl rand -base64 32).");
    }
    return bytes;
  }

  /// <summary>
  /// Registers a post-serialize hook in the publish pipeline. The chain
  /// is built at <see cref="PostSerializeHookChain"/> construction from
  /// all registered <see cref="IPostSerializeHook"/> instances ordered by
  /// <see cref="IPostSerializeHook.Order"/>.
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/PostSerializeHookChainTests.cs</tests>
  public static IServiceCollection AddWhizbangPostSerializeHook<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THook>(
      this IServiceCollection services) where THook : class, IPostSerializeHook {
    ArgumentNullException.ThrowIfNull(services);
    services.AddSingleton<IPostSerializeHook, THook>();
    services.TryAddSingleton<PostSerializeHookChain>();
    return services;
  }

  /// <summary>
  /// Convenience: register the built-in body-offload hook (claim-check
  /// pattern) along with the options binding. Use alongside
  /// <see cref="AddWhizbangMessageBodyStore{TStore}"/> and
  /// <c>services.Configure&lt;MessageBodyOffloadOptions&gt;(...)</c>;
  /// without a configured ProviderName the hook is a no-op (pass-through).
  /// </summary>
  /// <tests>tests/Whizbang.Core.Tests/Offloads/BodyOffloadPostSerializeHookTests.cs</tests>
  public static IServiceCollection AddWhizbangBodyOffload(this IServiceCollection services) {
    ArgumentNullException.ThrowIfNull(services);
    // AddOptions registers IOptions/IOptionsMonitor/IOptionsSnapshot for the
    // type even when no Configure is called — defaults still resolve.
    services.AddOptions<MessageBodyOffloadOptions>();
    return services.AddWhizbangPostSerializeHook<BodyOffloadPostSerializeHook>();
  }
}
