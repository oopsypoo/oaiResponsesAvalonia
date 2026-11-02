using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;
using ktsu.Semantics.Strings;

namespace oaiResponsesAvalonia.Services;

public sealed class KtsuCredentialService : ICredentialService, IDisposable
{
    private const string StoreName = "oaiResponsesAvalonia";
    private readonly object _sync = new();
    private CredentialCache? _cache;
    private ICredentialStore? _store;
    private bool _disposed;

    public Task SaveAsync(string credentialId, string secret) => Task.Run(() =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        lock (_sync)
        {
            ThrowIfDisposed();
            GetCache().AddOrReplace(ToPersona(credentialId), new CredentialWithToken
            {
                Token = SemanticString<CredentialToken>.Create(secret)
            });
        }
    });

    public Task<string?> GetAsync(string credentialId) => Task.Run(() =>
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return GetCache().TryGet(ToPersona(credentialId), out Credential? credential) &&
                   credential is CredentialWithToken tokenCredential
                ? tokenCredential.Token.ToString()
                : null;
        }
    });

    public Task<bool> ExistsAsync(string credentialId) => Task.Run(() =>
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return GetCache().TryGet(ToPersona(credentialId), out _);
        }
    });

    public Task<bool> DeleteAsync(string credentialId) => Task.Run(() =>
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return GetCache().Remove(ToPersona(credentialId));
        }
    });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _cache?.Dispose();
            _cache = null;
            _disposed = true;
        }
    }

    private CredentialCache GetCache()
    {
        if (_cache is not null)
            return _cache;

        _store ??= CredentialStoreFactory.CreateDefault(StoreName);
        _cache = new CredentialCache(_store);
        return _cache;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static PersonaGUID ToPersona(string credentialId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialId);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credentialId.Trim()));
        var stableGuid = new Guid(hash.AsSpan(0, 16)).ToString("D");
        return SemanticString<PersonaGUID>.Create(stableGuid);
    }
}
