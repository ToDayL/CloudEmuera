using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CloudEmuera.Infrastructure.Persistence;

/// <summary>
/// SQLite has deferred transactions by default.  Identity transitions need a
/// single writer from their first read through their conditional update, so use
/// BEGIN IMMEDIATE explicitly instead of relying on a read-then-write EF unit
/// of work.
/// </summary>
public sealed class SqliteImmediateTransaction : IAsyncDisposable
{
    private readonly CloudEmueraDbContext _db;
    private readonly IDbContextTransaction _transaction;
    private bool _completed;

    private SqliteImmediateTransaction(CloudEmueraDbContext db, IDbContextTransaction transaction)
    {
        _db = db;
        _transaction = transaction;
    }

    public static async Task<SqliteImmediateTransaction> BeginAsync(CloudEmueraDbContext db, CancellationToken cancellationToken = default)
    {
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnection connection = (SqliteConnection)db.Database.GetDbConnection();
            // Microsoft.Data.Sqlite's deferred=false is BEGIN IMMEDIATE.  Register
            // the transaction with EF so SaveChanges does not start a nested one.
            SqliteTransaction sqliteTransaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
            IDbContextTransaction transaction = db.Database.UseTransaction(sqliteTransaction)
                ?? throw new InvalidOperationException("EF Core did not register the SQLite immediate transaction.");
            return new SqliteImmediateTransaction(db, transaction);
        }
        catch
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_completed)
            {
                try { await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
