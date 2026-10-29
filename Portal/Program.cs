var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "5071";
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("PORTAL_URL") ?? $"http://0.0.0.0:{port}");
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 12 * 1024 * 1024);
var app = builder.Build();
var imageDir = Environment.GetEnvironmentVariable("IMAGE_STORAGE_PATH") ?? Path.Combine(app.Environment.ContentRootPath, "App_Data", "images");
Directory.CreateDirectory(imageDir);
var uploadToken = Environment.GetEnvironmentVariable("PORTAL_UPLOAD_TOKEN");
var snapshotRequestedTime = DateTimeOffset.MinValue;
var snapshotLock = new object();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/snapshot-request", (HttpRequest request) => {
    if (string.IsNullOrWhiteSpace(uploadToken) || !string.Equals(request.Headers.Authorization.ToString(), $"Bearer {uploadToken}", StringComparison.Ordinal)) return Results.Unauthorized();
    lock (snapshotLock) {
        snapshotRequestedTime = DateTimeOffset.UtcNow;
    }
    return Results.Ok(new { requested = true, timestamp = snapshotRequestedTime });
});

app.MapGet("/api/snapshot-request", (HttpRequest request) => {
    if (string.IsNullOrWhiteSpace(uploadToken) || !string.Equals(request.Headers.Authorization.ToString(), $"Bearer {uploadToken}", StringComparison.Ordinal)) return Results.Unauthorized();
    bool requested;
    lock (snapshotLock) {
        requested = (DateTimeOffset.UtcNow - snapshotRequestedTime).TotalSeconds < 25;
        if (requested) {
            snapshotRequestedTime = DateTimeOffset.MinValue;
        }
    }
    return Results.Ok(new { requested });
});

app.MapPost("/api/images", async (HttpRequest request) => {
    if (string.IsNullOrWhiteSpace(uploadToken)) return Results.Problem("Set PORTAL_UPLOAD_TOKEN before starting the portal.", statusCode: 503);
    if (!string.Equals(request.Headers.Authorization.ToString(), $"Bearer {uploadToken}", StringComparison.Ordinal)) return Results.Unauthorized();
    if (!request.HasFormContentType) return Results.BadRequest("Expected multipart form data.");
    var file = request.Form.Files.GetFile("image");
    if (file is null || file.Length == 0 || file.Length > 10 * 1024 * 1024) return Results.BadRequest("Choose an image up to 10 MB.");
    if (file.ContentType is not ("image/jpeg" or "image/png")) return Results.BadRequest("Only JPEG and PNG images are accepted.");
    var id = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}_{Guid.NewGuid():N}";
    var ext = file.ContentType == "image/png" ? ".png" : ".jpg";
    await using (var stream = File.Create(Path.Combine(imageDir, id + ext))) await file.CopyToAsync(stream);
    return Results.Ok(new { id, receivedAt = DateTimeOffset.UtcNow, contentType = file.ContentType });
});

app.MapGet("/api/images", (HttpRequest request) => {
    if (string.IsNullOrWhiteSpace(uploadToken) || !string.Equals(request.Headers.Authorization.ToString(), $"Bearer {uploadToken}", StringComparison.Ordinal)) return Results.Unauthorized();
    var files = Directory.EnumerateFiles(imageDir).OrderByDescending(File.GetLastWriteTimeUtc).Take(200)
        .Select(path => new { id = Path.GetFileNameWithoutExtension(path), url = "/api/images/" + Path.GetFileName(path), receivedAt = File.GetCreationTimeUtc(path) });
    return Results.Ok(files);
});

app.MapGet("/api/images/{fileName}", (string fileName, HttpRequest request) => {
    if (string.IsNullOrWhiteSpace(uploadToken) || !string.Equals(request.Headers.Authorization.ToString(), $"Bearer {uploadToken}", StringComparison.Ordinal)) return Results.Unauthorized();
    if (Path.GetFileName(fileName) != fileName) return Results.BadRequest();
    var path = Path.Combine(imageDir, fileName);
    if (!System.IO.File.Exists(path)) return Results.NotFound();
    var type = Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    return Results.File(path, type);
});

app.Run();
