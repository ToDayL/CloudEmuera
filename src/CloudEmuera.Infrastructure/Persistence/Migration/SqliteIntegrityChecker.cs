using Microsoft.Data.Sqlite;

namespace CloudEmuera.Infrastructure.Persistence;

internal static class SqliteIntegrityChecker
{
    public static async Task VerifyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await VerifyNoRowsAsync(connection, "PRAGMA foreign_key_check;", "foreign-key check", cancellationToken).ConfigureAwait(false);
        await VerifyQuickCheckAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyNoRowsAsync(
        SqliteConnection connection,
        string sql,
        string checkName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new SqliteIntegrityException($"SQLite {checkName} failed.");
        }
    }

    private static async Task VerifyQuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        bool sawResult = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sawResult = true;
            if (!string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqliteIntegrityException("SQLite quick check failed.");
            }
        }

        if (!sawResult)
        {
            throw new SqliteIntegrityException("SQLite quick check returned no result.");
        }
    }
}

public sealed class SqliteIntegrityException(string message) : Exception(message);
