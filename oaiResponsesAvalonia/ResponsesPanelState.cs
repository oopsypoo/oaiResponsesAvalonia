using oaiResponsesAvalonia.Data;
using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace oaiResponsesAvalonia
{
    public class ResponsesPanelState : ObservableObject
    {
        public bool IsWebSearchOptionsEnabled => UseWebSearch;
        public bool IsImageGenerationOptionsEnabled => UseImageGeneration;
        public bool IsImageGenerationOptionsVisible => UseImageGeneration;

        private string _promptText = string.Empty;
        public string PromptText
        {
            get => _promptText;
            set => SetProperty(ref _promptText, value ?? string.Empty);
        }
        
        private ChatMessage _selectedTurn;
        public ChatMessage SelectedTurn
        {
            get => _selectedTurn;
            set => SetProperty(ref _selectedTurn, value);
        }
        private string _selectedModel = string.Empty;
        public string SelectedModel
        {
            get => _selectedModel;
            set => SetProperty(ref _selectedModel, value ?? string.Empty);
        }

        private string _selectedReasoning = "none";
        public string SelectedReasoning
        {
            get => _selectedReasoning;
            set => SetProperty(ref _selectedReasoning, string.IsNullOrWhiteSpace(value) ? "none" : value);
        }

        private bool _useTextTool = true;
        public bool UseTextTool
        {
            get => _useTextTool;
            set => SetProperty(ref _useTextTool, value);
        }

        private bool _useWebSearch;
        public bool UseWebSearch
        {
            get => _useWebSearch;
            set
            {
                if (SetProperty(ref _useWebSearch, value))
                    OnPropertyChanged(nameof(IsWebSearchOptionsEnabled));
            }
        }

        private bool _useImageGeneration;
        public bool UseImageGeneration
        {
            get => _useImageGeneration;
            set
            {
                if (_useImageGeneration != value)
                {
                    _useImageGeneration = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsImageGenerationOptionsEnabled));
                    OnPropertyChanged(nameof(IsImageGenerationOptionsVisible));
                    OnPropertyChanged(nameof(IsOutputCompressionEnabled));
                    OnPropertyChanged(nameof(IsTransparentBackgroundAllowed));
                }
            }
        }

        private string _searchContextSize = "medium";
        public string SearchContextSize
        {
            get => _searchContextSize;
            set => SetProperty(ref _searchContextSize, string.IsNullOrWhiteSpace(value) ? "medium" : value);
        }

        private string _imageGenQuality = "auto";
        public string ImageGenQuality
        {
            get => _imageGenQuality;
            set => SetProperty(ref _imageGenQuality, NormalizeImageSetting(value, "auto", "auto", "low", "medium", "high"));
        }

        private string _imageGenSize = "auto";
        public string ImageGenSize
        {
            get => _imageGenSize;
            set => SetProperty(ref _imageGenSize, NormalizeImageSetting(value, "auto", "auto", "1024x1024", "1536x1024", "1024x1536"));
        }
        private string _imageGenOutputFormat = "jpeg";
        public string ImageGenOutputFormat
        {
            get => _imageGenOutputFormat;
            set
            {
                string normalizedValue = NormalizeImageSetting(value, "jpeg", "jpeg", "png", "webp");
                if (_imageGenOutputFormat != normalizedValue)
                {
                    _imageGenOutputFormat = normalizedValue;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsOutputCompressionEnabled));
                    OnPropertyChanged(nameof(IsTransparentBackgroundAllowed));
                }
            }
        }

        private int _imageGenOutputCompression = 85;
        public int ImageGenOutputCompression
        {
            get => _imageGenOutputCompression;
            set
            {
                int normalizedValue = Math.Clamp(value, 0, 100);
                if (_imageGenOutputCompression != normalizedValue)
                {
                    _imageGenOutputCompression = normalizedValue;
                    OnPropertyChanged();
                }
            }
        }

        private string _imageGenBackground = "auto";
        public string ImageGenBackground
        {
            get => _imageGenBackground;
            set
            {
                string normalizedValue = NormalizeImageSetting(value, "auto", "auto", "opaque", "transparent");
                if (_imageGenBackground != normalizedValue)
                {
                    _imageGenBackground = normalizedValue;
                    OnPropertyChanged();
                }
            }
        }

        

        public bool IsOutputCompressionEnabled =>
            string.Equals(ImageGenOutputFormat, "jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ImageGenOutputFormat, "webp", StringComparison.OrdinalIgnoreCase);

        public bool IsTransparentBackgroundAllowed =>
            UseImageGeneration &&
            (string.Equals(ImageGenOutputFormat, "png", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(ImageGenOutputFormat, "webp", StringComparison.OrdinalIgnoreCase));

        private static string NormalizeImageSetting(string? value, string fallback, params string[] allowedValues)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                foreach (string allowedValue in allowedValues)
                {
                    if (string.Equals(value, allowedValue, StringComparison.OrdinalIgnoreCase))
                        return allowedValue;
                }
            }

            return fallback;
        }

        //Developer functions-controls
        private bool _useDeveloperTools;
        public bool UseDeveloperTools
        {
            get => _useDeveloperTools;
            set
            {
                if (_useDeveloperTools == value) return;
                _useDeveloperTools = value;
                OnPropertyChanged(nameof(UseDeveloperTools));
                OnPropertyChanged(nameof(IsDeveloperToolsOptionsVisible));
                OnPropertyChanged(nameof(DeveloperToolLogsVisible));
            }
        }

        private string _developerRepositoryRoot = string.Empty;
        public string DeveloperRepositoryRoot
        {
            get => _developerRepositoryRoot;
            set
            {
                if (_developerRepositoryRoot == value) return;
                _developerRepositoryRoot = value;
                OnPropertyChanged(nameof(DeveloperRepositoryRoot));
            }
        }

        private string _developerScope = "repository";
        public string DeveloperScope
        {
            get => _developerScope;
            set
            {
                if (_developerScope == value) return;
                _developerScope = value;
                OnPropertyChanged(nameof(DeveloperScope));
            }
        }

        private bool _developerAllowReadOnlyOnly = true;
        public bool DeveloperAllowReadOnlyOnly
        {
            get => _developerAllowReadOnlyOnly;
            set
            {
                if (_developerAllowReadOnlyOnly == value) return;
                _developerAllowReadOnlyOnly = value;
                OnPropertyChanged(nameof(DeveloperAllowReadOnlyOnly));
                OnPropertyChanged(nameof(DeveloperAllowWriteRun));
            }
        }

        // Inverse of DeveloperAllowReadOnlyOnly, bound to the "Allow write/run" checkbox.
        public bool DeveloperAllowWriteRun
        {
            get => !DeveloperAllowReadOnlyOnly;
            set => DeveloperAllowReadOnlyOnly = !value;
        }

        private bool _developerRequireApproval = true;
        public bool DeveloperRequireApproval
        {
            get => _developerRequireApproval;
            set
            {
                if (_developerRequireApproval == value) return;
                _developerRequireApproval = value;
                OnPropertyChanged(nameof(DeveloperRequireApproval));
            }
        }

        public bool DeveloperToolLogsVisible => UseDeveloperTools && DeveloperShowToolLogs;

        private bool _developerShowToolLogs = true;
        public bool DeveloperShowToolLogs
        {
            get => _developerShowToolLogs;
            set
            {
                if (_developerShowToolLogs == value) return;
                _developerShowToolLogs = value;
                OnPropertyChanged(nameof(DeveloperShowToolLogs));
                OnPropertyChanged(nameof(DeveloperToolLogsVisible));
            }
        }

        //Set static, because of availability.It's called upon many times. Use GetDefaultAllowedExtensionsCsv()
        private static string _developerAllowedExtensionsCsv = ".cs,.xaml,.axaml,.csproj,.sln,.slnx,.json,.xml,.md,.config,.props,.targets,.js,.css,.html";
        /// <summary>
        /// Returns a comma-separated list of the default allowed file extensions for developers.
        /// </summary>
        /// <returns>A string containing the default allowed file extensions, separated by commas.</returns>
        public static string GetDefaultAllowedExtensionsCsv()
        {
            return _developerAllowedExtensionsCsv;
        }

        public string DeveloperAllowedExtensionsCsv
        {
            get => _developerAllowedExtensionsCsv;
            set
            {
                if (_developerAllowedExtensionsCsv == value) return;
                _developerAllowedExtensionsCsv = value;
                OnPropertyChanged(nameof(DeveloperAllowedExtensionsCsv));
            }
        }

        public bool IsDeveloperToolsOptionsVisible => UseDeveloperTools;
        private bool _isRequestInProgress;
        public bool IsRequestInProgress
        {
            get => _isRequestInProgress;
            set
            {
                if (_isRequestInProgress != value)
                {
                    _isRequestInProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AreRequestEditingControlsEnabled));
                }
            }
        }
        public bool AreRequestEditingControlsEnabled => !IsRequestInProgress;

        private bool _developerPendingReviewVisible;
        public bool DeveloperPendingReviewVisible
        {
            get => _developerPendingReviewVisible;
            set
            {
                if (_developerPendingReviewVisible == value) return;
                _developerPendingReviewVisible = value;
                OnPropertyChanged(nameof(DeveloperPendingReviewVisible));
            }
        }

        private string _developerPendingReviewTitle = string.Empty;
        public string DeveloperPendingReviewTitle
        {
            get => _developerPendingReviewTitle;
            set
            {
                if (_developerPendingReviewTitle == value) return;
                _developerPendingReviewTitle = value ?? string.Empty;
                OnPropertyChanged(nameof(DeveloperPendingReviewTitle));
            }
        }

        private string _developerPendingReviewToolName = string.Empty;
        public string DeveloperPendingReviewToolName
        {
            get => _developerPendingReviewToolName;
            set
            {
                if (_developerPendingReviewToolName == value) return;
                _developerPendingReviewToolName = value ?? string.Empty;
                OnPropertyChanged(nameof(DeveloperPendingReviewToolName));
            }
        }

        private string _developerPendingReviewSummary = string.Empty;
        public string DeveloperPendingReviewSummary
        {
            get => _developerPendingReviewSummary;
            set
            {
                if (_developerPendingReviewSummary == value) return;
                _developerPendingReviewSummary = value ?? string.Empty;
                OnPropertyChanged(nameof(DeveloperPendingReviewSummary));
            }
        }
    }

}