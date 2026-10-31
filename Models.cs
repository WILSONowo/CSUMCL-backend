using System.Text.Json.Serialization;

namespace PclCmu.Admin;

/// <summary>
/// A single server instance (a downloadable modpack) in the resource manifest.
/// Field names are serialized as snake_case per the resource-site protocol.
/// </summary>
public sealed class Instance
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }

    [JsonPropertyName("sort")] public int? Sort { get; set; }

    [JsonPropertyName("revision")] public int Revision { get; set; }

    [JsonPropertyName("required")] public bool Required { get; set; }

    [JsonPropertyName("minecraft")] public MinecraftInfo? Minecraft { get; set; }

    [JsonPropertyName("pack")] public PackInfo? Pack { get; set; }

    [JsonPropertyName("server")] public ServerInfo? Server { get; set; }

    /// <summary>
    /// Merges Minecraft metadata parsed from a pack's dependency section into this
    /// instance and returns true if anything changed. Semantics:
    ///   - a null <paramref name="parsed"/> (no dependencies object) is a no-op;
    ///   - an empty parsed game version keeps the existing game version;
    ///   - an empty parsed loader means vanilla (clears loader + version);
    ///   - a non-empty parsed loader replaces both loader and loader version.
    /// </summary>
    public bool ApplyMinecraft(MinecraftInfo? parsed)
    {
        if (parsed is null)
            return false;

        Minecraft ??= new MinecraftInfo();

        var beforeGame = Minecraft.GameVersion;
        var beforeLoader = Minecraft.Loader;
        var beforeLoaderVersion = Minecraft.LoaderVersion;

        if (!string.IsNullOrEmpty(parsed.GameVersion))
            Minecraft.GameVersion = parsed.GameVersion;

        if (!string.IsNullOrEmpty(parsed.Loader))
        {
            Minecraft.Loader = parsed.Loader;
            Minecraft.LoaderVersion = parsed.LoaderVersion;
        }
        else
        {
            Minecraft.Loader = "";
            Minecraft.LoaderVersion = "";
        }

        return Minecraft.GameVersion != beforeGame
               || Minecraft.Loader != beforeLoader
               || Minecraft.LoaderVersion != beforeLoaderVersion;
    }
}

public sealed class MinecraftInfo
{
    [JsonPropertyName("game_version")] public string GameVersion { get; set; } = "";

    [JsonPropertyName("loader")] public string Loader { get; set; } = "";

    [JsonPropertyName("loader_version")] public string LoaderVersion { get; set; } = "";
}

public sealed class PackInfo
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "mrpack";

    [JsonPropertyName("url")] public string Url { get; set; } = "";

    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";

    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }

    [JsonPropertyName("sha512")] public string? Sha512 { get; set; }

    [JsonPropertyName("size")] public long Size { get; set; }

    /// <summary>URL of the per-revision incremental file index, or null for old revisions.</summary>
    [JsonPropertyName("files_url")] public string? FilesUrl { get; set; }

    /// <summary>Lowercase hex SHA-256 of the file index bytes; null when no index exists.</summary>
    [JsonPropertyName("files_sha256")] public string? FilesSha256 { get; set; }
}

public sealed class ServerInfo
{
    [JsonPropertyName("address")] public string Address { get; set; } = "";

    [JsonPropertyName("port")] public int Port { get; set; }
}

public sealed class Manifest
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("instances")] public List<Instance> Instances { get; set; } = [];
}
