using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PclCmu.Admin;

// ---------------------------------------------------------------------------
// Self-contained test harness for the incremental .mrpack file-index feature.
// No external test framework: assertions accumulate failures and exit non-zero.
// ---------------------------------------------------------------------------

var failures = 0;
var checks = 0;

void Check(bool condition, string what)
{
    checks++;
    if (condition)
    {
        Console.WriteLine($"  ok: {what}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL: {what}");
    }
}

void Section(string name) => Console.WriteLine($"\n== {name} ==");

// ---------------------------------------------------------------- temp dirs

var tempRoot = Path.Combine(Path.GetTempPath(), $"pclcmu-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(tempRoot);
try
{
    TestPathSafety();
    TestFileIndexBuild();
    TestOverridesGroundTruth();
    TestInstanceStoreManifest();
    TestDependencyMapping();
    TestUploadMetadata();
    TestStartupBackfill();

    Console.WriteLine($"\n{checks - failures}/{checks} checks passed, {failures} failed.");
}
finally
{
    try { Directory.Delete(tempRoot, recursive: true); } catch { /* best effort */ }
}

return failures == 0 ? 0 : 1;

// ------------------------------------------------------------- path safety

void TestPathSafety()
{
    Section("path safety (IsSafeRelativePath)");
    Check(FileIndexBuilder.IsSafeRelativePath("mods/foo.jar"), "mods/foo.jar is safe");
    Check(FileIndexBuilder.IsSafeRelativePath("config/a/b.json"), "config/a/b.json is safe");
    Check(!FileIndexBuilder.IsSafeRelativePath("../evil.jar"), "../evil.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("a/../../evil.jar"), "a/../../evil.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("/abs.jar"), "absolute /abs.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("\\abs.jar"), "absolute \\abs.jar rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("C:\\Windows\\evil.jar"), "drive-letter C:\\ rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("mods/./foo.jar"), "dot segment rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("mods//foo.jar"), "empty segment rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath(""), "empty path rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath(null), "null path rejected");
    Check(!FileIndexBuilder.IsSafeRelativePath("   "), "whitespace path rejected");
}

// ------------------------------------------------------------ build helpers

string WriteSyntheticPack(string dir, string indexJson, IEnumerable<(string Name, byte[] Bytes)> overrides)
{
    var path = Path.Combine(dir, "pack.mrpack");
    using (var fs = File.Create(path))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        var index = zip.CreateEntry("modrinth.index.json");
        using (var s = index.Open())
            s.Write(Encoding.UTF8.GetBytes(indexJson));

        foreach (var (name, bytes) in overrides)
        {
            var e = zip.CreateEntry(name);
            using var s = e.Open();
            s.Write(bytes);
        }
    }
    return path;
}

string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

// ----------------------------------------------------------- file index build

void TestFileIndexBuild()
{
    Section("FileIndexBuilder.Build (shape, filtering, hashes)");

    var indexJson = """
    {
      "formatVersion": 1,
      "game": "minecraft",
      "versionId": "1.0.0",
      "name": "Test Pack",
      "files": [
        { "path": "mods/foo.jar", "downloads": ["https://cdn.example.com/mods/foo.jar", "http://insecure.example.com/foo.jar"], "hashes": { "sha1": "ABCDEF0123456789abcdef0123456789abcdef01", "sha512": "00AA11BB22CC33DD44EE55FF6677889900AA11BB22CC33DD44EE55FF6677889900AA11BB22CC33DD44EE55FF66778899" }, "fileSize": 1234 },
        { "path": "config/bar.json", "downloads": ["http://insecure.example.com/bar.json"], "hashes": { "sha1": "11", "sha512": "22" }, "fileSize": 56 },
        { "path": "../evil.jar", "downloads": ["https://cdn.example.com/evil.jar"], "hashes": { "sha1": "33", "sha512": "44" }, "fileSize": 78 },
        { "path": "/abs.jar", "downloads": ["https://cdn.example.com/abs.jar"], "hashes": { "sha1": "55", "sha512": "66" }, "fileSize": 90 },
        { "path": "mods/nourl.jar", "downloads": [], "hashes": { "sha1": "77", "sha512": "88" }, "fileSize": 100 },
        { "path": "resourcepacks/foo.zip", "downloads": ["https://cdn.example.com/rp/foo.zip"], "hashes": { "sha1": "11AA", "sha512": "22BB" }, "fileSize": 200 }
      ],
      "dependencies": { "minecraft": "1.20.1" }
    }
    """;

    var overrides = new List<(string, byte[])>
    {
        ("overrides/config/foo.txt", Encoding.UTF8.GetBytes("hello overrides")),
        ("overrides/mods/extra.dat", new byte[] { 1, 2, 3, 4 }),
        ("client-overrides/config/client.txt", Encoding.UTF8.GetBytes("client cfg")),
    };

    var zipPath = WriteSyntheticPack(tempRoot, indexJson, overrides);
    var packBytes = File.ReadAllBytes(zipPath);
    var packSha256 = Sha256Hex(packBytes);

    var build = FileIndexBuilder.Build(zipPath, revision: 7, packSha256: packSha256);
    var index = build.Index;

    Check(index.FormatVersion == 1, "formatVersion == 1");
    Check(index.Revision == 7, "revision == 7");
    Check(index.PackSha256 == packSha256, "packSha256 matches SHA-256 of pack bytes");
    Check(index.Files.Count == 2, $"files has 2 entries (got {index.Files.Count})");
    Check(build.SkippedFiles.Count == 4, $"4 files skipped (got {build.SkippedFiles.Count})");

    var foo = index.Files.SingleOrDefault(f => f.Path == "mods/foo.jar");
    Check(foo is not null, "mods/foo.jar present");
    if (foo is not null)
    {
        Check(foo.Downloads.Count == 1 && foo.Downloads[0] == "https://cdn.example.com/mods/foo.jar",
            "http URL filtered; only HTTPS kept");
        Check(foo.Hashes.Sha1 == "abcdef0123456789abcdef0123456789abcdef01", "sha1 normalized to lowercase");
        Check(foo.Hashes.Sha512 == "00aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff6677889900aa11bb22cc33dd44ee55ff66778899",
            "sha512 normalized to lowercase");
        Check(foo.FileSize == 1234, "fileSize == 1234");
    }

    var rp = index.Files.SingleOrDefault(f => f.Path == "resourcepacks/foo.zip");
    Check(rp is not null, "resourcepacks/foo.zip present (non-mod file included)");

    Check(!index.Files.Any(f => f.Path == "../evil.jar" || f.Path == "/abs.jar"
        || f.Path == "config/bar.json" || f.Path == "mods/nourl.jar"),
        "unsafe/no-URL files excluded");

    // Compact serialization + files_sha256 over exact bytes.
    var bytes = FileIndexBuilder.Serialize(index);
    var json = Encoding.UTF8.GetString(bytes);
    Check(!json.Contains('\n') && !json.Contains("  "), "serialized JSON is compact (no newlines)");
    var filesSha256 = FileIndexBuilder.ComputeSha256(bytes);
    Check(filesSha256 == Sha256Hex(bytes), "files_sha256 == SHA-256 of exact serialized bytes");

    // overrides hash: independent recomputation + stability.
    var expectedOverrides = ComputeOverridesIndependent(zipPath);
    Check(index.OverridesSha256 == expectedOverrides, "overridesSha256 matches independent computation");

    var build2 = FileIndexBuilder.Build(zipPath, revision: 7, packSha256: packSha256);
    Check(build2.Index.OverridesSha256 == index.OverridesSha256, "overridesSha256 stable across rebuilds");

    // Changing override content must change the hash.
    var overridesChanged = new List<(string, byte[])>
    {
        ("overrides/config/foo.txt", Encoding.UTF8.GetBytes("hello overrides CHANGED")),
        ("overrides/mods/extra.dat", new byte[] { 1, 2, 3, 4 }),
        ("client-overrides/config/client.txt", Encoding.UTF8.GetBytes("client cfg")),
    };
    var zipPath2 = WriteSyntheticPack(tempRoot, indexJson, overridesChanged);
    var build3 = FileIndexBuilder.Build(zipPath2, revision: 7, packSha256: packSha256);
    Check(build3.Index.OverridesSha256 != index.OverridesSha256, "overridesSha256 changes when override content changes");
}

string ComputeOverridesIndependent(string zipPath)
{
    using var zip = ZipFile.OpenRead(zipPath);
    var entries = zip.Entries
        .Where(e => !e.FullName.EndsWith('/'))
        .Where(e => e.FullName.StartsWith("overrides/", StringComparison.Ordinal)
                    || e.FullName.StartsWith("client-overrides/", StringComparison.Ordinal))
        .OrderBy(e => e.FullName, StringComparer.Ordinal)
        .ToList();

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    foreach (var e in entries)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(e.FullName));
        hash.AppendData(new byte[] { 0x00 });
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        hash.AppendData(ms.ToArray());
    }
    return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
}

void TestOverridesGroundTruth()
{
    Section("overridesSha256 ground truth (single entry)");

    var indexJson = """
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ], "dependencies": {} }
    """;

    var zipPath = WriteSyntheticPack(tempRoot, indexJson,
        new List<(string, byte[])> { ("overrides/a.txt", Encoding.UTF8.GetBytes("hello")) });

    var build = FileIndexBuilder.Build(zipPath, 1, "0".PadLeft(64, '0'));

    var expected = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("overrides/a.txt\u0000hello"))).ToLowerInvariant();

    Check(build.Index.OverridesSha256 == expected, "overridesSha256 == SHA256(name || NUL || bytes)");
}

// ------------------------------------------------------- instance store test

void TestInstanceStoreManifest()
{
    Section("InstanceStore manifest (files_url / files_sha256)");

    var dataDir = Path.Combine(tempRoot, "data");
    var wwwroot = Path.Combine(tempRoot, "wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);

    var keyPath = Path.Combine(tempRoot, "key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);

    store.Create(new Instance
    {
        Id = "t",
        Name = "T",
        Revision = 0,
        Required = true,
        Minecraft = new MinecraftInfo { GameVersion = "1.20.1", Loader = "fabric", LoaderVersion = "0.15.11" },
        Server = new ServerInfo { Address = "127.0.0.1", Port = 25565 },
    });

    var filesSha256 = "a".PadLeft(64, 'a');
    var revision = store.RecordUpload("t", "s1", "s256", "s512", 312, filesSha256, expectedRevision: 1);
    Check(revision == 1, "RecordUpload returns revision 1");

    Check(store.RecordUpload("missing", "s1", "s256", "s512", 1, filesSha256, expectedRevision: 1) == -1,
        "RecordUpload returns -1 for unknown instance");

    bool raced = false;
    try
    {
        store.RecordUpload("t", "s1", "s256", "s512", 312, filesSha256, expectedRevision: 99);
    }
    catch (InvalidOperationException)
    {
        raced = true;
    }
    Check(raced, "RecordUpload fails closed when revision raced");

    // Manifest JSON must include files_url + files_sha256.
    var manifest = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var pack = manifest.GetProperty("instances")[0].GetProperty("pack");
    Check(pack.GetProperty("url").GetString() == "https://cdn.example.com/packs/t/1.mrpack", "pack.url unchanged semantics");
    Check(pack.GetProperty("files_url").GetString() == "https://cdn.example.com/packs/t/1.files.json",
        "files_url == .../packs/t/1.files.json");
    Check(pack.GetProperty("files_sha256").GetString() == filesSha256, "files_sha256 present");
    Check(pack.GetProperty("kind").GetString() == "mrpack", "kind == mrpack");

    // Clone preserves the new fields.
    var clone = store.Get("t");
    Check(clone?.Pack?.FilesUrl == "https://cdn.example.com/packs/t/1.files.json", "Clone preserves FilesUrl");
    Check(clone?.Pack?.FilesSha256 == filesSha256, "Clone preserves FilesSha256");

    // A persisted instances.json now carries the new fields.
    var persisted = File.ReadAllText(Path.Combine(dataDir, "instances.json"));
    Check(persisted.Contains("\"files_url\"", StringComparison.Ordinal)
          && persisted.Contains("\"files_sha256\"", StringComparison.Ordinal),
        "instances.json persists files_url/files_sha256");

    // Old revisions (no index) still serialize with null files_url/files_sha256.
    store.Create(new Instance
    {
        Id = "old",
        Name = "Old",
        Revision = 2,
        Required = false,
        Pack = new PackInfo { Kind = "mrpack", Url = "https://cdn.example.com/packs/old/2.mrpack", Sha1 = "x", Sha256 = "y", Sha512 = "z", Size = 10 },
    });
    var manifest2 = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var oldPack = manifest2.GetProperty("instances")
        .EnumerateArray().Single(i => i.GetProperty("id").GetString() == "old").GetProperty("pack");
    Check(oldPack.TryGetProperty("files_url", out var fu) && fu.ValueKind == JsonValueKind.Null,
        "old revision files_url is null");
    Check(oldPack.TryGetProperty("files_sha256", out var fsh) && fsh.ValueKind == JsonValueKind.Null,
        "old revision files_sha256 is null");

    // Signature file is produced for the signed manifest.
    Check(File.Exists(Path.Combine(wwwroot, "manifest.json.sig")), "manifest.json.sig written");
}

// --------------------------------------------------- dependency mapping tests

string PackIndexWithDeps(string depsJson) => $$"""
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ], "dependencies": {{depsJson}} }
    """;

void TestDependencyMapping()
{
    Section("dependency -> Minecraft metadata mapping");

    // fabric-loader
    var fabric = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(fabric is not null, "fabric-loader yields Minecraft metadata");
    Check(fabric?.GameVersion == "1.26.3", "fabric game_version == 1.26.3");
    Check(fabric?.Loader == "fabric", "fabric loader == fabric");
    Check(fabric?.LoaderVersion == "0.20.1", "fabric loader_version == 0.20.1");

    // forge
    var forge = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "forge": "47.2.0" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(forge?.Loader == "forge", "forge loader == forge");

    // neoforge
    var neo = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "neoforge": "47.1.106" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(neo?.Loader == "neoforge", "neoforge loader == neoforge");

    // quilt-loader
    var quilt = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1", "quilt-loader": "0.23.2" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(quilt?.Loader == "quilt", "quilt-loader loader == quilt");

    // no loader dependency -> vanilla (empty loader)
    var vanilla = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(vanilla is not null && vanilla.Loader == "" && vanilla.LoaderVersion == "", "no loader -> vanilla (empty loader)");

    // no minecraft dependency -> empty game version (caller keeps existing)
    var noGame = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "fabric-loader": "0.20.1" }"""), []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(noGame is not null && noGame.GameVersion == "" && noGame.Loader == "fabric",
        "no minecraft -> empty game version, loader still mapped");

    // no dependencies object at all -> null metadata
    var noDepsJson = """
    { "formatVersion": 1, "game": "minecraft", "versionId": "1", "name": "T", "files": [
        { "path": "mods/m.jar", "downloads": ["https://cdn.example.com/m.jar"], "hashes": { "sha1": "aa", "sha512": "bb" }, "fileSize": 1 }
    ] }
    """;
    var noDeps = FileIndexBuilder.Build(
        WriteSyntheticPack(tempRoot, noDepsJson, []),
        revision: 1, packSha256: "0".PadLeft(64, '0')).Minecraft;
    Check(noDeps is null, "no dependencies object -> null metadata");

    // TryReadMinecraftMetadata: reads only the index (fast path) with the same result.
    var zipPath = WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []);
    Check(FileIndexBuilder.TryReadMinecraftMetadata(zipPath, out var fast) && fast is not null,
        "TryReadMinecraftMetadata succeeds for a valid pack");
    Check(fast?.GameVersion == "1.26.3" && fast?.Loader == "fabric" && fast?.LoaderVersion == "0.20.1",
        "TryReadMinecraftMetadata returns the same metadata as Build");

    // TryReadMinecraftMetadata: corrupt pack -> false (no throw).
    var corruptPath = Path.Combine(tempRoot, "corrupt.mrpack");
    File.WriteAllBytes(corruptPath, new byte[] { 1, 2, 3, 4, 5 });
    Check(!FileIndexBuilder.TryReadMinecraftMetadata(corruptPath, out var corruptMeta) && corruptMeta is null,
        "TryReadMinecraftMetadata returns false for corrupt pack");

    // TryReadMinecraftMetadata: ZIP without index -> false (no throw).
    var noIndexPath = Path.Combine(tempRoot, "noindex.mrpack");
    using (var fs = File.Create(noIndexPath))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        zip.CreateEntry("random.txt").Open().Dispose();
    Check(!FileIndexBuilder.TryReadMinecraftMetadata(noIndexPath, out var noIndexMeta) && noIndexMeta is null,
        "TryReadMinecraftMetadata returns false when index entry missing");
}

// -------------------------------------------------------- upload metadata test

void TestUploadMetadata()
{
    Section("upload metadata (RecordUpload updates Instance.Minecraft)");

    var dataDir = Path.Combine(tempRoot, "upload-data");
    var wwwroot = Path.Combine(tempRoot, "upload-wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);

    var keyPath = Path.Combine(tempRoot, "upload-key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);

    store.Create(new Instance
    {
        Id = "t",
        Name = "T",
        Revision = 0,
        Required = true,
        Minecraft = new MinecraftInfo { GameVersion = "1.20.1", Loader = "fabric", LoaderVersion = "0.15.11" },
        Server = new ServerInfo { Address = "127.0.0.1", Port = 25565 },
    });

    // Build a pack whose dependency section carries the corrected metadata.
    var packPath = WriteSyntheticPack(tempRoot, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""), []);
    var packBytes = File.ReadAllBytes(packPath);
    var build = FileIndexBuilder.Build(packPath, revision: 1, packSha256: Sha256Hex(packBytes));

    var revision = store.RecordUpload("t", "s1", Sha256Hex(packBytes), "s512", packBytes.Length,
        filesSha256: "a".PadLeft(64, 'a'), expectedRevision: 1, minecraft: build.Minecraft);
    Check(revision == 1, "RecordUpload returns revision 1");

    var updated = store.Get("t");
    Check(updated?.Minecraft?.GameVersion == "1.26.3", "upload corrected game_version to 1.26.3");
    Check(updated?.Minecraft?.Loader == "fabric", "upload kept loader fabric");
    Check(updated?.Minecraft?.LoaderVersion == "0.20.1", "upload corrected loader_version to 0.20.1");

    // The signed manifest carries the corrected minecraft fields.
    var manifest = JsonDocument.Parse(store.GetManifestJson()).RootElement;
    var mc = manifest.GetProperty("instances")[0].GetProperty("minecraft");
    Check(mc.GetProperty("game_version").GetString() == "1.26.3", "manifest game_version == 1.26.3");
    Check(mc.GetProperty("loader_version").GetString() == "0.20.1", "manifest loader_version == 0.20.1");
}

// ------------------------------------------------------- startup backfill test

void TestStartupBackfill()
{
    Section("startup backfill (InstanceStore corrects stale metadata)");

    // (a) Backfill corrects a stale instance and rewrites instances.json.
    var (dataDir, wwwroot) = SetupBackfillDir("a");
    var keyPath = Path.Combine(tempRoot, "backfill-key.pem");
    using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());

    WriteInstanceJson(dataDir, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    WritePack(wwwroot, "test", 1, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""));

    var store = new InstanceStore(dataDir, wwwroot, "https://cdn.example.com", keyPath);
    var inst = store.Get("test");
    Check(inst?.Minecraft?.GameVersion == "1.26.3", "backfill corrected game_version");
    Check(inst?.Minecraft?.LoaderVersion == "0.20.1", "backfill corrected loader_version");

    var persisted = File.ReadAllText(Path.Combine(dataDir, "instances.json"));
    Check(persisted.Contains("\"1.26.3\"", StringComparison.Ordinal) && persisted.Contains("\"0.20.1\"", StringComparison.Ordinal),
        "backfill rewrote instances.json with corrected metadata");

    // (b) Already-correct metadata -> instances.json is not rewritten.
    var (dataDir2, wwwroot2) = SetupBackfillDir("b");
    WriteInstanceJson(dataDir2, "test", revision: 1, game: "1.26.3", loader: "fabric", loaderVersion: "0.20.1");
    WritePack(wwwroot2, "test", 1, PackIndexWithDeps("""{ "minecraft": "1.26.3", "fabric-loader": "0.20.1" }"""));
    var before = File.ReadAllBytes(Path.Combine(dataDir2, "instances.json"));
    _ = new InstanceStore(dataDir2, wwwroot2, "https://cdn.example.com", keyPath);
    var after = File.ReadAllBytes(Path.Combine(dataDir2, "instances.json"));
    Check(before.SequenceEqual(after), "no-change backfill leaves instances.json untouched");

    // (c) Missing pack -> skipped, no crash, metadata unchanged.
    var (dataDir3, wwwroot3) = SetupBackfillDir("c");
    WriteInstanceJson(dataDir3, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    var store3 = new InstanceStore(dataDir3, wwwroot3, "https://cdn.example.com", keyPath);
    Check(store3.Get("test")?.Minecraft?.GameVersion == "1.20.1", "missing pack leaves metadata unchanged");

    // (d) Corrupt pack -> skipped, no crash, metadata unchanged.
    var (dataDir4, wwwroot4) = SetupBackfillDir("d");
    WriteInstanceJson(dataDir4, "test", revision: 1, game: "1.20.1", loader: "fabric", loaderVersion: "0.15.11");
    Directory.CreateDirectory(Path.Combine(wwwroot4, "packs", "test"));
    File.WriteAllBytes(Path.Combine(wwwroot4, "packs", "test", "1.mrpack"), new byte[] { 9, 9, 9 });
    var store4 = new InstanceStore(dataDir4, wwwroot4, "https://cdn.example.com", keyPath);
    Check(store4.Get("test")?.Minecraft?.GameVersion == "1.20.1", "corrupt pack leaves metadata unchanged");
}

(string DataDir, string WwwRoot) SetupBackfillDir(string tag)
{
    var dataDir = Path.Combine(tempRoot, $"bf-{tag}-data");
    var wwwroot = Path.Combine(tempRoot, $"bf-{tag}-wwwroot");
    Directory.CreateDirectory(dataDir);
    Directory.CreateDirectory(wwwroot);
    return (dataDir, wwwroot);
}

void WriteInstanceJson(string dataDir, string id, int revision, string game, string loader, string loaderVersion)
{
    var json = $$"""
    [
      {
        "id": "{{id}}",
        "name": "T",
        "revision": {{revision}},
        "required": true,
        "minecraft": { "game_version": "{{game}}", "loader": "{{loader}}", "loader_version": "{{loaderVersion}}" },
        "pack": {
          "kind": "mrpack",
          "url": "https://cdn.example.com/packs/{{id}}/{{revision}}.mrpack",
          "sha1": "s1",
          "sha256": "s256",
          "sha512": "s512",
          "size": 312
        },
        "server": { "address": "127.0.0.1", "port": 25565 }
      }
    ]
    """;
    File.WriteAllText(Path.Combine(dataDir, "instances.json"), json);
}

void WritePack(string wwwroot, string id, int revision, string indexJson)
{
    var packDir = Path.Combine(wwwroot, "packs", id);
    Directory.CreateDirectory(packDir);
    var zipPath = Path.Combine(packDir, $"{revision}.mrpack");
    using (var fs = File.Create(zipPath))
    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("modrinth.index.json");
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(indexJson));
    }
}
