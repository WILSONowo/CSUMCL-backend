using System.Text;
using System.Text.Json;

namespace PclCmu.Admin;

/// <summary>
/// Single-writer, file-backed store for instances. The in-memory list is the
/// source of truth; every mutation serializes <c>data/instances.json</c> and
/// regenerates <c>wwwroot/manifest.json</c> atomically (write-to-temp + rename)
/// under a lock.
/// </summary>
public sealed class InstanceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
    };

    private readonly string _dataDir;
    private readonly string _wwwrootDir;
    private readonly string _publicBaseUrl;
    private readonly ManifestSigner _signer;
    private readonly object _gate = new();
    private List<Instance> _instances;

    /// <summary>
    /// Creates the store and regenerates the signed manifest. Throws a clear
    /// startup error if the signing key is missing or invalid (fail closed).
    /// </summary>
    public InstanceStore(string dataDir, string wwwrootDir, string publicBaseUrl, string manifestSigningKeyPath)
    {
        _dataDir = dataDir;
        _wwwrootDir = wwwrootDir;
        _publicBaseUrl = publicBaseUrl.TrimEnd('/');
        _signer = new ManifestSigner(manifestSigningKeyPath);

        _instances = LoadInstances(Path.Combine(_dataDir, "instances.json"));

        // Correct stale Minecraft metadata from each published pack's dependency
        // section. Only rewrites instances.json when something actually changed;
        // missing/corrupt packs are skipped and logged, never fatal.
        var backfilled = BackfillMinecraftMetadata();

        // Ensure the public manifest always matches the on-disk data and the
        // current PublicBaseUrl (also produces the seed manifest on first run).
        lock (_gate)
        {
            if (backfilled)
                SaveLocked();
            else
                RegenerateManifestLocked();
        }
    }

    /// <summary>Ordered snapshot of all instances (sort ascending, then id).</summary>
    public List<Instance> List()
    {
        lock (_gate)
        {
            return _instances
                .Select(Clone)
                .OrderBy(i => i.Sort ?? 0)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }
    }

    public Instance? Get(string id)
    {
        lock (_gate)
        {
            var found = Find(id);
            return found is null ? null : Clone(found);
        }
    }

    public bool Exists(string id)
    {
        lock (_gate) return Find(id) is not null;
    }

    public void Create(Instance instance)
    {
        lock (_gate)
        {
            _instances.Add(instance);
            SaveLocked();
        }
    }

    /// <summary>Replaces metadata for an instance while preserving its pack and revision.</summary>
    public bool UpdateMetadata(Instance instance)
    {
        lock (_gate)
        {
            var index = IndexOf(instance.Id);
            if (index < 0)
                return false;

            var existing = _instances[index];
            instance.Revision = existing.Revision;
            instance.Pack = existing.Pack;
            _instances[index] = instance;
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Records a successful upload: bumps the revision by one and stores the
    /// pack hashes/size plus the incremental file index URL/hash. Returns the new
    /// revision, or -1 if the instance is gone.
    /// </summary>
    /// <param name="expectedRevision">
    /// The revision the caller predicted (previous + 1). Used to detect a
    /// concurrent upload and fail closed instead of publishing a manifest whose
    /// files_sha256 was computed for a different revision.
    /// </param>
    public int RecordUpload(
        string id,
        string sha1,
        string sha256,
        string sha512,
        long size,
        string filesSha256,
        int expectedRevision,
        MinecraftInfo? minecraft = null)
    {
        lock (_gate)
        {
            var index = IndexOf(id);
            if (index < 0)
                return -1;

            var existing = _instances[index];
            var revision = existing.Revision + 1;
            if (revision != expectedRevision)
                throw new InvalidOperationException(
                    $"Upload raced: instance \"{id}\" revision is now {revision}, expected {expectedRevision}.");

            existing.Revision = revision;
            existing.Pack = new PackInfo
            {
                Kind = "mrpack",
                Url = PackUrl(id, revision),
                Sha1 = sha1,
                Sha256 = sha256,
                Sha512 = sha512,
                Size = size,
                FilesUrl = FilesUrl(id, revision),
                FilesSha256 = filesSha256,
            };
            // Correct Minecraft metadata from the pack's dependency section in the
            // same atomic write as the revision/pack bump.
            existing.ApplyMinecraft(minecraft);
            _instances[index] = existing;

            SaveLocked();
            return revision;
        }
    }

    public string GetManifestJson()
    {
        lock (_gate)
        {
            return BuildManifestJsonLocked();
        }
    }

    private Instance? Find(string id)
        => _instances.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    private int IndexOf(string id)
        => _instances.FindIndex(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    private string PackUrl(string id, int revision)
        => $"{_publicBaseUrl}/packs/{id}/{revision}.mrpack";

    private string FilesUrl(string id, int revision)
        => $"{_publicBaseUrl}/packs/{id}/{revision}.files.json";

    /// <summary>
    /// Backfills stale <see cref="Instance.Minecraft"/> metadata for every
    /// published instance by reading the dependency section of the on-disk pack
    /// at <c>wwwroot/packs/&lt;id&gt;/&lt;revision&gt;.mrpack</c>. Returns true if
    /// anything changed. Missing or corrupt packs are skipped and logged.
    /// </summary>
    private bool BackfillMinecraftMetadata()
    {
        var changed = false;

        foreach (var instance in _instances)
        {
            if (instance.Revision <= 0 || instance.Pack is null)
                continue;

            var packPath = Path.Combine(_wwwrootDir, "packs", instance.Id, $"{instance.Revision}.mrpack");
            if (!File.Exists(packPath))
                continue;

            try
            {
                if (!FileIndexBuilder.TryReadMinecraftMetadata(packPath, out var parsed) || parsed is null)
                    continue;

                if (instance.ApplyMinecraft(parsed))
                {
                    changed = true;
                    Console.Error.WriteLine(
                        $"[startup] backfilled minecraft metadata for \"{instance.Id}\" from {packPath}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[startup] skipping minecraft backfill for \"{instance.Id}\" ({packPath}): {ex.Message}");
            }
        }

        return changed;
    }

    private void SaveLocked()
    {
        // Keep the stored pack.url (and files_url) in sync with PublicBaseUrl so
        // instances.json is always self-consistent.
        foreach (var i in _instances)
        {
            if (i.Pack is not null && i.Revision > 0)
            {
                i.Pack.Url = PackUrl(i.Id, i.Revision);
                if (i.Pack.FilesSha256 is not null)
                    i.Pack.FilesUrl = FilesUrl(i.Id, i.Revision);
            }
        }

        Directory.CreateDirectory(_dataDir);
        AtomicWriteBytes(Path.Combine(_dataDir, "instances.json"), SerializeUtf8(_instances));

        RegenerateManifestLocked();
    }

    private Manifest BuildManifestLocked()
    {
        var manifest = new Manifest { FormatVersion = 1 };
        foreach (var i in _instances
                     .Where(i => i.Revision > 0 && i.Pack is not null)
                     .OrderBy(i => i.Sort ?? 0)
                     .ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            var clone = Clone(i);
            clone.Pack!.Kind = "mrpack";
            clone.Pack.Url = PackUrl(i.Id, i.Revision);
            if (clone.Pack.FilesSha256 is not null)
                clone.Pack.FilesUrl = FilesUrl(i.Id, i.Revision);
            manifest.Instances.Add(clone);
        }

        return manifest;
    }

    private string BuildManifestJsonLocked()
        => JsonSerializer.Serialize(BuildManifestLocked(), JsonOptions);

    private void RegenerateManifestLocked()
    {
        Directory.CreateDirectory(_wwwrootDir);

        // Build the exact bytes once, write manifest.json, then sign those same
        // bytes and write the detached signature. Both writes are atomic.
        var bytes = SerializeUtf8(BuildManifestLocked());
        AtomicWriteBytes(Path.Combine(_wwwrootDir, "manifest.json"), bytes);

        var sigJson = _signer.Sign(bytes);
        AtomicWriteBytes(Path.Combine(_wwwrootDir, "manifest.json.sig"), Encoding.UTF8.GetBytes(sigJson));
    }

    private static byte[] SerializeUtf8<T>(T value)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));

    private static void AtomicWriteBytes(string path, byte[] bytes)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    private static List<Instance> LoadInstances(string path)
    {
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Instance>>(File.ReadAllText(path), JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            // Corrupt data file: start empty rather than crash the server; the
            // admin can recreate instances. The corrupt file is left in place
            // for inspection.
            return [];
        }
    }

    private static Instance Clone(Instance i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Description = i.Description,
        IconUrl = i.IconUrl,
        Sort = i.Sort,
        Revision = i.Revision,
        Required = i.Required,
        Minecraft = i.Minecraft is null
            ? null
            : new MinecraftInfo
            {
                GameVersion = i.Minecraft.GameVersion,
                Loader = i.Minecraft.Loader,
                LoaderVersion = i.Minecraft.LoaderVersion,
            },
        Pack = i.Pack is null
            ? null
            : new PackInfo
            {
                Kind = i.Pack.Kind,
                Url = i.Pack.Url,
                Sha1 = i.Pack.Sha1,
                Sha256 = i.Pack.Sha256,
                Sha512 = i.Pack.Sha512,
                Size = i.Pack.Size,
                FilesUrl = i.Pack.FilesUrl,
                FilesSha256 = i.Pack.FilesSha256,
            },
        Server = i.Server is null
            ? null
            : new ServerInfo { Address = i.Server.Address, Port = i.Server.Port },
    };
}
