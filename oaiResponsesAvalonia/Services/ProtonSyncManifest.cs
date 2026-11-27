using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace oaiResponsesAvalonia.Services;

/// <summary>Local-only baseline metadata for reconciling the mutable user-files area.</summary>
internal sealed class ProtonSyncManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public int Schema { get; set; } = 1;
    public string DeviceId { get; set; } = $"{Environment.UserName}@{Environment.MachineName}";
    public Dictionary<string, ProtonSyncManifestEntry> Files { get; set; } = new(StringComparer.Ordinal);

    public static async Task<ProtonSyncManifest> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return new ProtonSyncManifest();

        await using FileStream stream = File.OpenRead(path);
        ProtonSyncManifest? manifest = await JsonSerializer.DeserializeAsync<ProtonSyncManifest>(stream, JsonOptions);
        if (manifest is null || manifest.Schema != 1 || string.IsNullOrWhiteSpace(manifest.DeviceId) || manifest.Files is null)
            throw new InvalidDataException($"The Proton Sync manifest '{path}' has an unsupported or invalid format.");

        manifest.Files = new Dictionary<string, ProtonSyncManifestEntry>(manifest.Files, StringComparer.Ordinal);
        return manifest;
    }

    public async Task SaveAsync(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("The manifest must have a parent directory.");

        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, this, JsonOptions);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

internal sealed class ProtonSyncManifestEntry
{
    public string BaseSha1 { get; set; } = string.Empty;
    public long Size { get; set; }
    public long LocalMtimeTicks { get; set; }
}
