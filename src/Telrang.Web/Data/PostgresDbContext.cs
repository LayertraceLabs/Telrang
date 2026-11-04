using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Telrang.Web.Data;

/// <summary>
/// <see cref="ApplicationDbContext"/> on PostgreSQL (DESIGN.md §28). The same model; it exists as its own type so that
/// EF finds the PostgreSQL migration set (<c>Data/PostgresMigrations</c>) for it and the SQLite set
/// (<c>Data/Migrations</c>) for the base type. Two provider adjustments, neither visible to the app:
/// <list type="bullet">
/// <item>Strings are <c>text</c>: SQLite never enforced declared lengths, the app's validators are the limits, and a
/// value SQLite accepted (also one copied by <c>copy-to-postgres</c>) must fit here too. The model keeps its max lengths.</item>
/// <item>DateTimeOffset values are written in UTC: <c>timestamptz</c> takes offset 0 only; the instant is unchanged.</item>
/// </list>
/// </summary>
public class PostgresDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
{
    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> Utc = new(v => v.ToUniversalTime(), v => v);

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(string)) property.SetColumnType("text");
                else if ((property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                         && property.GetValueConverter() is null)
                    property.SetValueConverter(Utc);
            }
        }
    }

    /// <summary>
    /// The model without a server: design time (Program registers it while <c>EF.IsDesignTime</c>, for <c>dotnet ef …
    /// --context PostgresDbContext</c>) and tests. The connection string is a placeholder: this app migrates at startup.
    /// Not an <c>IDesignTimeDbContextFactory</c>: that interface is covariant, so EF would use it for
    /// <see cref="ApplicationDbContext"/> (the SQLite set) too.
    /// </summary>
    public static PostgresDbContext ForModel() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseAppIdentityModel().UseNpgsql("Host=localhost;Database=telrang_design").Options);
}
