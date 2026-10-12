using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace oaiResponsesAvalonia.Services
{
    /// <summary>
    /// Collects a lightweight, read-only snapshot of the host and workspace.
    /// This class and its result models are UI-framework independent.
    /// </summary>
    public sealed class EnvironmentCapabilityReportService
    {
        private const int MaximumWorkspaceFiles = 10000;
        private const int MaximumWorkspaceDirectories = 2000;
        private const int MaximumWorkspaceDepth = 6;
        private const int MaximumProjectFileEntries = 100;

        private static readonly string[] ExcludedDirectoryNames =
        {
            ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "target", "venv", ".venv"
        };

        private static readonly string[] CandidateTools =
        {
            "dotnet", "cargo", "rustc", "python", "python3", "pip", "pip3",
            "cmake", "ninja", "make", "gcc", "g++", "clang", "clang++",
            "qmake", "qtpaths", "node", "npm", "git"
        };

        public EnvironmentCapabilityReport CreateReport(string? workspaceRoot)
        {
            var report = new StringBuilder();
            WorkspaceTreeNode? workspaceTree = null;

            report.AppendLine("Environment capabilities report");
            report.AppendLine("Generated: " + DateTimeOffset.Now.ToString("u"));
            report.AppendLine();
            report.AppendLine("Host");
            report.AppendLine("- Operating system: " + RuntimeInformation.OSDescription);
            report.AppendLine("- OS platform: " + GetPlatformName());
            report.AppendLine("- Architecture: " + RuntimeInformation.OSArchitecture);
            report.AppendLine("- Application runtime: .NET " + Environment.Version);
            report.AppendLine();
            report.AppendLine("Detected tools (available on PATH)");

            foreach (string tool in CandidateTools)
            {
                string? location = FindTool(tool);
                report.AppendLine("- " + tool + ": " + (location ?? "not found"));
            }

            report.AppendLine();
            report.AppendLine("Workspace contents and project files");

            string root = (workspaceRoot ?? string.Empty).Trim();
            if (root.Length == 0)
            {
                report.AppendLine("- No workspace root is set.");
            }
            else if (!Directory.Exists(root))
            {
                report.AppendLine("- The configured workspace path does not exist or is not accessible.");
            }
            else
            {
                string fullRoot = Path.GetFullPath(root);
                report.AppendLine("- Root: " + fullRoot);
                WorkspaceScanResult scan = ScanWorkspace(fullRoot);
                workspaceTree = scan.RootNode;
                report.AppendLine("- Files inspected: " + scan.FilesInspected + (scan.ReachedFileLimit ? " (file limit reached; there may be more files)" : string.Empty));
                report.AppendLine("- Directories inspected: " + scan.DirectoriesInspected + (scan.ReachedDirectoryLimit ? " (directory limit reached; there may be more folders)" : string.Empty));

                if (scan.FilesInspected == 0)
                    report.AppendLine("- No files were found within the scanned workspace depth.");
                else
                {
                    report.AppendLine();
                    report.AppendLine("File types (by extension)");
                    foreach (var item in scan.ExtensionCounts.OrderByDescending(item => item.Value).ThenBy(item => item.Key).Take(20))
                        report.AppendLine("- " + item.Key + ": " + item.Value);
                    if (scan.ExtensionCounts.Count > 20)
                        report.AppendLine("- Other extensions: " + (scan.ExtensionCounts.Count - 20) + " additional types");
                }

                report.AppendLine();
                report.AppendLine("Recognized project and configuration files");
                if (scan.ProjectFileCount == 0)
                {
                    report.AppendLine("- No recognized project or solution files found.");
                    report.AppendLine("- Likely project ecosystems: unknown or not yet initialized");
                }
                else
                {
                    foreach (string projectFile in scan.ProjectFiles)
                        report.AppendLine("- " + projectFile);
                    if (scan.ProjectFileCount > scan.ProjectFiles.Count)
                        report.AppendLine("- ... project file list limited to " + MaximumProjectFileEntries + " entries");
                    report.AppendLine("- Likely project ecosystems: " + string.Join(", ", IdentifyProjectTypes(scan.ProjectFiles)));
                }
            }

            report.AppendLine();
            report.AppendLine("This is an informational snapshot. Tool availability is detected from PATH; no commands were run.");
            report.AppendLine("The workspace tree lists paths only; file contents are not read. Common generated/dependency folders are shown as skipped. Scanning is limited to depth " + MaximumWorkspaceDepth + ", " + MaximumWorkspaceFiles + " files, and " + MaximumWorkspaceDirectories + " directories.");
            report.AppendLine("Project type detection is based on filenames and may not identify every project in a workspace.");
            return new EnvironmentCapabilityReport(report.ToString(), workspaceTree);
        }

        private static string GetPlatformName()
        {
            if (OperatingSystem.IsWindows()) return "Windows";
            if (OperatingSystem.IsLinux()) return "Linux";
            if (OperatingSystem.IsMacOS()) return "macOS";
            return "Other";
        }

        private static string? FindTool(string toolName)
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> extensions = GetExecutableExtensions();

            foreach (string directory in directories)
            {
                foreach (string extension in extensions)
                {
                    try
                    {
                        string candidate = Path.Combine(directory.Trim(), toolName + extension);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                    catch (ArgumentException) { }
                    catch (NotSupportedException) { }
                    catch (PathTooLongException) { }
                }
            }

            return null;
        }

        private static IEnumerable<string> GetExecutableExtensions()
        {
            if (!OperatingSystem.IsWindows())
                return new[] { string.Empty };

            string? pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrWhiteSpace(pathExt))
                pathExt = ".COM;.EXE;.BAT;.CMD";

            return new[] { string.Empty }.Concat(pathExt.Split(';').Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        private static WorkspaceScanResult ScanWorkspace(string root)
        {
            var rootInfo = new DirectoryInfo(root);
            var rootNode = new WorkspaceTreeNode(string.IsNullOrEmpty(rootInfo.Name) ? root : rootInfo.Name, true);
            var result = new WorkspaceScanResult(rootNode) { DirectoriesDiscovered = 1 };
            var pending = new Stack<(string Directory, int Depth, WorkspaceTreeNode Node)>();
            pending.Push((root, 0, rootNode));

            while (pending.Count > 0 && result.FilesInspected < MaximumWorkspaceFiles && result.DirectoriesInspected < MaximumWorkspaceDirectories)
            {
                var current = pending.Pop();
                result.DirectoriesInspected++;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(current.Directory))
                    {
                        if (result.FilesInspected >= MaximumWorkspaceFiles)
                        {
                            result.ReachedFileLimit = true;
                            break;
                        }

                        result.FilesInspected++;
                        string name = Path.GetFileName(file);
                        string relativePath = Path.GetRelativePath(root, file);
                        current.Node.Children.Add(new WorkspaceTreeNode(name, false));
                        string extension = Path.GetExtension(name);
                        string extensionLabel = string.IsNullOrEmpty(extension) ? "[no extension]" : extension.ToLowerInvariant();
                        result.ExtensionCounts[extensionLabel] = result.ExtensionCounts.TryGetValue(extensionLabel, out int count) ? count + 1 : 1;

                        if (IsProjectFile(name))
                        {
                            result.ProjectFileCount++;
                            if (result.ProjectFiles.Count < MaximumProjectFileEntries)
                                result.ProjectFiles.Add(relativePath);
                        }
                    }

                    if (current.Depth >= MaximumWorkspaceDepth)
                    {
                        current.Node.Name += " (depth limit)";
                        continue;
                    }
                    if (result.FilesInspected >= MaximumWorkspaceFiles)
                        continue;

                    foreach (string directory in Directory.EnumerateDirectories(current.Directory))
                    {
                        if (result.DirectoriesDiscovered >= MaximumWorkspaceDirectories)
                        {
                            result.ReachedDirectoryLimit = true;
                            current.Node.Children.Add(new WorkspaceTreeNode("[additional folders omitted: directory limit]", true));
                            break;
                        }

                        string name = Path.GetFileName(directory);
                        bool excluded = ExcludedDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase);
                        var childNode = new WorkspaceTreeNode(excluded ? name + " (skipped)" : name, true);
                        current.Node.Children.Add(childNode);
                        result.DirectoriesDiscovered++;
                        if (excluded)
                            continue;

                        try
                        {
                            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                            {
                                if (result.DirectoriesInspected + pending.Count >= MaximumWorkspaceDirectories)
                                {
                                    result.ReachedDirectoryLimit = true;
                                    childNode.Name += " (not scanned: directory limit)";
                                    continue;
                                }
                                pending.Push((directory, current.Depth + 1, childNode));
                            }
                            else
                            {
                                childNode.Name += " (not followed: link)";
                            }
                        }
                        catch (UnauthorizedAccessException) { childNode.Name += " (not accessible)"; }
                        catch (IOException) { childNode.Name += " (not accessible)"; }
                    }
                }
                catch (UnauthorizedAccessException) { current.Node.Name += " (not accessible)"; }
                catch (IOException) { current.Node.Name += " (not accessible)"; }
            }

            if (pending.Count > 0 && result.DirectoriesInspected >= MaximumWorkspaceDirectories)
                result.ReachedDirectoryLimit = true;
            if (result.FilesInspected >= MaximumWorkspaceFiles)
                result.ReachedFileLimit = true;

            SortTree(rootNode);
            result.ProjectFiles.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        private static void SortTree(WorkspaceTreeNode node)
        {
            node.Children.Sort((left, right) =>
            {
                int directoryOrder = right.IsDirectory.CompareTo(left.IsDirectory);
                return directoryOrder != 0 ? directoryOrder : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            });
            foreach (WorkspaceTreeNode child in node.Children)
                SortTree(child);
        }

        private static bool IsProjectFile(string name)
        {
            string extension = Path.GetExtension(name);
            return name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)
                || name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vcxproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".pro", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".pro.user", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> IdentifyProjectTypes(IReadOnlyList<string> files)
        {
            var types = new List<string>();
            if (files.Any(IsDotNetProjectFile)) types.Add(".NET");
            if (files.Any(x => Path.GetFileName(x).Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))) types.Add("Rust");
            if (files.Any(x => new[] { "pyproject.toml", "requirements.txt", "Pipfile" }.Contains(Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))) types.Add("Python");
            if (files.Any(x => Path.GetFileName(x).Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(x).Equals(".vcxproj", StringComparison.OrdinalIgnoreCase))) types.Add("C/C++ (CMake or Visual C++)");
            if (files.Any(x => Path.GetExtension(x).Equals(".pro", StringComparison.OrdinalIgnoreCase))) types.Add("Qt qmake (possible)");
            if (files.Any(x => Path.GetFileName(x).Equals("package.json", StringComparison.OrdinalIgnoreCase))) types.Add("Node.js");
            return types.Count == 0 ? new[] { "unknown or not yet initialized" } : types;
        }

        private static bool IsDotNetProjectFile(string file)
        {
            string name = Path.GetFileName(file);
            string extension = Path.GetExtension(name);
            return extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class WorkspaceScanResult
        {
            public WorkspaceScanResult(WorkspaceTreeNode rootNode) => RootNode = rootNode;
            public WorkspaceTreeNode RootNode { get; }
            public int FilesInspected { get; set; }
            public int DirectoriesInspected { get; set; }
            public int DirectoriesDiscovered { get; set; }
            public int ProjectFileCount { get; set; }
            public bool ReachedFileLimit { get; set; }
            public bool ReachedDirectoryLimit { get; set; }
            public Dictionary<string, int> ExtensionCounts { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public List<string> ProjectFiles { get; } = new List<string>();
        }
    }

    public sealed class EnvironmentCapabilityReport
    {
        public EnvironmentCapabilityReport(string text, WorkspaceTreeNode? workspaceRoot)
        {
            Text = text;
            WorkspaceRoot = workspaceRoot;
        }

        public string Text { get; }
        public WorkspaceTreeNode? WorkspaceRoot { get; }
    }

    public sealed class WorkspaceTreeNode
    {
        public WorkspaceTreeNode(string name, bool isDirectory)
        {
            Name = name;
            IsDirectory = isDirectory;
        }

        public string Name { get; set; }
        public bool IsDirectory { get; }
        public List<WorkspaceTreeNode> Children { get; } = new List<WorkspaceTreeNode>();
    }
}
