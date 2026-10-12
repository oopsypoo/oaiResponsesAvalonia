using System;
using System.IO;
using System.Linq;

namespace oaiResponsesAvalonia.Services
{
    public sealed class WorkspaceGuard
    {
        private readonly DeveloperToolsOptions _options;
        private readonly string _rootFullPath;

        public WorkspaceGuard(DeveloperToolsOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _rootFullPath = Path.GetFullPath(_options.RepositoryRoot ?? string.Empty);

            if (string.IsNullOrWhiteSpace(_rootFullPath) || !Directory.Exists(_rootFullPath))
                throw new DirectoryNotFoundException($"Workspace root not found: {_rootFullPath}");
        }

        public string RootFullPath => _rootFullPath;

        public string ResolveRelativeFile(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new InvalidOperationException("Path is required.");

            if (Path.IsPathRooted(relativePath))
                throw new InvalidOperationException("Absolute paths are not allowed.");

            var fullPath = Path.GetFullPath(Path.Combine(_rootFullPath, relativePath));

            EnsureInsideRoot(fullPath);

            var ext = Path.GetExtension(fullPath);
            if (_options.AllowedExtensions?.Length > 0 &&
                !_options.AllowedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Extension not allowed: {ext}");
            }

            return fullPath;
        }

        public string ResolveRelativeDirectory(string subpath)
        {
            if (string.IsNullOrWhiteSpace(subpath))
                return _rootFullPath;

            if (Path.IsPathRooted(subpath))
                throw new InvalidOperationException("Absolute paths are not allowed.");

            var fullPath = Path.GetFullPath(Path.Combine(_rootFullPath, subpath));

            EnsureInsideRoot(fullPath);

            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Directory not found: {subpath}");

            return fullPath;
        }

        public string ToRelativePath(string fullPath)
        {
            return Path.GetRelativePath(_rootFullPath, fullPath);
        }

        private void EnsureInsideRoot(string fullPath)
        {
            var normalizedRoot = _rootFullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar);

            if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(normalizedPath, _rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Path escapes the workspace root.");
            }
        }
    }
}