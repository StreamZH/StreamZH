// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Data;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace StreamZH.Data;

/// <summary>Opens connections to the StreamZH database and keeps the schema up to date.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    public Database(string databaseFile)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public IDbConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Applies all pending migrations. Safe to call on every start.</summary>
    public void Migrate()
    {
        using var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddSQLite()
                .WithGlobalConnectionString(_connectionString)
                .ScanIn(typeof(Database).Assembly).For.Migrations())
            .BuildServiceProvider(validateScopes: false);

        services.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    public long SchemaVersion()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IFNULL(MAX(Version), 0) FROM VersionInfo";
        return Convert.ToInt64(command.ExecuteScalar());
    }
}