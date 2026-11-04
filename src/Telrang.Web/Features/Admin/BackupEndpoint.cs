using Telrang.Web.Data;
using Telrang.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Telrang.Web.Features.Admin;

/// <summary>
/// <c>POST /admin/backup</c> (SQLite only; 404 on PostgreSQL): a consistent copy of the SQLite database made with <c>VACUUM INTO</c>
/// into {DataDir}/tmp (owner-only; leftovers are deleted at startup, pentest AV-12), streamed and deleted on close. Admin only, antiforgery-checked, rate limited.
/// The Data Protection keys in {DataDir}/keys are not included and must be backed up separately.
/// </summary>
public static class BackupEndpoint
{
    public static IEndpointConventionBuilder MapAdminBackup(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/admin/backup", async (
                HttpContext http,
                [FromServices] IAntiforgery antiforgery,
                [FromServices] ApplicationDbContext db,
                [FromServices] AdminService admin,
                [FromServices] TimeProvider time,
                [FromServices] Status.AppStartInfo start,
                [FromServices] DatabaseSetup database,
                CancellationToken ct) =>
            {
                if (!await antiforgery.IsRequestValidAsync(http)) return Microsoft.AspNetCore.Http.Results.BadRequest();
                if (!await admin.IsAdminAsync(http.User)) return Microsoft.AspNetCore.Http.Results.NotFound();
                // PostgreSQL is backed up with pg_dump (DESIGN.md §28): there is no download.
                if (database.IsPostgres) return Microsoft.AspNetCore.Http.Results.NotFound();

                var path = Path.Combine(DataDirectory.TempPath(start.DataDir), $"backup-{Guid.NewGuid():N}.db");
                await db.Database.ExecuteSqlAsync($"VACUUM INTO {path}", ct);
                await admin.LogBackupAsync(http.User);

                http.Response.Headers.CacheControl = "no-store";
                var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                var name = $"telrang-{time.GetUtcNow():yyyyMMdd-HHmmss}.db";
                return Microsoft.AspNetCore.Http.Results.File(stream, "application/vnd.sqlite3", name);
            })
            .RequireAuthorization(AppPolicies.Admin)
            .RequireRateLimiting(RateLimiting.BackupPolicy)
            .DisableAntiforgery(); // validated explicitly above (no form-bound parameters)
}
