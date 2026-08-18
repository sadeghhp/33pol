using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Pol33.Persistence.Infrastructure;

/// <summary>
/// Runs a read-check-write body atomically with respect to other writers.
/// </summary>
/// <remarks>
/// On SQLite the body runs inside one <c>BEGIN IMMEDIATE</c> transaction, which takes the write lock
/// up front so a second writer blocks (subject to <c>busy_timeout</c>) before it can read a stale
/// starting value. This is started against the raw <see cref="SqliteConnection"/> with
/// <c>deferred: false</c>, deliberately: EF's <c>BeginTransactionAsync(IsolationLevel.Serializable)</c>
/// leaves the transaction <em>deferred</em>, so the read takes only a shared lock and the write has to
/// upgrade it, which under WAL fails with <c>SQLITE_BUSY_SNAPSHOT</c> rather than waiting — and
/// <c>busy_timeout</c> does not retry that, because the snapshot is genuinely stale.
///
/// <para>The connection is opened through <see cref="DatabaseFacade.OpenConnectionAsync"/> so the
/// <see cref="SqliteConnectionInterceptor"/> pragmas (foreign keys, busy timeout, synchronous) are
/// applied exactly as for any EF-opened connection.</para>
///
/// <para>Other relational providers get a Serializable EF transaction, which maps to real
/// serializable isolation there. The EF InMemory provider (unit tests) supports neither transactions
/// nor row locking, so the body simply runs directly.</para>
///
/// <para>An exception thrown by the body rolls the transaction back and propagates unchanged.</para>
/// </remarks>
internal static class GatewayWriteTransaction
{
    public static async Task RunAsync(
        GatewayDbContext dbContext,
        Func<CancellationToken, Task> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(body);

        if (!dbContext.Database.IsRelational())
        {
            await body(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (dbContext.Database.GetDbConnection() is not SqliteConnection sqliteConnection)
        {
            await using var providerTransaction = await dbContext.Database
                .BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);

            await body(cancellationToken).ConfigureAwait(false);
            await providerTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Through EF (not sqliteConnection.OpenAsync) so the connection interceptor fires.
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var immediateTransaction = sqliteConnection.BeginTransaction(
                System.Data.IsolationLevel.Serializable,
                deferred: false);

            await using (immediateTransaction.ConfigureAwait(false))
            {
                await dbContext.Database.UseTransactionAsync(immediateTransaction, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    await body(cancellationToken).ConfigureAwait(false);
                    await immediateTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await dbContext.Database.UseTransactionAsync(null, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
