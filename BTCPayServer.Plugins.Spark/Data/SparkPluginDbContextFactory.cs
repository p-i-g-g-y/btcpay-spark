using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.Spark.Data;

public class SparkPluginDbContextFactory(IOptions<DatabaseOptions> options)
    : BaseDbContextFactory<SparkPluginDbContext>(options, SparkPluginDbContext.SchemaName)
{
    public override SparkPluginDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var builder = new DbContextOptionsBuilder<SparkPluginDbContext>();
        ConfigureBuilder(builder);
        return new SparkPluginDbContext(builder.Options);
    }
}
