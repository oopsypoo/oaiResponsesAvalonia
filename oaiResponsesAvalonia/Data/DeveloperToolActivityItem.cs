using System;
using System.ComponentModel;
using System.Text.Json;

namespace oaiResponsesAvalonia.Data;

public sealed class DeveloperToolActivityItem : INotifyPropertyChanged
{
    private string _status = string.Empty;
    private string _resultSummary = string.Empty;
    private string _resultDetails = string.Empty;

    public string CallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.Now;

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(Header));
        }
    }

    public string ResultSummary
    {
        get => _resultSummary;
        private set
        {
            if (_resultSummary == value) return;
            _resultSummary = value;
            OnPropertyChanged(nameof(ResultSummary));
            OnPropertyChanged(nameof(Details));
        }
    }

    public string ResultDetails
    {
        get => _resultDetails;
        private set
        {
            if (_resultDetails == value) return;
            _resultDetails = value;
            OnPropertyChanged(nameof(ResultDetails));
            OnPropertyChanged(nameof(Details));
        }
    }

    public string Header => $"{ToolName} — {Status}";

    public string Details
    {
        get
        {
            string target = string.IsNullOrWhiteSpace(Target) ? string.Empty : $"Target: {Target}\n";
            string summary = string.IsNullOrWhiteSpace(ResultSummary) ? string.Empty : $"Result: {ResultSummary}\n";
            string raw = string.IsNullOrWhiteSpace(ResultDetails) ? string.Empty : $"\nDetails:\n{ResultDetails}";
            return $"{target}{summary}Started: {StartedAt:G}{raw}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(string status, string resultJson)
    {
        Status = status;
        ResultDetails = resultJson ?? string.Empty;
        ResultSummary = SummarizeResult(resultJson);
    }

    public static string GetTarget(string argumentsJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return string.Empty;
            foreach (string name in new[] { "path", "project_path", "solution_path", "target_path", "project", "subpath", "projectPath", "solutionPath", "targetPath" })
            {
                if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    string? target = value.GetString();
                    if (!string.IsNullOrWhiteSpace(target))
                        return target;
                }
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    public static string SummarizeResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
            return string.Empty;

        try
        {
            using JsonDocument document = JsonDocument.Parse(resultJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "Operation finished.";
            foreach (string name in new[] { "error", "summary", "message", "status" })
            {
                if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                {
                    string? text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return Truncate(text.Trim(), 200);
                }
            }

            if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
                return "Operation reported an error.";
        }
        catch (JsonException)
        {
            return Truncate(resultJson.Trim(), 200);
        }

        return "Operation finished.";
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
