// <copyright file="Z11072026_WishlistJobIdMigration.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>

namespace slskd.Migrations;

using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using Serilog;

/// <summary>
///     Adds the job columns to the wishlist database: JobId (owning discography/label-crate job release) and
///     DestinationDirectory (the job's target directory for automatic downloads).
/// </summary>
public sealed class Z11072026_WishlistJobIdMigration
{
    public Z11072026_WishlistJobIdMigration(string connectionString)
    {
        ConnectionString = connectionString;
    }

    private string ConnectionString { get; }
    private ILogger Log { get; } = Serilog.Log.ForContext<Z11072026_WishlistJobIdMigration>();

    public void Apply()
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            if (!WishlistTableExists(connection))
            {
                return;
            }

            var columns = GetExistingColumns(connection);
            var statements = new List<string>();
            if (!columns.Contains("JobId"))
            {
                statements.Add("ALTER TABLE WishlistItems ADD COLUMN JobId TEXT NULL");
            }

            if (!columns.Contains("DestinationDirectory"))
            {
                statements.Add("ALTER TABLE WishlistItems ADD COLUMN DestinationDirectory TEXT NULL");
            }

            if (statements.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            foreach (var statement in statements)
            {
                using var command = new SqliteCommand(statement, connection, transaction);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            Log.Information("> Added {Count} job column(s) to WishlistItems.", statements.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Wishlist job-id migration failed: {Message}", ex.Message);
        }
    }

    private static bool WishlistTableExists(SqliteConnection connection)
    {
        using var command = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='WishlistItems'",
            connection);
        return command.ExecuteScalar() != null;
    }

    private static HashSet<string> GetExistingColumns(SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = new SqliteCommand("PRAGMA table_info(WishlistItems)", connection);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
