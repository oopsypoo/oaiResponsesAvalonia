namespace CredentialCachePlayground.Services;

public interface ICredentialService
{
    string StoreName { get; }

    Task SaveAsync(string credentialId, string secret);
    Task<string?> GetAsync(string credentialId);
    Task<string?> GetFromFreshCacheAsync(string credentialId);
    Task<bool> ExistsAsync(string credentialId);
    Task<bool> DeleteAsync(string credentialId);
}
