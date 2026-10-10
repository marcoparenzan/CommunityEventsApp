using Microsoft.Data.SqlClient;

namespace EventsCommon.Services;

public sealed class SqlConnectionFactory(IConfiguration configuration)
{
    private readonly string connectionString = configuration.GetConnectionString("Events")
        ?? throw new InvalidOperationException("ConnectionStrings:Events must be configured.");

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
