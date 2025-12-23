using Microsoft.Data.Sqlite;

namespace NetX.Core.Data;

/// <summary>
/// SQLite Database Service for NetX
/// Singleton pattern for application-wide database access
/// </summary>
public sealed class DatabaseService
{
    private static readonly Lazy<DatabaseService> _instance = new(() => new DatabaseService());
    public static DatabaseService Instance => _instance.Value;

    private readonly string _dbPath;
    private readonly string _connectionString;

    private DatabaseService()
    {
        // Store database in 'data' folder next to executable
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var dataDir = Path.Combine(appDir, "data");

        if (!Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }

        _dbPath = Path.Combine(dataDir, "netx.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    /// <summary>
    /// Initialize database and create tables if they don't exist
    /// </summary>
    public void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Create settings table
        ExecuteNonQuery(connection, @"
            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            )");

        // Create network history table
        ExecuteNonQuery(connection, @"
            CREATE TABLE IF NOT EXISTS NetworkHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                ProcessId INTEGER NOT NULL,
                BytesSent INTEGER NOT NULL,
                BytesReceived INTEGER NOT NULL,
                Timestamp TEXT DEFAULT CURRENT_TIMESTAMP
            )");

        // Create bandwidth rules table
        ExecuteNonQuery(connection, @"
            CREATE TABLE IF NOT EXISTS BandwidthRules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProcessName TEXT NOT NULL,
                ProcessPath TEXT,
                RuleType TEXT NOT NULL,
                MaxDownload INTEGER,
                MaxUpload INTEGER,
                IsEnabled INTEGER DEFAULT 1,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            )");

        // Create cleanup history table
        ExecuteNonQuery(connection, @"
            CREATE TABLE IF NOT EXISTS CleanupHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Category TEXT NOT NULL,
                FilesDeleted INTEGER NOT NULL,
                BytesFreed INTEGER NOT NULL,
                CleanedAt TEXT DEFAULT CURRENT_TIMESTAMP
            )");

        // Create indexes for better performance
        ExecuteNonQuery(connection, @"
            CREATE INDEX IF NOT EXISTS idx_network_timestamp
            ON NetworkHistory(Timestamp)");

        ExecuteNonQuery(connection, @"
            CREATE INDEX IF NOT EXISTS idx_network_process
            ON NetworkHistory(ProcessName)");

        // Insert default settings if not exist
        InsertDefaultSettings(connection);
    }

    private void InsertDefaultSettings(SqliteConnection connection)
    {
        var defaultSettings = new Dictionary<string, string>
        {
            { "Language", "auto" },
            { "Theme", "dark" },
            { "RefreshRateMs", "1000" },
            { "StartWithWindows", "false" },
            { "MinimizeToTray", "true" },
            { "ShowNotifications", "true" },
            { "BandwidthMode", "basic" },
            { "DataRetentionDays", "30" }
        };

        foreach (var setting in defaultSettings)
        {
            ExecuteNonQuery(connection, @"
                INSERT OR IGNORE INTO Settings (Key, Value)
                VALUES (@key, @value)",
                new SqliteParameter("@key", setting.Key),
                new SqliteParameter("@value", setting.Value));
        }
    }

    #region Settings Operations

    public string? GetSetting(string key)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Settings WHERE Key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        return cmd.ExecuteScalar()?.ToString();
    }

    public void SetSetting(string key, string value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, @"
            INSERT INTO Settings (Key, Value, UpdatedAt)
            VALUES (@key, @value, CURRENT_TIMESTAMP)
            ON CONFLICT(Key) DO UPDATE SET
                Value = @value,
                UpdatedAt = CURRENT_TIMESTAMP",
            new SqliteParameter("@key", key),
            new SqliteParameter("@value", value));
    }

    public Dictionary<string, string> GetAllSettings()
    {
        var settings = new Dictionary<string, string>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    #endregion

    #region Network History Operations

    public void LogNetworkUsage(string processName, int processId, long bytesSent, long bytesReceived)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, @"
            INSERT INTO NetworkHistory (ProcessName, ProcessId, BytesSent, BytesReceived)
            VALUES (@name, @pid, @sent, @received)",
            new SqliteParameter("@name", processName),
            new SqliteParameter("@pid", processId),
            new SqliteParameter("@sent", bytesSent),
            new SqliteParameter("@received", bytesReceived));
    }

    public void CleanupOldData(int retentionDays = 30)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, @"
            DELETE FROM NetworkHistory
            WHERE Timestamp < datetime('now', @days || ' days')",
            new SqliteParameter("@days", -retentionDays));
    }

    public (long sent, long received) GetTodayBandwidth()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(BytesSent), 0), COALESCE(SUM(BytesReceived), 0)
            FROM NetworkHistory
            WHERE date(Timestamp) = date('now')";

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return (reader.GetInt64(0), reader.GetInt64(1));
        }
        return (0, 0);
    }

    #endregion

    #region Bandwidth Rules Operations

    public void AddBandwidthRule(string processName, string? processPath, string ruleType,
        long? maxDownload = null, long? maxUpload = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, @"
            INSERT INTO BandwidthRules (ProcessName, ProcessPath, RuleType, MaxDownload, MaxUpload)
            VALUES (@name, @path, @type, @down, @up)",
            new SqliteParameter("@name", processName),
            new SqliteParameter("@path", (object?)processPath ?? DBNull.Value),
            new SqliteParameter("@type", ruleType),
            new SqliteParameter("@down", (object?)maxDownload ?? DBNull.Value),
            new SqliteParameter("@up", (object?)maxUpload ?? DBNull.Value));
    }

    public void DeleteBandwidthRule(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection,
            "DELETE FROM BandwidthRules WHERE Id = @id",
            new SqliteParameter("@id", id));
    }

    public void SetBandwidthRuleEnabled(int id, bool enabled)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecuteNonQuery(connection, @"
            UPDATE BandwidthRules SET IsEnabled = @enabled WHERE Id = @id",
            new SqliteParameter("@id", id),
            new SqliteParameter("@enabled", enabled ? 1 : 0));
    }

    #endregion

    #region Helper Methods

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, params SqliteParameter[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddRange(parameters);
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection GetConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    #endregion
}
