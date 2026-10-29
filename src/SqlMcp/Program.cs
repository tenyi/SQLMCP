using System.ComponentModel;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

try
{
    var path = args.Length switch
    {
        0 => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sqlmcp", "sqlmcp.json"),
        2 when args[0] == "--config" => Path.GetFullPath(args[1]),
        _ => throw new ArgumentException("用法：sqlmcp [--config <path>]")
    };
    var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new Exception();
    settings.Validate();
    var builder = Host.CreateApplicationBuilder();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Services.AddSingleton(settings);
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<SqlTools>();
    await builder.Build().RunAsync();
    return 0;
}
catch (Exception)
{
    // 原始解析例外可能包含機密，不直接輸出。
    Console.Error.WriteLine("啟動失敗。請檢查設定檔。用法：sqlmcp [--config <path>]");
    return 1;
}

public sealed class Settings
{
    public Dictionary<string, Database> Connections { get; set; } = [];
    public void Validate()
    {
        foreach (var (name, db) in Connections)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(db.ConnectionString)
                || db.MaxRows is < 1 or > 10000 || db.TimeoutSeconds is < 1 or > 300)
                throw new ArgumentException("無效連線設定");
            foreach (var entity in db.Entities.Values)
                if (string.IsNullOrWhiteSpace(entity.Schema) || string.IsNullOrWhiteSpace(entity.Table)
                    || entity.Columns.Length == 0 || entity.Columns.Any(string.IsNullOrWhiteSpace)
                    || entity.Columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != entity.Columns.Length)
                    throw new ArgumentException("無效實體設定");
        }
    }
}
public sealed class Database
{
    public string ConnectionString { get; set; } = "";
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? PasswordEnvironmentVariable { get; set; }
    public int MaxRows { get; set; } = 100;
    public int TimeoutSeconds { get; set; } = 30;
    public Dictionary<string, Entity> Entities { get; set; } = [];
    public string BuildConnectionString()
    {
        var b = new SqlConnectionStringBuilder(ConnectionString);
        if (UserName is not null) { b.IntegratedSecurity = false; b.UserID = UserName; }
        if (PasswordEnvironmentVariable is not null)
            b.Password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable)
                ?? throw new InvalidOperationException();
        else if (Password is not null) b.Password = Password;
        return b.ConnectionString;
    }
}
public sealed class Entity
{
    public string Schema { get; set; } = "dbo";
    public string Table { get; set; } = "";
    public string[] Columns { get; set; } = [];
}
[McpServerToolType]
public sealed class SqlTools(Settings settings)
{
    [McpServerTool(Name = "list_connections", ReadOnly = true), Description("列出可用 SQL Server 連線名稱，不包含機密。")]
    public string ListConnections() => JsonSerializer.Serialize(settings.Connections.Keys);

    [McpServerTool(Name = "list_entities", ReadOnly = true), Description("列出指定連線允許讀取的實體別名與公開欄位。")]
    public string ListEntities(string connection) => JsonSerializer.Serialize(GetDatabase(connection).Entities.Select(
        x => new { name = x.Key, columns = x.Value.Columns }));

    [McpServerTool(Name = "read_records", ReadOnly = true), Description("讀取允許的實體與欄位。limit 不可超過連線的 MaxRows；結果不保證排序。")]
    public async Task<string> ReadRecords(string connection, string entity, int limit = 100, CancellationToken cancellationToken = default)
    {
        var db = GetDatabase(connection);
        if (!db.Entities.TryGetValue(entity, out var source)) throw new McpException("未知或未授權的實體。");
        if (limit < 1 || limit > db.MaxRows) throw new McpException($"limit 必須介於 1 與 {db.MaxRows}。");
        try
        {
            await using var sql = new SqlConnection(db.BuildConnectionString());
            await sql.OpenAsync(cancellationToken);
            await using var cmd = sql.CreateCommand();
            cmd.CommandTimeout = db.TimeoutSeconds;
            // 識別字僅從設定白名單取得並跳脫；筆數使用參數。
            cmd.CommandText = $"SELECT TOP (@limit) {string.Join(",", source.Columns.Select(Quote))} FROM {Quote(source.Schema)}.{Quote(source.Table)}";
            cmd.Parameters.Add("@limit", System.Data.SqlDbType.Int).Value = limit;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[source.Columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return JsonSerializer.Serialize(rows);
        }
        catch (SqlException) { throw new McpException("資料庫操作失敗，請檢查連線、實體設定與權限。"); }
        catch (InvalidOperationException) { throw new McpException("連線設定或密碼環境變數無效。"); }
    }
    private Database GetDatabase(string name) => settings.Connections.TryGetValue(name, out var db)
        ? db : throw new McpException("未知或未授權的連線。");
    private static string Quote(string value) => "[" + value.Replace("]", "]]") + "]";
}
