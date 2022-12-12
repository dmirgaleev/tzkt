﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Dapper;
using Npgsql;
using Dynamic.Json;
using Netezos.Encoding;
using Tzkt.Data.Models;

namespace Tzkt.Sync.Services.Domains
{
    public class DomainsService : BackgroundService
    {
        readonly string ConnectionString;
        readonly DomainsConfig Config;
        readonly ILogger Logger;

        #region state
        int RecordsBigMap = -1;
        int ExpiryBigMap = -1;
        int Level = -1;
        #endregion

        public DomainsService(IConfiguration config, ILogger<DomainsService> logger)
        {
            ConnectionString = config.GetConnectionString("DefaultConnection");
            Config = config.GetDomainsConfig();
            Logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                Logger.LogInformation("Domains plugin started");

                await InitState();
                if (RecordsBigMap == -1 || ExpiryBigMap == -1)
                {
                    Logger.LogWarning("Domains plugin configured with an invalid name registry contract");
                    return;
                }

                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await SyncDomains(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed to sync domains");
                    }
                    await Task.Delay(Config.PeriodSec * 1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Domains plugin crashed");
            }
            finally
            {
                Logger.LogInformation("Domains plugin stopped");
            }
        }

        async Task SyncDomains(CancellationToken ct)
        {
            using var conn = new NpgsqlConnection(ConnectionString);
            while (!ct.IsCancellationRequested)
            {
                var pending = await GetPendingDomains(conn, 10_000);
                if (pending.Count == 0)
                {
                    await UpdateExpirations(conn);
                    break;
                }

                var lastLevel = pending.Max(x => x.LastLevel);
                await SaveDomains(conn, pending);
                await UpdateExpirations(conn);

                if (pending.Count == 10_000)
                {
                    await SaveState(conn, lastLevel);
                    Level = lastLevel;
                }
                else
                {
                    await SaveState(conn, lastLevel + 1);
                    Level = lastLevel + 1;
                    break;
                }
            }
        }

        async Task<List<Domain>> GetPendingDomains(NpgsqlConnection conn, int limit)
        {
            var rows = await conn.QueryAsync("""
                SELECT  record."Id",
                        record."FirstLevel",
                        record."LastLevel",
                        record."JsonKey"#>>'{}' as "Name",
                        record."JsonValue"->>'level' as "Level",
                        record."JsonValue"->>'address' as "Address",
                        record."JsonValue"->>'owner' as "Owner",
                        record."JsonValue"->'data' as "Data",
                        expiry."JsonValue"#>>'{}' as "Expiration"
                FROM "BigMapKeys" as record
                LEFT JOIN "BigMapKeys" as expiry
                ON expiry."BigMapPtr" = @ptr2
                AND expiry."JsonKey" = record."JsonValue"->'expiry_key'
                INNER JOIN "AppState" as state
                ON state."Id" = -1
                WHERE record."BigMapPtr" = @ptr
                AND record."LastLevel" >= @level
                AND record."LastLevel" < state."Level"
                ORDER BY record."LastLevel"
                LIMIT @limit
                """, new { ptr = RecordsBigMap, ptr2 = ExpiryBigMap, level = Level, limit });

            var res = new List<Domain>(rows.Count());
            foreach (var row in rows)
            {
                try
                {
                    res.Add(new Domain
                    {
                        Id = row.Id,
                        Level = int.Parse(row.Level),
                        Name = Utf8.Convert(Hex.Parse((string)row.Name)),
                        Address = row.Address,
                        Owner = row.Owner,
                        Expiration = row.Expiration is string s 
                            ? DateTimeOffset.Parse(s).UtcDateTime
                            : DateTimeOffset.MaxValue.UtcDateTime.Date,
                        Data = row.Data == "{}" ? null : ParseDomainData((string)row.Data),
                        FirstLevel = row.FirstLevel,
                        LastLevel = row.LastLevel
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to parse domain record {id}", (int)row.Id);
                }
            }
            return res;
        }

        static async Task SaveDomains(NpgsqlConnection conn, List<Domain> domains)
        {
            for (int i = 0; i < domains.Count; i += 1000)
            {
                var p = 0;
                var sql = new StringBuilder();
                var param = new DynamicParameters();
                var max = Math.Min(1000, domains.Count - i);

                sql.AppendLine(@"INSERT INTO ""Domains"" (""Id"", ""Level"", ""Name"", ""Address"", ""Owner"", ""Expiration"", ""Data"", ""FirstLevel"", ""LastLevel"") VALUES");
                for (int j = 0; j < max; j++)
                {
                    var domain = domains[i + j];
                    param.Add($"@p{p}", domain.Id);
                    param.Add($"@p{p + 1}", domain.Level);
                    param.Add($"@p{p + 2}", domain.Name);
                    param.Add($"@p{p + 3}", domain.Address);
                    param.Add($"@p{p + 4}", domain.Owner);
                    param.Add($"@p{p + 5}", domain.Expiration);
                    param.Add($"@p{p + 6}", domain.Data == null ? null : JsonSerializer.Serialize(domain.Data));
                    param.Add($"@p{p + 7}", domain.FirstLevel);
                    param.Add($"@p{p + 8}", domain.LastLevel);
                    sql.Append($"(@p{p}, @p{p + 1}, @p{p + 2}, @p{p + 3}, @p{p + 4}, @p{p + 5}, @p{p + 6}::jsonb, @p{p + 7}, @p{p + 8})");
                    if (j < max - 1) sql.Append(',');
                    sql.AppendLine();
                    p += 9;
                }
                sql.AppendLine(@"ON CONFLICT (""Id"") DO UPDATE SET");
                sql.AppendLine(@"""Level"" = EXCLUDED.""Level"",");
                sql.AppendLine(@"""Name"" = EXCLUDED.""Name"",");
                sql.AppendLine(@"""Address"" = EXCLUDED.""Address"",");
                sql.AppendLine(@"""Owner"" = EXCLUDED.""Owner"",");
                sql.AppendLine(@"""Expiration"" = EXCLUDED.""Expiration"",");
                sql.AppendLine(@"""Data"" = EXCLUDED.""Data"",");
                sql.AppendLine(@"""FirstLevel"" = EXCLUDED.""FirstLevel"",");
                sql.AppendLine(@"""LastLevel"" = EXCLUDED.""LastLevel"";");

                await conn.ExecuteAsync(sql.ToString(), param);
            }
        }

        async Task UpdateExpirations(NpgsqlConnection conn)
        {
            await conn.ExecuteAsync("""
                UPDATE "Domains" 
                SET "Expiration" = updates.expiration
                FROM (
                	SELECT id, expiration
                	FROM (
                		SELECT  record."Id" AS id,
                				(expiry."JsonValue"#>>'{}')::timestamptz AS expiration
                		FROM "BigMapKeys" AS expiry
                		INNER JOIN "BigMapKeys" AS record
                		ON record."BigMapPtr" = @ptr
                		AND record."JsonValue"->'expiry_key' = expiry."JsonKey"
                		INNER JOIN "AppState" AS state
                		ON state."Id" = -1
                		WHERE expiry."BigMapPtr" = @ptr2
                		AND expiry."LastLevel" >= @level
                		AND expiry."LastLevel" < state."Level"
                	) expiry_map
                	INNER JOIN "Domains" AS domain
                	ON domain."Id" = id
                	WHERE domain."Expiration" != expiration
                    FOR UPDATE
                ) updates
                WHERE "Id" = updates.id
                """, new { ptr = RecordsBigMap, ptr2 = ExpiryBigMap, level = Level });
        }

        async Task InitState()
        {
            using var conn = new NpgsqlConnection(ConnectionString);
            var (contract, level) = await GetState(conn);
            if (contract != Config.NameRegistry)
            {
                await conn.ExecuteAsync(@"DELETE FROM ""Domains""");
                await SaveState(conn, Config.NameRegistry);
                (contract, level) = (Config.NameRegistry, 0);
            }

            var contractId = await conn.QueryFirstOrDefaultAsync<int>("""
                SELECT "Id"
                FROM "Accounts"
                WHERE "Address" = @contract
                LIMIT 1
            """, new { contract });

            if (contractId == 0) return;

            var recordsBigMap = await conn.QueryFirstOrDefaultAsync<int>("""
                SELECT "Ptr"
                FROM "BigMaps"
                WHERE "ContractId" = @contractId
                AND "StoragePath" = 'store.records'
                AND "Active" = true
                LIMIT 1
            """, new { contractId });

            if (recordsBigMap == 0) return;

            var expiryBigMap = await conn.QueryFirstOrDefaultAsync<int>("""
                SELECT "Ptr"
                FROM "BigMaps"
                WHERE "ContractId" = @contractId
                AND "StoragePath" = 'store.expiry_map'
                AND "Active" = true
                LIMIT 1
            """, new { contractId });

            if (expiryBigMap == 0) return;

            RecordsBigMap = recordsBigMap;
            ExpiryBigMap = expiryBigMap;
            Level = level;
        }

        static async Task<(string, int)> GetState(NpgsqlConnection conn)
        {
            var row = await conn.QueryFirstAsync("""
                SELECT "DomainsNameRegistry", "DomainsLevel"
                FROM "AppState"
                WHERE "Id" = -1
                LIMIT 1
            """);
            return (row.DomainsNameRegistry, row.DomainsLevel);
        }

        static async Task SaveState(NpgsqlConnection conn, string contract)
        {
            await conn.ExecuteAsync("""
                UPDATE "AppState"
                SET "DomainsNameRegistry" = @contract, "DomainsLevel" = 0
                WHERE "Id" = -1
            """, new { contract });
        }

        static async Task SaveState(NpgsqlConnection conn, int level)
        {
            await conn.ExecuteAsync("""
                UPDATE "AppState"
                SET "DomainsLevel" = @level
                WHERE "Id" = -1
            """, new { level });
        }

        static JsonElement ParseDomainData(string data)
        {
            var res = new Dictionary<string, string>();
            foreach (var prop in DJson.Parse(data))
            {
                var bytes = Hex.Parse((string)prop.Value);
                res[(string)prop.Name] = IsReadable(bytes) ? Utf8.Convert(bytes) : (string)prop.Value;
            }
            return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(res));
        }

        static bool IsReadable(byte[] bytes) => bytes.Count(x => x >= 32 && x <= 126) / (double)bytes.Length > 0.8;
    }
}
