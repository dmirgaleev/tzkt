﻿using Netezos.Encoding;
using NJsonSchema.Annotations;

namespace Tzkt.Api.Models
{
    public class TicketInfo
    {
        /// <summary>
        /// Internal TzKT id.
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Contract, created the ticket.
        /// </summary>
        public Alias Ticketer { get; set; }

        /// <summary>
        /// Ticket content type in Micheline format.
        /// This field is omitted by default and must be explicitly selected via `?select=` parameter.
        /// </summary>
        public IMicheline RawType { get; set; }

        /// <summary>
        /// Ticket content in Micheline format.
        /// This field is omitted by default and must be explicitly selected via `?select=` parameter.
        /// </summary>
        public IMicheline RawContent { get; set; }

        /// <summary>
        /// Ticket content in JSON format.
        /// </summary>
        [JsonSchemaType(typeof(object), IsNullable = true)]
        public RawJson Content { get; set; }
        
        /// <summary>
        /// 32-bit hash of the ticket content type.
        /// This field can be used for searching similar tickets (which have the same type).
        /// </summary>
        public int TypeHash { get; set; }

        /// <summary>
        /// 32-bit hash of the ticket content.
        /// This field can be used for searching same tickets (which have the same content).
        /// </summary>
        public int ContentHash { get; set; }

        /// <summary>
        /// Total amount of existing tickets.
        /// </summary>
        public string TotalSupply { get; set; }
    }
}
