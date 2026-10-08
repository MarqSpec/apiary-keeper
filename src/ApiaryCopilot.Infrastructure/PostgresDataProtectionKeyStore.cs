using Npgsql;

namespace ApiaryCopilot.Infrastructure;

/// <summary>
/// <see cref="IDataProtectionKeyStore"/> over the <c>DataProtectionKeys</c> table, which the
/// <c>AddDataProtectionKeys</c> migration creates. Plain parameterized SQL on its own connection rather than the
/// tenant <see cref="ApiaryCopilotDbContext"/>, so the key ring is never reachable through the account-filtered model.
/// </summary>
internal sealed class PostgresDataProtectionKeyStore(string? connectionString) : IDataProtectionKeyStore
{
    public const string TableName = "DataProtectionKeys";

    public IReadOnlyList<string> GetAll()
    {
        using var connection = Open();
        using var command = new NpgsqlCommand($"SELECT \"Xml\" FROM \"{TableName}\" ORDER BY \"Id\"", connection);
        using var reader = command.ExecuteReader();
        var elements = new List<string>();
        while (reader.Read())
        {
            elements.Add(reader.GetString(0));
        }

        return elements;
    }

    public void Add(string? friendlyName, string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        using var connection = Open();
        using var command = new NpgsqlCommand($"INSERT INTO \"{TableName}\" (\"FriendlyName\", \"Xml\") VALUES ($1, $2)", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)friendlyName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = xml });
        command.ExecuteNonQuery();
    }

    private NpgsqlConnection Open()
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"No database connection string: the Data Protection key ring is kept in PostgreSQL; set ConnectionStrings__{InfrastructureServiceCollectionExtensions.ConnectionStringName} (DEPLOYMENT.md §3).");
        }

        var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        return connection;
    }
}
