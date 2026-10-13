using System;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using Apex.Editor.Services.Stock;

namespace Apex.Shots;

/// <summary>
/// stock.db's schema contract on the Apex side: an index of another schema, one that says none, or a damaged file turns
/// the stock features off with a sentence that says why (local assets never read it); a superseded search stops.
/// </summary>
public partial class Program
{
    private static void StockSchemaChecks(string temp)
    {
        string Fixture(string name, Action<SqliteConnection>? change)
        {
            var path = Path.Combine(temp, "schema-" + name, "stock.db");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BuildStockFixture(path);
            if (change is not null)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open();
                change(db);
            }
            return path;
        }
        void Exec(SqliteConnection db, string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        var good = Fixture("good", null);
        Check("stock schema: an index of schema 1 is used", new StockDb(good).SchemaProblem() is null);
        var newer = Fixture("newer", db => Exec(db, "UPDATE meta SET value = '2' WHERE key = 'schema_version'"));
        var problem = new StockDb(newer).SchemaProblem();
        Check($"stock schema: a newer schema is refused and says to update Apex ({problem})", problem is not null && problem.Contains("newer") && problem.Contains("Update Apex"));
        var none = Fixture("none", db => Exec(db, "DELETE FROM meta WHERE key = 'schema_version'"));
        Check($"stock schema: an index that names no schema is refused ({new StockDb(none).SchemaProblem()})", new StockDb(none).SchemaProblem() is { } p2 && p2.Contains("Rebuild"));
        var damaged = Path.Combine(temp, "schema-damaged", "stock.db");
        Directory.CreateDirectory(Path.GetDirectoryName(damaged)!);
        File.WriteAllText(damaged, "this is not a database, just bytes where stock.db should be");
        Check($"stock schema: a damaged file is refused, not thrown ({new StockDb(damaged).SchemaProblem()})", new StockDb(damaged).SchemaProblem() is { } p3 && p3.Contains("can't be read"));

        using (var library = new StockLibrary(Path.Combine(temp, "no-such-install"), Path.Combine(temp, "none", "stocklib.exe"), newer))
            Check($"stock schema: the library leaves a newer index alone and says why ({library.State}: {library.Problem})",
                library.Db is null && !library.CanSearch && library.State == StockState.Failed && library.Problem.Contains("newer"));
        using (var library = new StockLibrary(Path.Combine(temp, "no-such-install"), Path.Combine(temp, "none", "stocklib.exe"), good))
            Check("stock schema: ... and uses a good one", library.CanSearch && library.State == StockState.Ready);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool stopped;
        try
        {
            new StockDb(good).Search(new StockQuery(new[] { "smg" }, Array.Empty<string>(), 50, 0), cancelled.Token);
            stopped = false;
        }
        catch (OperationCanceledException)
        {
            stopped = true;
        }
        Check("stock search: a search a newer keystroke superseded stops instead of running to the end", stopped);
    }
}
