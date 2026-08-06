using Microsoft.Data.Sqlite;
using CPMigration.Shared;

namespace CPMigration.Core;

public sealed class SqliteImportService
{
    private readonly IPipelineLogger _logger;
    public SqliteImportService(IPipelineLogger logger) => _logger = logger;

    public async Task<List<TableProfile>> ImportDirectoryAsync(PipelineOptions options, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        var databasePath = Path.Combine(options.OutputDirectory, "erp_intermediario.sqlite");
        if (File.Exists(databasePath)) File.Delete(databasePath);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;", cancellationToken);
        await CreateCatalogAsync(connection, cancellationToken);

        var files = Directory.EnumerateFiles(options.InputDirectory, "*.csv", SearchOption.AllDirectories).OrderBy(x => x).ToArray();
        var result = new List<TableProfile>(files.Length);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            _logger.Write(Severity.Info, "IMPORT_FILE", $"[{index + 1}/{files.Length}] {Path.GetFileName(file)}", file);
            try { result.Add(await ImportFileAsync(connection, file, options, cancellationToken)); }
            catch (Exception ex)
            {
                _logger.Write(Severity.Error, "IMPORT_FAILED", ex.Message, file);
                result.Add(new TableProfile
                {
                    OriginalName = Path.GetFileNameWithoutExtension(file),
                    SqliteName = ToSqliteName(Path.GetFileNameWithoutExtension(file)),
                    SourcePath = file,
                    State = TableState.ReviewRequired
                });
            }
        }
        return result;
    }

    private async Task<TableProfile> ImportFileAsync(SqliteConnection connection, string file, PipelineOptions options, CancellationToken cancellationToken)
    {
        using var reader = new CsvDocumentReader(file);
        var tableName = ToSqliteName(Path.GetFileNameWithoutExtension(file));
        var profile = new TableProfile
        {
            OriginalName = Path.GetFileNameWithoutExtension(file),
            SqliteName = tableName,
            SourcePath = file,
            Encoding = reader.Format.Encoding.WebName,
            Delimiter = reader.Format.Delimiter,
            State = TableState.Mapped
        };
        foreach (var header in reader.Headers) profile.Columns.Add(new ColumnProfile { Name = header });

        var columnDefinitions = string.Join(",", reader.Headers.Select(h => $"{Quote(h)} TEXT"));
        await ExecuteAsync(connection, $"CREATE TABLE {Quote(tableName)} (__SOURCE_ROW INTEGER NOT NULL,{columnDefinitions});", cancellationToken);

        var parameterNames = reader.Headers.Select((_, i) => $"@p{i}").ToArray();
        var insertSql = $"INSERT INTO {Quote(tableName)} (__SOURCE_ROW,{string.Join(',', reader.Headers.Select(Quote))}) VALUES (@row,{string.Join(',', parameterNames)});";
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insertSql;
        command.Parameters.Add(new SqliteParameter("@row", SqliteType.Integer));
        foreach (var parameter in parameterNames) command.Parameters.Add(new SqliteParameter(parameter, SqliteType.Text));

        var pending = 0;
        while (reader.TryRead(out var fields, out var error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (error is not null)
            {
                profile.MalformedRows++;
                _logger.Write(Severity.Warning, "MALFORMED_ROW", error.Message, file, reader.LogicalRowNumber);
                continue;
            }
            command.Parameters["@row"].Value = reader.LogicalRowNumber;
            for (var i = 0; i < reader.Headers.Length; i++)
            {
                var value = i < fields.Length ? fields[i] : null;
                command.Parameters[parameterNames[i]].Value = string.IsNullOrEmpty(value) ? DBNull.Value : value;
                if (!string.IsNullOrEmpty(value))
                {
                    var column = profile.Columns[i];
                    column.NonEmptyCount++;
                    column.MaxLength = Math.Max(column.MaxLength, value.Length);
                    if (column.Examples.Count < 5 && !column.Examples.Contains(value)) column.Examples.Add(value);
                }
                else profile.Columns[i].NullCount++;
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
            profile.RowCount++;
            pending++;
            if (pending >= options.BatchSize) pending = 0;
        }
        await transaction.CommitAsync(cancellationToken);
        await SaveCatalogAsync(connection, profile, cancellationToken);
        return profile;
    }

    private static async Task CreateCatalogAsync(SqliteConnection connection, CancellationToken ct) => await ExecuteAsync(connection,
        "CREATE TABLE __CP_TABLES (ORIGINAL_NAME TEXT, SQLITE_NAME TEXT, SOURCE_PATH TEXT, ROW_COUNT INTEGER, MALFORMED_ROWS INTEGER, ENCODING TEXT, DELIMITER TEXT);" +
        "CREATE TABLE __CP_COLUMNS (TABLE_NAME TEXT, COLUMN_NAME TEXT, NON_EMPTY_COUNT INTEGER, NULL_COUNT INTEGER, MAX_LENGTH INTEGER);", ct);

    private static async Task SaveCatalogAsync(SqliteConnection connection, TableProfile p, CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var table = connection.CreateCommand();
        table.Transaction = tx;
        table.CommandText = "INSERT INTO __CP_TABLES VALUES (@o,@s,@p,@r,@m,@e,@d)";
        table.Parameters.AddWithValue("@o", p.OriginalName); table.Parameters.AddWithValue("@s", p.SqliteName);
        table.Parameters.AddWithValue("@p", p.SourcePath); table.Parameters.AddWithValue("@r", p.RowCount);
        table.Parameters.AddWithValue("@m", p.MalformedRows); table.Parameters.AddWithValue("@e", p.Encoding);
        table.Parameters.AddWithValue("@d", p.Delimiter.ToString());
        await table.ExecuteNonQueryAsync(ct);
        foreach (var c in p.Columns)
        {
            await using var column = connection.CreateCommand();
            column.Transaction = tx;
            column.CommandText = "INSERT INTO __CP_COLUMNS VALUES (@t,@c,@n,@z,@l)";
            column.Parameters.AddWithValue("@t", p.SqliteName); column.Parameters.AddWithValue("@c", c.Name);
            column.Parameters.AddWithValue("@n", c.NonEmptyCount); column.Parameters.AddWithValue("@z", c.NullCount);
            column.Parameters.AddWithValue("@l", c.MaxLength);
            await column.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task ExecuteAsync(SqliteConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(ct);
    }
    public static string ToSqliteName(string name) => "T_" + new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";
}
