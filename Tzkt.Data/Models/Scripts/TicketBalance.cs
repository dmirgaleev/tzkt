﻿using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Tzkt.Data.Models
{
    public class TicketBalance
    {
        public long Id { get; set; }
        public int TicketerId { get; set; }
        public long TicketId { get; set; }
        public int AccountId { get; set; }
        public int FirstLevel { get; set; }
        public int LastLevel { get; set; }
        public int TransfersCount { get; set; }
        public BigInteger Balance { get; set; }
        public int? IndexedAt { get; set; }
        
        //TODO Delete ForeignKey
        #region relations
        [ForeignKey(nameof(TicketerId))]
        public Account Ticketer { get; set; }
        
        [ForeignKey(nameof(AccountId))]
        public Account Account { get; set; }
        
        [ForeignKey(nameof(TicketId))]
        public Ticket Ticket { get; set; }
        #endregion
    }

    public static class TicketBalanceModel
    {
        public static void BuildTicketBalanceModel(this ModelBuilder modelBuilder)
        {
            #region keys
            modelBuilder.Entity<TicketBalance>()
                .HasKey(x => x.Id);
            #endregion

            #region props
            // TODO: switch to `numeric` type after migration to .NET 6
            var converter = new ValueConverter<BigInteger, string>(
                x => x.ToString(),
                x => BigInteger.Parse(x));

            modelBuilder.Entity<TicketBalance>()
                .Property(x => x.Balance)
                .HasConversion(converter);
            #endregion

            #region indexes
            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.Id)
                .IsUnique();

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.TicketerId);

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.TicketerId)
                .HasFilter($@"""{nameof(TicketBalance.Balance)}"" != '0'");

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.TicketId);

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.TicketId)
                .HasFilter($@"""{nameof(TicketBalance.Balance)}"" != '0'");

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.AccountId);

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.AccountId)
                .HasFilter($@"""{nameof(TicketBalance.Balance)}"" != '0'");

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => new { x.AccountId, x.TicketerId });

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => new { x.AccountId, x.TicketId })
                .IsUnique();

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.LastLevel);

            modelBuilder.Entity<TicketBalance>()
                .HasIndex(x => x.IndexedAt)
                .HasFilter($@"""{nameof(TicketBalance.IndexedAt)}"" is not null");
            #endregion
        }
    }
}
