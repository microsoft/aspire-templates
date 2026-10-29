using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Azure.Storage.Blobs;
using MediaStudio.Core;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

var builder = WebApplication.CreateBuilder(args);
builder.AddMediaServices();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = 6 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 6 * 1024 * 1024);
builder.Services.AddHostedService<Dispatcher>();
var token = builder.Configuration["Studio:Token"];
if (string.IsNullOrWhiteSpace(token) || token.Length < 20)
{
    throw new InvalidOperationException("Studio:Token must contain at least 20 characters. Configure the studio-token secret parameter.");
}
var expectedToken = SHA256.HashData(Encoding.UTF8.GetBytes(token));
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MediaDb>();
    await db.Database.EnsureCreatedAsync();
}
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.Ordinal)
            ? authorization[7..] : "";
        if (!CryptographicOperations.FixedTimeEquals(expectedToken, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.Response.StatusCode = 401;
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHealthChecks("/health");
app.MapGet("/api/jobs", async (MediaDb db) =>
    await db.Jobs.AsNoTracking().OrderByDescending(job => job.Created).Take(100).ToListAsync());
app.MapGet("/api/jobs/{id:guid}", async (Guid id, MediaDb db) =>
    await db.Jobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id) is { } job
        ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/jobs", async (HttpRequest request, MediaDb db, BlobContainerClient blobs, CancellationToken ct) =>
{
    var key = request.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 8 or > 100)
        return Results.BadRequest(new { error = "Provide an Idempotency-Key between 8 and 100 characters." });
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Upload a multipart form with an image field." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("image");
    if (file is null || file.Length is < 1 or > 5 * 1024 * 1024)
        return Results.BadRequest(new { error = "Choose a PNG or JPEG up to 5 MiB." });
    using var data = new MemoryStream();
    await file.CopyToAsync(data, ct);
    data.Position = 0;
    using (var encoded = SKData.CreateCopy(data.ToArray()))
    using (var codec = SKCodec.Create(encoded))
    {
        if (codec is null || codec.Info.Width < 1 || codec.Info.Height < 1 ||
            (long)codec.Info.Width * codec.Info.Height > 16_000_000 ||
            codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg))
            return Results.BadRequest(new { error = "Only valid PNG/JPEG images up to 16 megapixels are accepted." });
    }
    var simulate = form["simulateFailure"] == "true";
    using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    fingerprint.AppendData(data.ToArray());
    fingerprint.AppendData([simulate ? (byte)1 : (byte)0]);
    var hash = Convert.ToHexString(fingerprint.GetHashAndReset());
    var existing = await db.Jobs.SingleOrDefaultAsync(job => job.RequestKey == key, ct);
    if (existing is not null)
        return existing.Hash == hash
            ? Results.Ok(existing) : Results.Conflict(new { error = "Idempotency-Key already used for a different request." });
    var job = new Job
    {
        Id = Guid.NewGuid(), Name = Path.GetFileName(file.FileName)[..Math.Min(Path.GetFileName(file.FileName).Length, 160)],
        RequestKey = key, Hash = hash, SimulateFailure = simulate,
        TraceParent = Activity.Current?.Id
    };
    data.Position = 0;
    await blobs.CreateIfNotExistsAsync(cancellationToken: ct);
    await blobs.GetBlobClient($"original/{job.Id}").UploadAsync(data, overwrite: false, cancellationToken: ct);
    db.Jobs.Add(job);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException)
    {
        // A concurrent request can win the unique key after the initial read.
        db.ChangeTracker.Clear();
        existing = await db.Jobs.SingleOrDefaultAsync(row => row.RequestKey == key, ct);
        await blobs.GetBlobClient($"original/{job.Id}").DeleteIfExistsAsync(cancellationToken: ct);
        if (existing is null) throw;
        return existing.Hash == hash
            ? Results.Ok(existing) : Results.Conflict(new { error = "Idempotency-Key already used for a different request." });
    }
    return Results.Accepted($"/api/jobs/{job.Id}", job);
});
app.MapPost("/api/jobs/{id:guid}/retry", async (Guid id, MediaDb db, CancellationToken ct) =>
{
    var changed = await db.Jobs.Where(job => job.Id == id && job.State == "Failed")
        .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.State, "Queued")
            .SetProperty(job => job.Generation, job => job.Generation + 1)
            .SetProperty(job => job.Attempts, 0).SetProperty(job => job.SimulateFailure, false)
            .SetProperty(job => job.Error, (string?)null).SetProperty(job => job.Lease, (Guid?)null)
            .SetProperty(job => job.NextDispatch, DateTimeOffset.UtcNow)
            .SetProperty(job => job.Updated, DateTimeOffset.UtcNow), ct);
    return changed == 1 ? Results.Accepted() : Results.Conflict(new { error = "Only failed jobs can be retried." });
});
app.MapGet("/api/jobs/{id:guid}/rendition", async (Guid id, MediaDb db, BlobContainerClient blobs, CancellationToken ct) =>
{
    if (!await db.Jobs.AnyAsync(job => job.Id == id && job.State == "Ready", ct))
        return Results.NotFound();
    var blob = await blobs.GetBlobClient($"rendition/{id}.jpg").DownloadStreamingAsync(cancellationToken: ct);
    return Results.Stream(blob.Value.Content, "image/jpeg");
});
app.MapFallbackToFile("index.html");
app.Run();
