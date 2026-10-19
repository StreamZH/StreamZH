// StreamZH - one app for your whole media pipeline.
// Copyright (c) 2026 The StreamZH Authors.
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using Dapper;
using FluentMigrator;
using StreamZH.Core.Security;

namespace StreamZH.Data.Migrations;

/// <summary>
/// 0.12: history entries stored the release's download link and guid with the indexer's API key ("apikey=", "r=", …).
/// They are rewritten without it (<see cref="SecretUrls"/>); new entries are stored that way by <see cref="HistoryRepository"/>.
/// Only rows that contain a secret parameter are touched, so running it again changes nothing.
/// </summary>
[Migration(28, "Redact history links")]
public sealed class M028_RedactHistoryLinks : Migration
{
    public override void Up() => Execute.WithConnection((connection, transaction) => Redact(connection, transaction));

    public override void Down()
    {
        // The keys are gone for good; nothing to restore.
    }

    public static int Redact(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction)
    {
        var rows = connection.Query<(long Id, string Data)>("SELECT Id, Data FROM History", transaction: transaction).ToList();
        var changed = 0;
        foreach (var (id, data) in rows)
        {
            Dictionary<string, string?>? values;
            try
            {
                values = JsonSerializer.Deserialize<Dictionary<string, string?>>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (values is null || !values.Values.Any(SecretUrls.ContainsSecret))
            {
                continue;
            }

            var redacted = HistoryRepository.RedactData(values);
            connection.Execute("UPDATE History SET Data = @data WHERE Id = @id", new { id, data = JsonSerializer.Serialize(redacted) }, transaction);
            changed++;
        }

        return changed;
    }
}
