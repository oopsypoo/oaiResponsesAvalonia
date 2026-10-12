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
            set => SetProperty(ref _imageGenQuality, string.IsNullOrWhiteSpace(value) ? "auto" : value);
        }

        private string _imageGenSize = "auto";
        public string ImageGenSize
        {
            get => _imageGenSize;
            set => SetProperty(ref _imageGenSize, string.IsNullOrWhiteSpace(value) ? "auto" : value);
        }
        private string _imageGenOutputFormat = "jpeg";
        public string ImageGenOutputFormat
        {
            get => _imageGenOutputFormat;
            set
            {
                if (_imageGenOutputFormat != value)
                {
                    _imageGenOutputFormat = value;
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
                if (_imageGenOutputCompression != value)
                {
                    _imageGenOutputCompression = value;
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
                if (_imageGenBackground != value)
                {
                    _imageGenBackground = value;
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
                OnPropertyChanged(nameof(DeveloperWriteFunctionsEnabled));
            }
        }

        public bool DeveloperWriteFunctionsEnabled => !DeveloperAllowReadOnlyOnly;

        private bool _developerRequireWriteConfirmation = true;
        public bool DeveloperRequireWriteConfirmation
        {
            get => _developerRequireWriteConfirmation;
            set
            {
                if (_developerRequireWriteConfirmation == value) return;
                _developerRequireWriteConfirmation = value;
                OnPropertyChanged(nameof(DeveloperRequireWriteConfirmation));
            }
        }

        private bool _developerRequireExecutionConfirmation = true;
        public bool DeveloperRequireExecutionConfirmation
        {
            get => _developerRequireExecutionConfirmation;
            set
            {
                if (_developerRequireExecutionConfirmation == value) return;
                _developerRequireExecutionConfirmation = value;
                OnPropertyChanged(nameof(DeveloperRequireExecutionConfirmation));
            }
        }

        private bool _developerRequireConfirmation;
        public bool DeveloperRequireConfirmation
        {
            get => _developerRequireConfirmation;
            set
            {
                if (_developerRequireConfirmation == value) return;
                _developerRequireConfirmation = value;
                OnPropertyChanged(nameof(DeveloperRequireConfirmation));
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

        private bool _developerToolSearchProjectText = true;
        public bool DeveloperToolSearchProjectText
        {
            get => _developerToolSearchProjectText;
            set
            {
                if (_developerToolSearchProjectText == value) return;
                _developerToolSearchProjectText = value;
                OnPropertyChanged(nameof(DeveloperToolSearchProjectText));
            }
        }

        private bool _developerToolReadProjectFile = true;
        public bool DeveloperToolReadProjectFile
        {
            get => _developerToolReadProjectFile;
            set
            {
                if (_developerToolReadProjectFile == value) return;
                _developerToolReadProjectFile = value;
                OnPropertyChanged(nameof(DeveloperToolReadProjectFile));
            }
        }

        private bool _developerToolListProjectFiles;
        public bool DeveloperToolListProjectFiles
        {
            get => _developerToolListProjectFiles;
            set
            {
                if (_developerToolListProjectFiles == value) return;
                _developerToolListProjectFiles = value;
                OnPropertyChanged(nameof(DeveloperToolListProjectFiles));
            }
        }

        private bool _developerToolCreateDotNetSolution;
        public bool DeveloperToolCreateDotNetSolution
        {
            get => _developerToolCreateDotNetSolution;
            set
            {
                if (_developerToolCreateDotNetSolution == value) return;
                _developerToolCreateDotNetSolution = value;
                OnPropertyChanged(nameof(DeveloperToolCreateDotNetSolution));
            }
        }

        private bool _developerToolBuildDotNetProject;
        public bool DeveloperToolBuildDotNetProject
        {
            get => _developerToolBuildDotNetProject;
            set
            {
                if (_developerToolBuildDotNetProject == value) return;
                _developerToolBuildDotNetProject = value;
                OnPropertyChanged(nameof(DeveloperToolBuildDotNetProject));
            }
        }

        private bool _developerToolRunDotNetProject;
        public bool DeveloperToolRunDotNetProject
        {
            get => _developerToolRunDotNetProject;
            set
            {
                if (_developerToolRunDotNetProject == value) return;
                _developerToolRunDotNetProject = value;
                OnPropertyChanged(nameof(DeveloperToolRunDotNetProject));
            }
        }

        private bool _developerToolRunDiagnostics;
        public bool DeveloperToolRunDiagnostics
        {
            get => _developerToolRunDiagnostics;
            set
            {
                if (_developerToolRunDiagnostics == value) return;
                _developerToolRunDiagnostics = value;
                OnPropertyChanged(nameof(DeveloperToolRunDiagnostics));
            }
        }
        //Set static, because of availability.It's called upon many times. Use GetDefaultAllowedExtensionsCsv()
        private static string _developerAllowedExtensionsCsv = ".cs,.xaml,.csproj,.sln,.slnx,.json,.xml,.md,.config,.props,.targets,.xaml,.js,.css,.html";
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
        private bool _developerToolWriteProjectFile;
        public bool DeveloperToolWriteProjectFile
        {
            get => _developerToolWriteProjectFile;
            set
            {
                if (_developerToolWriteProjectFile == value) return;
                _developerToolWriteProjectFile = value;
                OnPropertyChanged(nameof(DeveloperToolWriteProjectFile));
            }
        }

        private bool _developerToolReplaceInProjectFile;
        public bool DeveloperToolReplaceInProjectFile
        {
            get => _developerToolReplaceInProjectFile;
            set
            {
                if (_developerToolReplaceInProjectFile == value) return;
                _developerToolReplaceInProjectFile = value;
                OnPropertyChanged(nameof(DeveloperToolReplaceInProjectFile));
            }
        }

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