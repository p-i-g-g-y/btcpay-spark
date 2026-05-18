using System.Data;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Spark.Data;

/// <summary>
/// First-install schema bootstrap.
///
/// <para>
/// EF Core migrations have been intentionally removed for this plugin — we're still
/// pre-public-release and reshape the schema freely between builds. Migrations add tooling
/// overhead (designer file, snapshot, version-history table, manual <c>add-migration</c> dance)
/// that buys nothing while only one site is running the code. Instead this initializer:
/// </para>
///
/// <list type="number">
///   <item>Checks if the <c>BTCPayServer.Plugins.Spark</c> schema exists.</item>
///   <item>If yes — no-op. The plugin assumes the schema matches the current model. Schema
///         shape changes during dev are handled via <see cref="Controllers.SparkController.WipePluginData"/>
///         (server-admin Danger zone) which drops the schema for a clean recreate on next boot.</item>
///   <item>If no — generates the CREATE script from the live EF model
///         (<c>Database.GenerateCreateScript()</c>) and runs it once. This is identical to what
///         an EF migration would emit, just constructed at runtime from the model snapshot.</item>
/// </list>
///
/// <para>
/// When the plugin reaches a public release we'll re-add a real EF migration pipeline so installed
/// users get incremental schema upgrades on each plugin update. Until then: install fresh, or wipe
/// and reinstall to pick up shape changes.
/// </para>
/// </summary>
public class SparkPluginSchemaInitializer(
    ILogger<SparkPluginSchemaInitializer> logger,
    SparkPluginDbContextFactory dbContextFactory) : IStartupTask
{
    public async Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var conn = ctx.Database.GetDbConnection();
        var weOpened = conn.State == ConnectionState.Closed;
        if (weOpened) await conn.OpenAsync(cancellationToken);
        try
        {
            if (await SchemaExistsAsync(conn, cancellationToken))
            {
                logger.LogDebug(
                    "Spark plugin schema '{Schema}' already exists; skipping bootstrap.",
                    SparkPluginDbContext.SchemaName);
                return;
            }

            logger.LogInformation(
                "Bootstrapping Spark plugin schema '{Schema}' from the EF model (first install).",
                SparkPluginDbContext.SchemaName);

            var script = ctx.Database.GenerateCreateScript();
            await ctx.Database.ExecuteSqlRawAsync(script, cancellationToken);
            logger.LogInformation("Spark plugin schema created.");
        }
        finally
        {
            if (weOpened) await conn.CloseAsync();
        }
    }

    private static async Task<bool> SchemaExistsAsync(System.Data.Common.DbConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.schemata WHERE schema_name = @s LIMIT 1";
        var p = cmd.CreateParameter();
        p.ParameterName = "@s";
        p.Value = SparkPluginDbContext.SchemaName;
        cmd.Parameters.Add(p);
        return (await cmd.ExecuteScalarAsync(ct)) is not null;
    }
}
