using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace oaiResponsesAvalonia.Services
{
    public static class McpTransportKinds
    {
        public const string Http = "http";
        public const string Stdio = "stdio";
    }

    public sealed class McpToolSetting : ObservableObject
    {
        private string _name = string.Empty;
        private string _description = string.Empty;
        private bool? _readOnlyHint;
        private bool _enabled;
        private bool _requireApproval = true;
        private bool _available = true;

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string Description { get => _description; set => SetProperty(ref _description, value); }
        public bool? ReadOnlyHint { get => _readOnlyHint; set => SetProperty(ref _readOnlyHint, value); }

        // Whether the AI may see and use this tool.
        public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

        // Whether each call must be approved by the user first.
        public bool RequireApproval { get => _requireApproval; set => SetProperty(ref _requireApproval, value); }

        // False when the server no longer lists a tool that was seen earlier.
        [JsonIgnore]
        public bool Available { get => _available; set => SetProperty(ref _available, value); }
    }

    public sealed class McpServerConfig : ObservableObject
    {
        private string _name = "New server";
        private string _transport = McpTransportKinds.Http;
        private string _url = string.Empty;
        private string _command = string.Empty;
        private string _arguments = string.Empty;
        private string _environmentText = string.Empty;
        private string _headersText = string.Empty;
        private bool _enabled = true;
        private string _status = string.Empty;

        public string Name { get => _name; set => SetProperty(ref _name, value); }

        public string Transport
        {
            get => _transport;
            set
            {
                if (SetProperty(ref _transport, value))
                {
                    OnPropertyChanged(nameof(IsHttp));
                    OnPropertyChanged(nameof(IsStdio));
                }
            }
        }

        public string Url { get => _url; set => SetProperty(ref _url, value); }
        public string Command { get => _command; set => SetProperty(ref _command, value); }
        public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }

        // One KEY=VALUE per line. Values may reference ${ENV_VAR} so secrets are not stored here.
        public string EnvironmentText { get => _environmentText; set => SetProperty(ref _environmentText, value); }

        // One "Header: value" per line. Values may reference ${ENV_VAR}.
        public string HeadersText { get => _headersText; set => SetProperty(ref _headersText, value); }

        public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }

        public ObservableCollection<McpToolSetting> Tools { get; set; } = new();

        [JsonIgnore]
        public bool IsHttp => string.Equals(Transport, McpTransportKinds.Http, System.StringComparison.OrdinalIgnoreCase);

        [JsonIgnore]
        public bool IsStdio => !IsHttp;

        [JsonIgnore]
        public string Status { get => _status; set => SetProperty(ref _status, value); }
    }

    public sealed record McpToolInfo(string Name, string Description, bool? ReadOnlyHint);
}
