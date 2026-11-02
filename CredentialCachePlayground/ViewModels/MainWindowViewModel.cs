using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CredentialCachePlayground.Services;

namespace CredentialCachePlayground.ViewModels;

public partial class MainWindowViewModel(ICredentialService credentialService) : ObservableObject
{
    [ObservableProperty]
    private string _credentialId = "GoogleSafeBrowsing.ApiKey.Test";

    [ObservableProperty]
    private string _secret = string.Empty;

    [ObservableProperty]
    private string _status = "Ready. No credential operation has been performed.";

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Secret))
        {
            Status = "Enter a test credential before saving.";
            return;
        }

        await RunAsync(async () =>
        {
            await credentialService.SaveAsync(CredentialId, Secret);
            Status = $"Credential saved to {credentialService.StoreName}. The secret is not displayed.";
        });
    }

    [RelayCommand]
    private async Task RetrieveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var value = await credentialService.GetAsync(CredentialId);
            if (value is null)
            {
                Status = "No credential was found for this ID.";
                return;
            }

            Secret = value;
            Status = $"Credential retrieved from the current cache ({credentialService.StoreName}); the input remains masked.";
        });
    }

    [RelayCommand]
    private async Task RetrieveFromFreshCacheAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var value = await credentialService.GetFromFreshCacheAsync(CredentialId);
            if (value is null)
            {
                Status = "No credential was found when loading through a fresh cache instance.";
                return;
            }

            Secret = value;
            Status = $"Credential restored through a fresh cache instance from {credentialService.StoreName}; the input remains masked.";
        });
    }

    [RelayCommand]
    private async Task CheckExistsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var exists = await credentialService.ExistsAsync(CredentialId);
            Status = exists
                ? $"A credential exists for this ID ({credentialService.StoreName})."
                : "No credential exists for this ID.";
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var removed = await credentialService.DeleteAsync(CredentialId);
            Secret = string.Empty;
            Status = removed
                ? $"Credential deleted from {credentialService.StoreName}."
                : "No credential existed for this ID.";
        });
    }

    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            // Do not include credential values in error/status output.
            Status = $"Credential operation failed ({ex.GetType().Name}): {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
