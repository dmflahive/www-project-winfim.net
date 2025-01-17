using Serilog;
using System;
using System.Data.SQLite;
using System.IO;

namespace WinFIM.NET_Service
{
    internal sealed class SQLiteHelper 
    {
        internal string ConnectionString { get; }
        private string DbFilePath { get; }
        private const int CURRENT_DATABASE_VERSION = 3;
        private const string CURRENT_DATABASE_VERSION_NOTES =
            "capitalised table names," +
            "renamed field fileowner to owner," +
            "renamed field filetype to pathtype," +
            "added field pathexists to tables BASELINE_PATH and CURRENT_PATH," +
            "added table VERSION_CONTROL," +
            "removed table monlist," +
            "renamed table baseline_table to BASELINE_PATH," +
            "renamed table current_table  to CURRENT_PATH";

        internal SQLiteHelper()
        {
            DbFilePath = LogHelper.WorkDir + "\\fimdb.db";
            ConnectionString = @"URI=file:" + DbFilePath + ";PRAGMA journal_mode=WAL;";
        }

        internal void EnsureDatabaseExists()
        // Create the database if it doesn't exist or is the wrong version
        {
            if (File.Exists(DbFilePath))
            {
                Log.Debug($"SQLite database file {DbFilePath} exists");
                var checkedDatabaseVersion = CheckDatabaseVersion();
                if (checkedDatabaseVersion != CURRENT_DATABASE_VERSION)
                {
                    var dbFileName = Path.GetFileNameWithoutExtension(DbFilePath);
                    var dbFileExt = Path.GetExtension(DbFilePath);
                    var dbDirName = Path.GetDirectoryName(DbFilePath);
                    var currentFileFriendlyDateTime = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    var backupDbFileName = $"{dbFileName}-old-version-v{checkedDatabaseVersion}-{currentFileFriendlyDateTime}{dbFileExt}";
                    var backupDbPath = $"{dbDirName}\\{backupDbFileName}";
                    Log.Information($"SQLite database {DbFilePath} is version {checkedDatabaseVersion}. Required version {CURRENT_DATABASE_VERSION}. Renaming to {backupDbPath}");
                    if (DbFilePath != null) File.Move(DbFilePath, backupDbPath);
                    EnsureTablesExist();
                }
            }
            if (!File.Exists(DbFilePath))
            {
                Log.Information($"Creating SQLite database file {DbFilePath}");
                SQLiteConnection.CreateFile(DbFilePath);
                EnsureTablesExist();
            }
        }

        private int CheckDatabaseVersion()
        {
            Log.Debug("Checking database version");
            var checkedDatabaseVersion = 0;
            try
            {
                const string sql = "SELECT version FROM VERSION_CONTROL order by version desc limit 1";
                var output = ExecuteScalar(sql, false) ?? 0;
                checkedDatabaseVersion = Convert.ToInt32(output); // try convert to integer, or output 0
                Log.Debug($"Database version for {DbFilePath}: {checkedDatabaseVersion}");
            }
            catch
            {
                Log.Debug($"Database version for {DbFilePath} not found. Interpreting as version {checkedDatabaseVersion}");
            }
            return checkedDatabaseVersion;
        }

        private void EnsureTablesExist()
        // Ensure that all required tables exist
        {
            Log.Debug("Creating SQlite table BASELINE_PATH if it doesn't exist...");
            var sql = @"
                CREATE TABLE IF NOT EXISTS BASELINE_PATH (
                    pathname    TEXT PRIMARY KEY,
                    pathexists  BOOLEAN  CHECK (pathexists IN (0, 1)) NOT NULL,
                    filesize    INT,
                    owner       TEXT NOT NULL,
                    checktime   TEXT NOT NULL,
                    filehash    TEXT,
                    pathtype    TEXT NOT NULL
                );";
            ExecuteNonQuery(sql);

            Log.Debug("Creating SQlite table CURRENT_PATH if it doesn't exist...");
            sql = @"
                CREATE TABLE IF NOT EXISTS CURRENT_PATH (
                    pathname    TEXT PRIMARY KEY,
                    pathexists  BOOLEAN  CHECK (pathexists IN (0, 1)) NOT NULL,
                    filesize    INT,
                    owner       TEXT NOT NULL,
                    checktime   TEXT NOT NULL,
                    filehash    TEXT,
                    pathtype    TEXT NOT NULL
                );";
            ExecuteNonQuery(sql);

            Log.Debug("Creating SQlite table CONF_FILE_CHECKSUM if it doesn't exist...");
            sql = @"
                CREATE TABLE IF NOT EXISTS CONF_FILE_CHECKSUM (
                    pathname    TEXT PRIMARY KEY,
                    filehash    TEXT
                );";
            ExecuteNonQuery(sql);

            Log.Debug("Creating SQlite table VERSION_CONTROL if it doesn't exist...");
            sql = @"
                CREATE TABLE IF NOT EXISTS VERSION_CONTROL (
                    version     INT PRIMARY KEY,
                    notes       TEXT NOT NULL
                );";
            ExecuteNonQuery(sql);

            Log.Debug("Setting database version...");
            sql = $@"
                INSERT OR REPLACE INTO VERSION_CONTROL (version, notes) 
                VALUES ({CURRENT_DATABASE_VERSION}, '{CURRENT_DATABASE_VERSION_NOTES}');
            ";
            ExecuteNonQuery(sql);
        }

        internal void ExecuteNonQuery(string sql)
        {
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();
                    using (var command = new SQLiteCommand(connection))
                    {
                        Log.Verbose($"Running ExecuteNonQuery {sql}");
                        command.CommandText = sql;
                        command.CommandType = System.Data.CommandType.Text;
                        command.ExecuteNonQuery();
                    } 
                    connection.Close();
                }
            }
            catch (Exception e)
            {
                var errorMessage = $"Error running ExecuteNonQuery {sql}";
                Log.Error(e, errorMessage);
                throw;
            }

        }

        // A query that returns the first value in the first row as an object
        internal object ExecuteScalar(string sql, bool isLogError = true)
        {
            object output;
            try
            {
                using (var connection = new SQLiteConnection(ConnectionString))
                {
                    connection.Open();
                    using (var command = new SQLiteCommand(connection))
                    {
                        Log.Verbose($"Running ExecuteScalar {sql}");
                        command.CommandText = sql;
                        output = command.ExecuteScalar();
                    }
                }
            }
            catch (Exception e)
            {
                if (!isLogError)
                {
                    return null;
                }
                var errorMessage = $"Error running query {sql}";
                Log.Error(e, errorMessage);
                throw;
            }

            return output;
        }
    }
}