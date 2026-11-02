using System.Security.Cryptography;
using System.Text;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using ktsu.Semantics.Strings;

namespace CredentialCachePlayground.Services;

public sealed class KtsuCredentialService : ICredentialService, IDisposable
{
    private const string ServiceName = "CredentialCachePlayground";
    private CredentialCache? _cache;
    private ICredentialStore? _store;

    public string StoreName => GetCache().Store.Name;

    public Task SaveAsync(string credentialId, string secret) => Task.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        var persona = ToPersona(credentialId);
        GetCache().AddOrReplace(persona, new CredentialWithToken
        {
            Token = SemanticString<CredentialToken>.Create(secret)
        });
    });

    public Task<string?> GetAsync(string credentialId) => Task.Run(() =>
    {
        var cache = GetCache();
        return cache.TryGet(ToPersona(credentialId), out Credential? credential)
            && credential is CredentialWithToken tokenCredential
                ? tokenCredential.Token.ToString()
                : null;
    });

    public Task<string?> GetFromFreshCacheAsync(string credentialId) => Task.Run(() =>
    {
        // A new CredentialCache instance has no in-process entry, so this verifies
        // that the value can be loaded again from the selected operating-system store.
        _cache?.Dispose();
        _cache = CreateCache();

        return _cache.TryGet(ToPersona(credentialId), out Credential? credential)
            && credential is CredentialWithToken tokenCredential
                ? tokenCredential.Token.ToString()
                : null;
    });

    public Task<bool> ExistsAsync(string credentialId) => Task.Run(() =>
        GetCache().TryGet(ToPersona(credentialId), out _));

    public Task<bool> DeleteAsync(string credentialId) => Task.Run(() =>
        GetCache().Remove(ToPersona(credentialId)));

    public void Dispose() => _cache?.Dispose();

    private CredentialCache GetCache() => _cache ??= CreateCache();

    private CredentialCache CreateCache()
    {
        _store ??= CredentialStoreFactory.CreateDefault(ServiceName);
        return new CredentialCache(_store);
    }

    private static PersonaGUID ToPersona(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);

        // PersonaGUID is a semantic string. A stable GUID derived from the caller's
        // credential ID makes the same entry addressable after an application restart.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credentialId.Trim()));
        var stableGuid = new Guid(hash.AsSpan(0, 16)).ToString("D");
        return SemanticString<PersonaGUID>.Create(stableGuid);
    }
}
