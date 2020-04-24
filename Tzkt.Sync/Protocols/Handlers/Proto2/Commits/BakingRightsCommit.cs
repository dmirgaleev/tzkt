﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tzkt.Data.Models;

namespace Tzkt.Sync.Protocols.Proto2
{
    class BakingRightsCommit : ProtocolCommit
    {
        Block Block;
        Protocol Protocol;
        int Cycle;

        BakingRightsCommit(ProtocolHandler protocol) : base(protocol) { }

        public async Task Init(Block block)
        {
            Block = block;
            Protocol = block.Protocol ?? await Cache.GetProtocolAsync(block.ProtoCode);
            Cycle = (Block.Level - 1) / Protocol.BlocksPerCycle;
        }

        public override async Task Apply()
        {
            var sql = "";

            // TODO: should use protocol of the previous block
            if (Block.Priority == 0 && Block.Validations == Protocol.EndorsersPerBlock)
            {
                sql = $@"
                    DELETE  FROM ""BakingRights""
                    WHERE   ""Type"" = {(int)BakingRightType.Baking}
                    AND     ""Level"" = {Block.Level}
                    AND     ""Priority"" > 0;

                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Success}
                    WHERE   ""Level"" = {Block.Level};";
            }
            else
            {
                #region baking
                if (Block.Priority == 0)
                {
                    sql = $@"
                    DELETE  FROM ""BakingRights""
                    WHERE   ""Type"" = {(int)BakingRightType.Baking}
                    AND     ""Level"" = {Block.Level}
                    AND     ""Priority"" > 0;

                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Success}
                    WHERE   ""Type"" = {(int)BakingRightType.Baking}
                    AND     ""Level"" = {Block.Level};";
                }
                else
                {
                    var weirdRights = await Db.Database.ExecuteSqlRawAsync($@"
                        DELETE  FROM ""BakingRights"" as r
                        USING   ""Accounts"" as a
                        WHERE   a.""Id"" = r.""BakerId"" AND a.""Type"" != {(int)AccountType.Delegate}
                        AND     r.""Type"" = {(int)BakingRightType.Baking}
                        AND     r.""Level"" = {Block.Level};");

                    var existedRights = await Db.Database.ExecuteSqlRawAsync($@"
                        UPDATE  ""BakingRights""
                        SET     ""Status"" = {(int)BakingRightStatus.Missed}
                        WHERE   ""Type"" = {(int)BakingRightType.Baking}
                        AND     ""Level"" = {Block.Level};");

                    if (weirdRights + existedRights > Block.Priority)
                    {
                        sql = $@"
                            DELETE  FROM ""BakingRights""
                            WHERE   ""Type"" = {(int)BakingRightType.Baking}
                            AND     ""Level"" = {Block.Level}
                            AND     ""Priority"" > {Block.Priority};

                            UPDATE  ""BakingRights""
                            SET     ""Status"" = {(int)BakingRightStatus.Success}
                            WHERE   ""Type"" = {(int)BakingRightType.Baking}
                            AND     ""Level"" = {Block.Level}
                            AND     ""Priority"" = {Block.Priority};";
                    }
                    else
                    {
                        using var stream = await Proto.Node.GetLevelBakingRightsAsync(Block.Level, Block.Priority + 1);
                        var bakingRights = await (Proto.Serializer as Serializer).DeserializeBakingRights(stream);
                        //bakingRights = bakingRights.OrderBy(x => x.Priority);

                        for (int i = weirdRights + existedRights; i < bakingRights.Count; i++)
                        {
                            var delegat = await Cache.GetDelegateOrDefaultAsync(bakingRights[i].Delegate);
                            if (delegat == null) continue; // WTF: [level:28680] - Baking rights were given to non-baker account

                            Db.BakingRights.Add(new BakingRight
                            {
                                BakerId = delegat.Id,
                                Cycle = Cycle,
                                Level = Block.Level,
                                Priority = bakingRights[i].Priority,
                                Status = bakingRights[i].Priority == Block.Priority ? BakingRightStatus.Success : BakingRightStatus.Missed,
                                Type = BakingRightType.Baking,
                            });
                        }
                    }
                }
                #endregion

                #region endorsing
                // TODO: should use protocol of the previous block
                if (Block.Validations == Protocol.EndorsersPerBlock)
                {
                    sql += $@"
                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Success}
                    WHERE   ""Type"" = {(int)BakingRightType.Endorsing}
                    AND     ""Level"" = {Block.Level};";
                }
                else if (Block.Endorsements?.Count > 0)
                {
                    sql += $@"
                    DELETE  FROM ""BakingRights"" as r
                    USING   ""Accounts"" as a
                    WHERE   a.""Id"" = r.""BakerId"" AND a.""Type"" != {(int)AccountType.Delegate}
                    AND     r.""Type"" = {(int)BakingRightType.Endorsing}
                    AND     r.""Level"" = {Block.Level};

                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Missed}
                    WHERE   ""Type"" = {(int)BakingRightType.Endorsing}
                    AND     ""Level"" = {Block.Level};

                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Success}
                    WHERE   ""Type"" = {(int)BakingRightType.Endorsing}
                    AND     ""Level"" = {Block.Level}
                    AND     ""BakerId"" = ANY(ARRAY[{string.Join(',', Block.Endorsements.Select(x => x.Delegate.Id))}]);";
                }
                else
                {
                    sql += $@"
                    DELETE  FROM ""BakingRights"" as r
                    USING   ""Accounts"" as a
                    WHERE   a.""Id"" = r.""BakerId"" AND a.""Type"" != {(int)AccountType.Delegate}
                    AND     r.""Type"" = {(int)BakingRightType.Endorsing}
                    AND     r.""Level"" = {Block.Level};

                    UPDATE  ""BakingRights""
                    SET     ""Status"" = {(int)BakingRightStatus.Missed}
                    WHERE   ""Type"" = {(int)BakingRightType.Endorsing}
                    AND     ""Level"" = {Block.Level};";
                }
                #endregion
            }

            await Db.Database.ExecuteSqlRawAsync(sql);

            #region new cycle
            if (Block.Events.HasFlag(BlockEvents.CycleBegin))
            {
                var futureCycle = Cycle + Protocol.PreservedCycles;

                var rights = await Task.WhenAll(
                    Proto.Node.GetBakingRightsAsync(Block.Level, futureCycle, BakingRight.MaxPriority + 1),
                    Proto.Node.GetEndorsingRightsAsync(Block.Level, futureCycle));

                var bakingRights = await (Proto.Serializer as Serializer).DeserializeBakingRights(rights[0]);
                var endorsingRights = await (Proto.Serializer as Serializer).DeserializeEndorsingRights(rights[1]);

                var delegates = new HashSet<string>(512);
                foreach (var r in bakingRights) delegates.Add(r.Delegate);
                foreach (var r in endorsingRights) delegates.Add(r.Delegate);

                await Cache.PrepareAccounts(delegates.ToList());

                var conn = Db.Database.GetDbConnection() as NpgsqlConnection;
                using var writer = conn.BeginBinaryImport(@"COPY ""BakingRights"" (""Cycle"", ""Level"", ""BakerId"", ""Type"", ""Status"", ""Priority"", ""Slots"") FROM STDIN (FORMAT BINARY)");

                foreach (var er in endorsingRights)
                {
                    if (!await Cache.AccountExistsAsync(er.Delegate))
                        throw new Exception($"Account {er.Delegate} doesn't exist");

                    // WTF: [level:28680] - Baking rights were given to non-baker account
                    var acc = await Cache.GetAccountAsync(er.Delegate);

                    writer.StartRow();
                    writer.Write(er.Level / Protocol.BlocksPerCycle, NpgsqlTypes.NpgsqlDbType.Integer); // level + 1
                    writer.Write(er.Level + 1, NpgsqlTypes.NpgsqlDbType.Integer);                       // level + 1
                    writer.Write(acc.Id, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write((byte)BakingRightType.Endorsing, NpgsqlTypes.NpgsqlDbType.Smallint);
                    writer.Write((byte)BakingRightStatus.Future, NpgsqlTypes.NpgsqlDbType.Smallint);
                    writer.WriteNull();
                    writer.Write(er.Slots.Count, NpgsqlTypes.NpgsqlDbType.Integer);
                }

                foreach (var br in bakingRights)
                {
                    if (!await Cache.AccountExistsAsync(br.Delegate))
                        throw new Exception($"Account {br.Delegate} doesn't exist");

                    // WTF: [level:28680] - Baking rights were given to non-baker account
                    var acc = await Cache.GetAccountAsync(br.Delegate);

                    writer.StartRow();
                    writer.Write(futureCycle, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(br.Level, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write(acc.Id, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.Write((byte)BakingRightType.Baking, NpgsqlTypes.NpgsqlDbType.Smallint);
                    writer.Write((byte)BakingRightStatus.Future, NpgsqlTypes.NpgsqlDbType.Smallint);
                    writer.Write(br.Priority, NpgsqlTypes.NpgsqlDbType.Integer);
                    writer.WriteNull();
                }

                writer.Complete();
            }
            #endregion
        }

        public override async Task Revert()
        {
            #region new cycle
            if (Block.Events.HasFlag(BlockEvents.CycleBegin))
            {
                await Db.Database.ExecuteSqlRawAsync($@"
                    DELETE FROM ""BakingRights""
                    WHERE   ""Cycle"" = {Cycle + Protocol.PreservedCycles} AND ""Type"" = 0
                    OR      ""Level"" > {(Cycle + Protocol.PreservedCycles) * Protocol.BlocksPerCycle + 1}");
            }
            #endregion

            #region endorsing & baking
            await Db.Database.ExecuteSqlRawAsync($@"
                UPDATE  ""BakingRights""
                SET     ""Status"" = {(int)BakingRightStatus.Future}
                WHERE   ""Level"" = {Block.Level}");
            #endregion
        }

        #region static
        public static async Task<BakingRightsCommit> Apply(ProtocolHandler proto, Block block)
        {
            var commit = new BakingRightsCommit(proto);
            await commit.Init(block);
            await commit.Apply();

            return commit;
        }

        public static async Task<BakingRightsCommit> Revert(ProtocolHandler proto, Block block)
        {
            var commit = new BakingRightsCommit(proto);
            await commit.Init(block);
            await commit.Revert();

            return commit;
        }
        #endregion
    }
}
