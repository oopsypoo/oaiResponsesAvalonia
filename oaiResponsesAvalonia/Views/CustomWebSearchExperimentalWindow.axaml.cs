using Avalonia.Controls;
using Avalonia.Interactivity;
using oaiResponsesAvalonia.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Views;

public partial class CustomWebSearchExperimentalWindow : Window
{
    private const string GoogleSafeBrowsingCredentialId = CustomWebSearchService.GoogleSafeBrowsingCredentialId;
    private const string BraveSearchCredentialId = CustomWebSearchService.BraveCredentialId;
    private readonly AppSettings _settings;
    public AppSettings Settings => _settings;
    private readonly ICredentialService _credentialService;
    private readonly bool _ownsCredentialService;

    public CustomWebSearchExperimentalWindow()
        : this(new AppSettings(), new KtsuCredentialService(), ownsCredentialService: true)
    {
    }

    public CustomWebSearchExperimentalWindow(AppSettings settings, ICredentialService credentialService)
        : this(settings, credentialService, ownsCredentialService: false)
    {
    }

    private CustomWebSearchExperimentalWindow(
        AppSettings settings,
        ICredentialService credentialService,
        bool ownsCredentialService)
    {
        InitializeComponent();
        _settings = settings;
        _credentialService = credentialService;
        _ownsCredentialService = ownsCredentialService;

        EnableCustomWebSearchCheckBox.IsChecked = settings.CustomWebSearchExperimentalEnabled;
        SearchCountryTextBox.Text = settings.CustomSearchCountry;
        SearchLanguageTextBox.Text = settings.CustomSearchLanguage;
        SearchInstructionsTextBox.Text = settings.CustomSearchAdditionalInstructions;
        SelectProvider(CustomSearchProviderComboBox, settings.CustomSearchProvider, "brave");
        SelectProvider(ReputationProviderComboBox, settings.ReputationProvider, "google-safe-browsing");

        Opened += async (_, _) =>
        {
            await RefreshCredentialStatusAsync(
                GoogleSafeBrowsingCredentialId,
                GoogleSafeBrowsingKeyStatusText,
                RemoveGoogleSafeBrowsingKeyButton,
                "Google Safe Browsing");
            await RefreshCredentialStatusAsync(
                BraveSearchCredentialId,
                BraveSearchKeyStatusText,
                RemoveBraveSearchKeyButton,
                "Brave Search");
        };
        Closed += (_, _) =>
        {
            if (_ownsCredentialService && _credentialService is IDisposable disposable)
                disposable.Dispose();
        };
    }

    private static void SelectProvider(ComboBox comboBox, string? value, string fallback)
    {
        string selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), selected, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string GetSelectedProvider(ComboBox comboBox, string fallback) =>
        comboBox.SelectedItem is ComboBoxItem item && item.Tag is not null
            ? item.Tag.ToString() ?? fallback
            : fallback;

    private async Task RefreshCredentialStatusAsync(
        string credentialId,
        TextBlock statusText,
        Button removeButton,
        string providerName)
    {
        try
        {
            bool exists = await _credentialService.ExistsAsync(credentialId);
            statusText.Text = exists
                ? $"{providerName} API key is stored in the operating system credential store."
                : $"No {providerName} API key is stored.";
            removeButton.IsEnabled = exists;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Secure credential storage is unavailable: {ex.Message}";
            removeButton.IsEnabled = false;
        }
    }

    private async void SaveBraveSearchKey_Click(object? sender, RoutedEventArgs e) =>
        await SaveCredentialAsync(BraveSearchCredentialId, BraveSearchKeyTextBox, BraveSearchKeyStatusText,
            RemoveBraveSearchKeyButton, "Brave Search");

    private async void SaveGoogleSafeBrowsingKey_Click(object? sender, RoutedEventArgs e) =>
        await SaveCredentialAsync(GoogleSafeBrowsingCredentialId, GoogleSafeBrowsingKeyTextBox,
            GoogleSafeBrowsingKeyStatusText, RemoveGoogleSafeBrowsingKeyButton, "Google Safe Browsing");

    private async Task SaveCredentialAsync(
        string credentialId,
        TextBox keyTextBox,
        TextBlock statusText,
        Button removeButton,
        string providerName)
    {
        string secret = keyTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(secret))
        {
            statusText.Text = "Enter an API key before saving.";
            return;
        }

        try
        {
            await _credentialService.SaveAsync(credentialId, secret);
            keyTextBox.Clear();
            statusText.Text = "API key saved in the operating system credential store.";
            removeButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Unable to save the API key: {ex.Message}";
        }
    }

    private async void RemoveBraveSearchKey_Click(object? sender, RoutedEventArgs e) =>
        await RemoveCredentialAsync(BraveSearchCredentialId, BraveSearchKeyTextBox, BraveSearchKeyStatusText,
            RemoveBraveSearchKeyButton, "Brave Search");

    private async void RemoveGoogleSafeBrowsingKey_Click(object? sender, RoutedEventArgs e) =>
        await RemoveCredentialAsync(GoogleSafeBrowsingCredentialId, GoogleSafeBrowsingKeyTextBox,
            GoogleSafeBrowsingKeyStatusText, RemoveGoogleSafeBrowsingKeyButton, "Google Safe Browsing");

    private async Task RemoveCredentialAsync(
        string credentialId,
        TextBox keyTextBox,
        TextBlock statusText,
        Button removeButton,
        string providerName)
    {
        try
        {
            await _credentialService.DeleteAsync(credentialId);
            keyTextBox.Clear();
            await RefreshCredentialStatusAsync(credentialId, statusText, removeButton, providerName);
        }
        catch (Exception ex)
        {
            statusText.Text = $"Unable to remove the API key: {ex.Message}";
        }
    }

    private async void TestSearchPipeline_Click(object? sender, RoutedEventArgs e)
    {
        string query = TestSearchQueryTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            SearchPipelineTestStatusText.Text = "Enter a test query first.";
            return;
        }

        SearchPipelineTestStatusText.Text = "Testing search and URL reputation providers…";
        try
        {
            using var searchService = new CustomWebSearchService(_credentialService);
            string result = await searchService.SearchAsync(
                query,
                GetSelectedProvider(CustomSearchProviderComboBox, "brave"),
                GetSelectedProvider(ReputationProviderComboBox, "google-safe-browsing"),
                _settings.CustomSearchContextSize);

            using JsonDocument document = JsonDocument.Parse(result);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
            {
                SearchPipelineTestStatusText.Text = root.TryGetProperty("error", out JsonElement error)
                    ? error.GetString() ?? "The search test failed."
                    : "The search test failed.";
                return;
            }

            int noKnownThreat = 0;
            int unknown = 0;
            int knownThreat = 0;
            var unknownDetails = new List<string>();
            if (root.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in results.EnumerateArray())
                {
                    string status = item.TryGetProperty("reputation", out JsonElement reputation)
                        ? reputation.GetString() ?? "Unknown"
                        : "Unknown";
                    if (string.Equals(status, "NoKnownThreat", StringComparison.OrdinalIgnoreCase))
                        noKnownThreat++;
                    else if (string.Equals(status, "KnownThreat", StringComparison.OrdinalIgnoreCase))
                        knownThreat++;
                    else
                    {
                        unknown++;
                        string reason = item.TryGetProperty("reputationNote", out JsonElement note)
                            ? note.GetString() ?? "No diagnostic detail was returned."
                            : "No diagnostic detail was returned.";
                        string url = item.TryGetProperty("url", out JsonElement resultUrl)
                            ? resultUrl.GetString() ?? string.Empty
                            : string.Empty;
                        unknownDetails.Add(string.IsNullOrWhiteSpace(url) ? reason : $"{url}: {reason}");
                    }
                }
            }

            string diagnosticDetails = unknownDetails.Count == 0
                ? string.Empty
                : "\nUnknown details:\n" + string.Join("\n", unknownDetails.Take(3));
            SearchPipelineTestStatusText.Text =
                $"Search test succeeded. {noKnownThreat} result(s) had no known threat match, {knownThreat} known-threat result(s) were redacted, and {unknown} were unknown. A no-match result is not a safety guarantee.{diagnosticDetails}";
        }
        catch (Exception ex)
        {
            SearchPipelineTestStatusText.Text = $"Search pipeline test failed: {ex.Message}";
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.CustomWebSearchExperimentalEnabled = EnableCustomWebSearchCheckBox.IsChecked == true;
        _settings.CustomSearchProvider = GetSelectedProvider(CustomSearchProviderComboBox, "brave");
        _settings.ReputationProvider = GetSelectedProvider(ReputationProviderComboBox, "google-safe-browsing");
        _settings.CustomSearchCountry = SearchCountryTextBox.Text?.Trim() ?? string.Empty;
        _settings.CustomSearchLanguage = SearchLanguageTextBox.Text?.Trim() ?? string.Empty;
        _settings.CustomSearchAdditionalInstructions = SearchInstructionsTextBox.Text?.Trim() ?? string.Empty;
        AppSettings.SaveSettings(_settings);
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
