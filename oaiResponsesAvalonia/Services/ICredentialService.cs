using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

public interface ICredentialService
{
    Task SaveAsync(string credentialId, string secret);
    Task<string?> GetAsync(string credentialId);
    Task<bool> ExistsAsync(string credentialId);
    Task<bool> DeleteAsync(string credentialId);
}
