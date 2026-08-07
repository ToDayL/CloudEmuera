using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class ProtectedSqliteConnection : SqliteConnection
{
    private readonly SqliteConnectionResources _resources;

    public ProtectedSqliteConnection(string connectionString, SqliteConnectionResources resources)
        : base(connectionString)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                _resources.Dispose();
            }
        }
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _resources.Dispose();
        }
    }
}

internal sealed class SqliteConnectionResources : IDisposable
{
    private SafeFileHandle? _database;
    private SafeFileHandle? _parentDirectory;

    public SqliteConnectionResources(SafeFileHandle database, SafeFileHandle parentDirectory)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _parentDirectory = parentDirectory ?? throw new ArgumentNullException(nameof(parentDirectory));
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _database, null)?.Dispose();
        Interlocked.Exchange(ref _parentDirectory, null)?.Dispose();
    }
}
