using Microsoft.Data.Sqlite;

namespace Sokna.PrintAgent.Core;

internal static class SqliteConnectionExtensions
{
    extension(SqliteConnection connection)
    {
        public long LastInsertRowId
        {
            get
            {
                using var command=connection.CreateCommand();
                command.CommandText="SELECT last_insert_rowid();";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }
}
