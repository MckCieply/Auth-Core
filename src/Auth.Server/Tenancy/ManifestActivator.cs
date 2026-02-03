using System.Text;
using Auth.Infrastructure.Persistence;
using Auth.Server.Email;
using Microsoft.EntityFrameworkCore;

namespace Auth.Server.Tenancy;

/// <summary>
/// Decides which manifest is active (spec 0005 → Manifest): the product's file if it is valid, and then it is stored in
/// the database; otherwise the last valid manifest stored, and with none ever stored the built-in one. A broken file
/// never stops the service: the reason is logged and the health check reports <c>Degraded</c>.
/// </summary>
public sealed partial class ManifestActivator(
    ManifestSettings settings, ManifestHolder holder, IServiceScopeFactory scopes, TimeProvider clock, ILogger<ManifestActivator> logger)
{
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        var read = await ReadFileAsync(cancellationToken);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        if (read.Manifest is { } manifest)
        {
            await StoreAsync(db, manifest, cancellationToken);
            holder.Set(manifest, degradedReason: null);
            return;
        }

        var reason = read.Error ?? "the manifest is not valid";
        var stored = await db.ActiveManifests.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var fallback = stored is null ? null : Manifest.FromJson(stored.Content);
        LogNotUsed(logger, settings.Path, reason, fallback is null ? "built-in" : "last valid stored");
        holder.Set(fallback ?? Manifest.BuiltIn, reason);
    }

    private async Task<ManifestParseResult> ReadFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(settings.Path);
            if (!file.Exists)
            {
                return new ManifestParseResult(null, "the file does not exist");
            }

            if (file.Length > ManifestParser.MaxBytes)
            {
                return new ManifestParseResult(null, $"the file is larger than {ManifestParser.MaxBytes} bytes");
            }

            return ManifestParser.Parse(await File.ReadAllTextAsync(settings.Path, Encoding.UTF8, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ManifestParseResult(null, $"the file cannot be read ({exception.GetType().Name})");
        }
    }

    // One statement, so replicas starting together leave one row.
    private async Task StoreAsync(AuthDbContext db, Manifest manifest, CancellationToken cancellationToken)
    {
        var id = ActiveManifest.TheOnlyId;
        var content = manifest.ToJson();
        var now = StorableTime.Now(clock);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO "ActiveManifests" ("Id", "Content", "StoredAt")
            VALUES ({id}, {content}, {now})
            ON CONFLICT ("Id") DO UPDATE SET "Content" = EXCLUDED."Content", "StoredAt" = EXCLUDED."StoredAt"
            """,
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The manifest {Path} was not used: {Reason}. The {Fallback} manifest stays active.")]
    private static partial void LogNotUsed(ILogger logger, string path, string reason, string fallback);
}
