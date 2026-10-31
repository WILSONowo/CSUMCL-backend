using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Primitives;

namespace PclCmu.Admin;

/// <summary>
/// Hand-rolled router + HTML rendering for the loopback-only admin listener.
/// No frontend build: pages are server-rendered HTML with inline CSS.
/// </summary>
public static class AdminRouter
{
    private const string InstancesPrefix = "/admin/instances/";
    private const string UploadSuffix = "/upload";

    private static readonly Regex InstanceIdRegex = new("^[a-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static async Task HandleAsync(
        HttpContext context,
        InstanceStore store,
        AuthService auth,
        string? passwordHash,
        long uploadLimitBytes,
        string wwwrootDir)
    {
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;

        // ---- Login (no auth required) -------------------------------------
        if (path == "/admin/login")
        {
            if (method == "GET")
            {
                await GetLoginAsync(context, auth, passwordHash, null);
                return;
            }

            if (method == "POST")
            {
                await PostLoginAsync(context, auth, passwordHash);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Logout --------------------------------------------------------
        if (path == "/admin/logout")
        {
            if (method == "POST")
            {
                await PostLogoutAsync(context, auth);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Everything below requires a valid session --------------------
        if (!auth.IsAuthenticated(context))
        {
            Redirect(context, "/admin/login");
            return;
        }

        // ---- Dashboard -----------------------------------------------------
        if (path == "/admin" || path == "/admin/")
        {
            if (method == "GET")
            {
                await GetDashboardAsync(context, store, auth);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Manifest preview ---------------------------------------------
        if (path == "/admin/manifest")
        {
            if (method == "GET")
            {
                await GetManifestPreviewAsync(context, store);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- New instance --------------------------------------------------
        if (path == "/admin/instances/new")
        {
            if (method == "GET")
            {
                await GetInstanceFormAsync(context, auth, null, null, null);
                return;
            }

            if (method == "POST")
            {
                await PostNewInstanceAsync(context, auth, store);
                return;
            }

            await MethodNotAllowedAsync(context);
            return;
        }

        // ---- Instance upload / edit ---------------------------------------
        if (path.StartsWith(InstancesPrefix, StringComparison.Ordinal))
        {
            var rest = path.Substring(InstancesPrefix.Length);

            if (rest.EndsWith(UploadSuffix, StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(rest.Substring(0, rest.Length - UploadSuffix.Length));
                if (id.Length > 0)
                {
                    if (method == "GET")
                    {
                        await GetUploadFormAsync(context, auth, store, id);
                        return;
                    }

                    if (method == "POST")
                    {
                        await PostUploadAsync(context, auth, store, id, uploadLimitBytes, wwwrootDir);
                        return;
                    }

                    await MethodNotAllowedAsync(context);
                    return;
                }
            }
            else if (!rest.Contains('/') && rest.Length > 0)
            {
                var id = Uri.UnescapeDataString(rest);
                if (method == "GET")
                {
                    await GetEditFormAsync(context, auth, store, id);
                    return;
                }

                if (method == "POST")
                {
                    await PostEditAsync(context, auth, store, id);
                    return;
                }

                await MethodNotAllowedAsync(context);
                return;
            }
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    // ------------------------------------------------------------------ Login

    private static async Task GetLoginAsync(HttpContext context, AuthService auth, string? passwordHash, string? error)
    {
        if (auth.IsAuthenticated(context))
        {
            Redirect(context, "/admin");
            return;
        }

        var csrf = auth.EnsureCsrfCookie(context);
        var notice = string.IsNullOrWhiteSpace(passwordHash)
            ? "<p class=\"err\">Admin password is not configured. Set the PCL_CMU_ADMIN_PASSWORD_HASH environment variable.</p>"
            : "";
        var err = error is null ? "" : $"<p class=\"err\">{H(error)}</p>";

        var content = $$"""
{{notice}}
{{err}}
<form method="post" action="/admin/login">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  <label>Password <input type="password" name="password" autofocus required></label>
  <div class="actions"><button type="submit">Log in</button></div>
</form>
""";

        await WriteHtmlAsync(context, Layout("Admin login", content));
    }

    private static async Task PostLoginAsync(HttpContext context, AuthService auth, string? passwordHash)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var password = form["password"].ToString();

        if (!string.IsNullOrWhiteSpace(passwordHash) && PasswordHasher.Verify(password, passwordHash))
        {
            auth.StartSession(context);
            Redirect(context, "/admin");
            return;
        }

        await GetLoginAsync(
            context,
            auth,
            passwordHash,
            string.IsNullOrWhiteSpace(passwordHash)
                ? "Admin password is not configured."
                : "Invalid password.");
    }

    private static async Task PostLogoutAsync(HttpContext context, AuthService auth)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        auth.SignOut(context);
        Redirect(context, "/admin/login");
    }

    // -------------------------------------------------------------- Dashboard

    private static async Task GetDashboardAsync(HttpContext context, InstanceStore store, AuthService auth)
    {
        var csrf = auth.EnsureCsrfCookie(context);
        var rows = new StringBuilder();

        foreach (var i in store.List())
        {
            var idEnc = Uri.EscapeDataString(i.Id);
            var size = i.Pack is null ? "—" : i.Pack.Size.ToString();
            var server = i.Server is null
                ? "—"
                : $"{H(i.Server.Address)}:{i.Server.Port}";

            rows.Append("<tr>");
            rows.Append($"<td><a href=\"/admin/instances/{idEnc}\">{H(i.Id)}</a></td>");
            rows.Append($"<td>{H(i.Name)}</td>");
            rows.Append($"<td>{i.Revision}</td>");
            rows.Append($"<td>{size}</td>");
            rows.Append($"<td>{(i.Required ? "yes" : "no")}</td>");
            rows.Append($"<td>{server}</td>");
            rows.Append($"<td><a href=\"/admin/instances/{idEnc}/upload\">upload</a> · <a href=\"/admin/instances/{idEnc}\">edit</a></td>");
            rows.Append("</tr>");
        }

        var content = $$"""
<p>
  <a href="/admin/instances/new">New instance</a> ·
  <a href="/admin/manifest">View manifest</a>
  <form method="post" action="/admin/logout" style="display:inline; margin-left:1rem;">
    <input type="hidden" name="_csrf" value="{{H(csrf)}}">
    <button type="submit">Log out</button>
  </form>
</p>
<table>
<thead><tr><th>ID</th><th>Name</th><th>Revision</th><th>Size</th><th>Required</th><th>Server</th><th>Actions</th></tr></thead>
<tbody>
{{rows}}
</tbody>
</table>
""";

        await WriteHtmlAsync(context, Layout("Dashboard", content));
    }

    // ------------------------------------------------------- Manifest preview

    private static async Task GetManifestPreviewAsync(HttpContext context, InstanceStore store)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(store.GetManifestJson());
    }

    // ----------------------------------------------------------- New instance

    private static async Task GetInstanceFormAsync(
        HttpContext context,
        AuthService auth,
        Instance? instance,
        string? error,
        Dictionary<string, string>? submitted)
    {
        var csrf = auth.EnsureCsrfCookie(context);
        var isEdit = instance is not null;

        var values = instance is not null
            ? new Dictionary<string, string>
            {
                ["id"] = instance.Id,
                ["name"] = instance.Name,
                ["description"] = instance.Description ?? "",
                ["icon_url"] = instance.IconUrl ?? "",
                ["sort"] = instance.Sort?.ToString() ?? "",
                ["required"] = instance.Required ? "true" : "",
                ["game_version"] = instance.Minecraft?.GameVersion ?? "",
                ["loader"] = instance.Minecraft?.Loader ?? "",
                ["loader_version"] = instance.Minecraft?.LoaderVersion ?? "",
                ["server_address"] = instance.Server?.Address ?? "",
                ["server_port"] = instance.Server?.Port.ToString() ?? "25565",
            }
            : submitted ?? new Dictionary<string, string>();

        var err = error is null ? "" : $"<p class=\"err\">{H(error)}</p>";
        var action = isEdit
            ? $"/admin/instances/{Uri.EscapeDataString(instance!.Id)}"
            : "/admin/instances/new";

        var idField = isEdit
            ? $"<input type=\"hidden\" name=\"id\" value=\"{H(values["id"])}\"><p>ID: <strong>{H(values["id"])}</strong></p>"
            : $"<label>ID <input name=\"id\" required pattern=\"[a-z0-9_-]{{1,64}}\" maxlength=\"64\" value=\"{H(Get(values, "id"))}\" placeholder=\"my-instance\"></label>";

        var content = $$"""
{{err}}
<form method="post" action="{{action}}">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  {{idField}}
  <label>Name <input name="name" required value="{{H(Get(values, "name"))}}"></label>
  <label>Description <textarea name="description" rows="3">{{H(Get(values, "description"))}}</textarea></label>
  <label>Icon URL <input name="icon_url" type="url" value="{{H(Get(values, "icon_url"))}}"></label>
  <div class="row">
    <label>Sort <input name="sort" type="number" value="{{H(Get(values, "sort"))}}"></label>
    <label class="row"><input name="required" type="checkbox" value="true" {{(Get(values, "required") == "true" ? "checked" : "")}}> Required</label>
  </div>
  <fieldset>
    <legend>Minecraft</legend>
    <label>Game version <input name="game_version" value="{{H(Get(values, "game_version"))}}"></label>
    <label>Loader <input name="loader" value="{{H(Get(values, "loader"))}}"></label>
    <label>Loader version <input name="loader_version" value="{{H(Get(values, "loader_version"))}}"></label>
  </fieldset>
  <fieldset>
    <legend>Server</legend>
    <label>Address <input name="server_address" value="{{H(Get(values, "server_address"))}}"></label>
    <label>Port <input name="server_port" type="number" value="{{H(Get(values, "server_port"))}}"></label>
  </fieldset>
  <div class="actions">
    <button type="submit">{{(isEdit ? "Save" : "Create")}}</button>
    <a href="/admin">Cancel</a>
  </div>
</form>
""";

        await WriteHtmlAsync(context, Layout(isEdit ? "Edit instance" : "New instance", content));
    }

    private static async Task PostNewInstanceAsync(HttpContext context, AuthService auth, InstanceStore store)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var id = form["id"].ToString().Trim();
        var name = form["name"].ToString().Trim();

        if (!InstanceIdRegex.IsMatch(id))
        {
            await GetInstanceFormAsync(context, auth, null, "Invalid ID: use 1-64 chars of a-z, 0-9, '-' or '_'.", ToDict(form));
            return;
        }

        if (store.Exists(id))
        {
            await GetInstanceFormAsync(context, auth, null, $"An instance with ID \"{id}\" already exists.", ToDict(form));
            return;
        }

        if (name.Length == 0)
        {
            await GetInstanceFormAsync(context, auth, null, "Name is required.", ToDict(form));
            return;
        }

        var instance = new Instance
        {
            Id = id,
            Name = name,
            Description = NullIfEmpty(form["description"]),
            IconUrl = NullIfEmpty(form["icon_url"]),
            Sort = ParseNullableInt(form["sort"]),
            Revision = 0,
            Required = IsChecked(form["required"]),
            Minecraft = new MinecraftInfo
            {
                GameVersion = form["game_version"].ToString().Trim(),
                Loader = form["loader"].ToString().Trim(),
                LoaderVersion = form["loader_version"].ToString().Trim(),
            },
            Pack = null,
            Server = new ServerInfo
            {
                Address = form["server_address"].ToString().Trim(),
                Port = ParseInt(form["server_port"], 25565),
            },
        };

        store.Create(instance);
        Redirect(context, "/admin");
    }

    // ------------------------------------------------------------- Edit

    private static async Task GetEditFormAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await GetInstanceFormAsync(context, auth, instance, null, null);
    }

    private static async Task PostEditAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var existing = store.Get(id);
        if (existing is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var name = form["name"].ToString().Trim();
        if (name.Length == 0)
        {
            await GetInstanceFormAsync(context, auth, existing, "Name is required.", ToDict(form));
            return;
        }

        var updated = new Instance
        {
            Id = existing.Id,
            Name = name,
            Description = NullIfEmpty(form["description"]),
            IconUrl = NullIfEmpty(form["icon_url"]),
            Sort = ParseNullableInt(form["sort"]),
            Revision = existing.Revision, // preserved by UpdateMetadata too
            Required = IsChecked(form["required"]),
            Minecraft = new MinecraftInfo
            {
                GameVersion = form["game_version"].ToString().Trim(),
                Loader = form["loader"].ToString().Trim(),
                LoaderVersion = form["loader_version"].ToString().Trim(),
            },
            Pack = existing.Pack,
            Server = new ServerInfo
            {
                Address = form["server_address"].ToString().Trim(),
                Port = ParseInt(form["server_port"], 25565),
            },
        };

        store.UpdateMetadata(updated);
        Redirect(context, "/admin");
    }

    // ------------------------------------------------------------- Upload

    private static async Task GetUploadFormAsync(HttpContext context, AuthService auth, InstanceStore store, string id)
    {
        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var csrf = auth.EnsureCsrfCookie(context);
        var idEnc = Uri.EscapeDataString(id);
        var nextRevision = instance.Revision + 1;

        var content = $$"""
<p>Upload a Modrinth <code>.mrpack</code> for <strong>{{H(id)}}</strong>.
The ZIP must contain a <code>modrinth.index.json</code> entry.
Revision will become <strong>{{nextRevision}}</strong>.</p>
<form id="upload-form" data-upload-form method="post" enctype="multipart/form-data" action="/admin/instances/{{idEnc}}/upload">
  <input type="hidden" name="_csrf" value="{{H(csrf)}}">
  <label>.mrpack file <input type="file" name="file" id="upload-file" accept=".mrpack,application/zip" required></label>
  <div id="upload-progress-panel" hidden>
    <progress id="upload-progress" max="100" value="0"></progress>
    <div class="upload-stats">
      <span id="upload-percent">0%</span>
      <span id="upload-bytes">0 MB / 0 MB</span>
      <span id="upload-speed"></span>
    </div>
    <p id="upload-stage" class="upload-stage">正在上传…</p>
  </div>
  <p id="upload-error" class="err" hidden></p>
  <div class="actions">
    <button type="submit" id="upload-submit">Upload</button>
    <a href="/admin">Cancel</a>
  </div>
</form>
{{UploadScript}}
""";

        await WriteHtmlAsync(context, Layout("Upload pack", content));
    }

    private static async Task PostUploadAsync(
        HttpContext context,
        AuthService auth,
        InstanceStore store,
        string id,
        long uploadLimitBytes,
        string wwwrootDir)
    {
        var form = await context.Request.ReadFormAsync();
        if (!auth.ValidateCsrf(context, form[AuthService.CsrfFieldName]))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var instance = store.Get(id);
        if (instance is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0)
        {
            await WriteHtmlAsync(
                context,
                Layout("Upload failed", "<p class=\"err\">No file was uploaded.</p><p><a href=\"/admin\">Back</a></p>"));
            return;
        }

        if (file.Length > uploadLimitBytes)
        {
            await WriteHtmlAsync(
                context,
                Layout("Upload failed", "<p class=\"err\">File exceeds the upload limit.</p><p><a href=\"/admin\">Back</a></p>"),
                StatusCodes.Status413PayloadTooLarge);
            return;
        }

        var packDir = Path.Combine(wwwrootDir, "packs", id);
        Directory.CreateDirectory(packDir);
        var tmp = Path.Combine(packDir, $".upload-{Guid.NewGuid():N}.tmp");

        try
        {
            // Stream to temp while computing SHA-1 + SHA-256 + SHA-512 + size
            // in one pass, enforcing the limit.
            long size = 0;
            string sha1;
            string sha256;
            string sha512;
            using (var incSha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            using (var incSha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var incSha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512))
            using (var src = file.OpenReadStream())
            using (var dst = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    size += read;
                    if (size > uploadLimitBytes)
                        throw new UploadTooLargeException();

                    incSha1.AppendData(buffer, 0, read);
                    incSha256.AppendData(buffer, 0, read);
                    incSha512.AppendData(buffer, 0, read);
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                }

                sha1 = Convert.ToHexString(incSha1.GetHashAndReset()).ToLowerInvariant();
                sha256 = Convert.ToHexString(incSha256.GetHashAndReset()).ToLowerInvariant();
                sha512 = Convert.ToHexString(incSha512.GetHashAndReset()).ToLowerInvariant();
            }

            // Validate it is a ZIP containing modrinth.index.json.
            if (!ContainsModrinthIndex(tmp))
            {
                File.Delete(tmp);
                await WriteHtmlAsync(
                    context,
                    Layout("Upload failed", "<p class=\"err\">Invalid .mrpack: the ZIP must contain a modrinth.index.json entry.</p><p><a href=\"/admin\">Back</a></p>"));
                return;
            }

            // Build the incremental file index from the uploaded pack.
            var predictedRevision = instance.Revision + 1;
            byte[] indexBytes;
            string filesSha256;
            MinecraftInfo? minecraft = null;
            try
            {
                var build = FileIndexBuilder.Build(tmp, predictedRevision, sha256);
                indexBytes = FileIndexBuilder.Serialize(build.Index);
                filesSha256 = FileIndexBuilder.ComputeSha256(indexBytes);
                minecraft = build.Minecraft;

                // Surface skipped files (unsafe path / no HTTPS URL) so the
                // operator can see why the index is incomplete. The client falls
                // back to a full-pack download when a required file is missing.
                foreach (var skipped in build.SkippedFiles)
                    Console.Error.WriteLine($"[upload] {id}: skipping {skipped}");

                if (build.Index.Files.Count == 0)
                {
                    File.Delete(tmp);
                    await WriteHtmlAsync(
                        context,
                        Layout("Upload failed", "<p class=\"err\">Cannot publish an incremental index: no modrinth index files have both a safe path and an HTTPS download URL. Upload aborted.</p><p><a href=\"/admin\">Back</a></p>"));
                    return;
                }
            }
            catch (InvalidDataException ex)
            {
                File.Delete(tmp);
                await WriteHtmlAsync(
                    context,
                    Layout("Upload failed", $"<p class=\"err\">Invalid .mrpack: {H(ex.Message)}</p><p><a href=\"/admin\">Back</a></p>"));
                return;
            }

            // Place the index + pack first, then commit the revision atomically so
            // the signed manifest never points at a missing artifact. On any
            // failure below we remove the freshly written files.
            var indexFile = Path.Combine(packDir, $"{predictedRevision}.files.json");
            var finalPack = Path.Combine(packDir, $"{predictedRevision}.mrpack");
            FileIndexBuilder.WriteAtomic(indexFile, indexBytes);
            File.Move(tmp, finalPack, overwrite: true);

            int revision;
            try
            {
                revision = store.RecordUpload(id, sha1, sha256, sha512, size, filesSha256, predictedRevision, minecraft);
            }
            catch (InvalidOperationException ex)
            {
                File.Delete(indexFile);
                File.Delete(finalPack);
                await WriteHtmlAsync(
                    context,
                    Layout("Upload failed", $"<p class=\"err\">{H(ex.Message)}</p><p><a href=\"/admin\">Back</a></p>"));
                return;
            }

            if (revision < 0)
            {
                File.Delete(indexFile);
                File.Delete(finalPack);
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Redirect(context, "/admin");
        }
        catch (UploadTooLargeException)
        {
            File.Delete(tmp);
            await WriteHtmlAsync(
                context,
                Layout("Upload failed", "<p class=\"err\">File exceeds the upload limit.</p><p><a href=\"/admin\">Back</a></p>"),
                StatusCodes.Status413PayloadTooLarge);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }

    // Vanilla, dependency-free progress UI for the upload form. Inline so it
    // needs no external JS/CSS and no CSP exclusions. The plain <form> fallback
    // still works when JS is disabled.
    private const string UploadScript =
        """
        <script>
        (function () {
          "use strict";
          var form = document.getElementById("upload-form");
          if (!form) { return; }

          var progressPanel = document.getElementById("upload-progress-panel");
          var progress = document.getElementById("upload-progress");
          var percentEl = document.getElementById("upload-percent");
          var bytesEl = document.getElementById("upload-bytes");
          var speedEl = document.getElementById("upload-speed");
          var stageEl = document.getElementById("upload-stage");
          var errorEl = document.getElementById("upload-error");
          var submitBtn = document.getElementById("upload-submit");
          var fileInput = document.getElementById("upload-file");

          var MB = 1024 * 1024;
          var REDIRECT_DELAY_MS = 800;
          // Loopback uploads are fast, but server-side hashing of a very large
          // pack can take a while. This cap only cuts off truly stuck requests.
          var UPLOAD_TIMEOUT_MS = 30 * 60 * 1000;

          function fmtMB(bytes) {
            return (bytes / MB).toFixed(2) + " MB";
          }

          function setProgress(loaded, total) {
            var percent = total > 0 ? Math.round((loaded / total) * 100) : 0;
            if (percent > 100) { percent = 100; }
            progress.value = percent;
            percentEl.textContent = percent + "%";
            bytesEl.textContent = fmtMB(loaded) + " / " + fmtMB(total);
          }

          function setSpeed(bytesPerSec) {
            speedEl.textContent = bytesPerSec > 0 ? fmtMB(bytesPerSec) + "/s" : "";
          }

          function setStage(text, done) {
            stageEl.textContent = text;
            if (done) {
              stageEl.classList.add("done");
            } else {
              stageEl.classList.remove("done");
            }
          }

          function showError(text) {
            errorEl.textContent = text;
            errorEl.hidden = false;
          }

          function setControlsDisabled(disabled) {
            submitBtn.disabled = disabled;
            fileInput.disabled = disabled;
          }

          function fail(text) {
            setStage("上传失败。", false);
            showError(text);
            setControlsDisabled(false);
          }

          // The server renders error pages (title "Upload failed", <p class="err">)
          // with HTTP 200. Detect them so we do not falsely report success.
          function isServerErrorPage(text) {
            return text.indexOf("class=\"err\"") !== -1 || text.indexOf("Upload failed") !== -1;
          }

          form.addEventListener("submit", function (ev) {
            ev.preventDefault();

            if (!fileInput.files || fileInput.files.length === 0) {
              showError("请选择一个 .mrpack 文件。");
              return;
            }

            var file = fileInput.files[0];
            // Snapshot the fields before disabling controls: disabled inputs are
            // excluded from FormData, which would drop the file.
            var formData = new FormData(form);

            errorEl.hidden = true;
            errorEl.textContent = "";
            progressPanel.hidden = false;
            setControlsDisabled(true);
            setStage("正在上传…", false);
            setSpeed(0);
            setProgress(0, file.size);

            var totalBytes = file.size;
            var lastLoaded = 0;
            var lastTime = Date.now();

            var xhr = new XMLHttpRequest();
            xhr.open("POST", form.getAttribute("action"), true);
            xhr.timeout = UPLOAD_TIMEOUT_MS;

            xhr.upload.onprogress = function (e) {
              if (!e.lengthComputable) { return; }
              var now = Date.now();
              var dt = (now - lastTime) / 1000;
              if (dt > 0) { setSpeed((e.loaded - lastLoaded) / dt); }
              lastLoaded = e.loaded;
              lastTime = now;
              setProgress(e.loaded, e.total);
            };

            xhr.upload.onload = function () {
              setStage("上传完成，服务器正在校验并生成索引…", false);
              setSpeed(0);
              setProgress(totalBytes, totalBytes);
            };

            xhr.onload = function () {
              var text = xhr.responseText || "";
              if (xhr.status >= 200 && xhr.status < 300) {
                if (isServerErrorPage(text)) {
                  fail("上传失败：服务器拒绝了该文件。");
                  return;
                }
                setStage("上传完成。", true);
                setProgress(totalBytes, totalBytes);
                setSpeed(0);
                setTimeout(function () { window.location.href = "/admin"; }, REDIRECT_DELAY_MS);
                return;
              }
              fail("上传失败：服务器返回 HTTP " + xhr.status + "。");
            };

            xhr.onerror = function () {
              fail("上传失败：网络错误，请重试。");
            };

            xhr.ontimeout = function () {
              fail("上传失败：请求超时，请重试。");
            };

            xhr.send(formData);
          });
        })();
        </script>
        """;

    private static bool ContainsModrinthIndex(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(e => string.Equals(e.FullName, "modrinth.index.json", StringComparison.OrdinalIgnoreCase));
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    // -------------------------------------------------------------- Helpers

    private static string Layout(string title, string content)
    {
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{H(title)}} — PCL-CMU Admin</title>
<style>
  body { font-family: system-ui, -apple-system, sans-serif; margin: 2rem auto; max-width: 920px; padding: 0 1rem; color: #222; }
  h1 { font-size: 1.4rem; }
  table { border-collapse: collapse; width: 100%; margin-top: 0.5rem; }
  th, td { border: 1px solid #ccc; padding: 0.4rem 0.6rem; text-align: left; font-size: 0.9rem; }
  th { background: #f3f3f3; }
  form { display: grid; gap: 0.5rem; max-width: 620px; }
  label { display: grid; gap: 0.2rem; font-size: 0.9rem; }
  input, textarea { padding: 0.35rem; font-size: 0.9rem; font-family: inherit; }
  fieldset { display: grid; gap: 0.5rem; border: 1px solid #ddd; padding: 0.75rem; }
  .row { display: flex; gap: 0.75rem; align-items: center; }
  .row label { display: inline-flex; gap: 0.3rem; align-items: center; }
  a { color: #1a5fb4; }
  .err { color: #b00; font-weight: 600; }
  .actions { margin-top: 0.5rem; display: flex; gap: 0.75rem; align-items: center; }
  code { background: #f3f3f3; padding: 0 0.2rem; }
  [hidden] { display: none !important; }
  #upload-progress-panel { display: grid; gap: 0.4rem; margin-top: 0.5rem; }
  #upload-progress { width: 100%; max-width: 620px; height: 1rem; border: 0; appearance: none; -webkit-appearance: none; }
  #upload-progress::-webkit-progress-bar { background: #eee; border-radius: 999px; }
  #upload-progress::-webkit-progress-value { background: #1a5fb4; border-radius: 999px; }
  #upload-progress::-moz-progress-bar { background: #1a5fb4; border-radius: 999px; }
  .upload-stats { display: flex; flex-wrap: wrap; gap: 0.75rem; font-size: 0.85rem; color: #444; }
  .upload-stage { margin: 0; font-size: 0.9rem; color: #333; }
  .upload-stage.done { color: #177245; font-weight: 600; }
</style>
</head>
<body>
<h1>{{H(title)}}</h1>
{{content}}
</body>
</html>
""";
    }

    private static async Task WriteHtmlAsync(HttpContext context, string html, int statusCode = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        await context.Response.WriteAsync(html);
    }

    private static async Task MethodNotAllowedAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        await context.Response.WriteAsync("Method not allowed.");
    }

    private static void Redirect(HttpContext context, string location)
        => context.Response.Redirect(location, permanent: false);

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Get(Dictionary<string, string> d, string key)
        => d.TryGetValue(key, out var v) ? v : "";

    private static string? NullIfEmpty(StringValues v)
    {
        var s = v.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private static int? ParseNullableInt(StringValues v)
    {
        var s = v.ToString().Trim();
        return int.TryParse(s, out var n) ? n : null;
    }

    private static int ParseInt(StringValues v, int fallback)
        => int.TryParse(v.ToString().Trim(), out var n) ? n : fallback;

    private static bool IsChecked(StringValues v)
        => string.Equals(v.ToString(), "true", StringComparison.Ordinal);

    private static Dictionary<string, string> ToDict(IFormCollection form)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in form.Keys)
            d[key] = form[key].ToString();
        return d;
    }

    private sealed class UploadTooLargeException : Exception;
}
