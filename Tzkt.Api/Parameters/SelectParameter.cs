﻿using System;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using NJsonSchema.Annotations;

namespace Tzkt.Api
{
    [ModelBinder(BinderType = typeof(SelectBinder))]
    [JsonSchemaExtensionData("x-tzkt-extension", "query-parameter")]
    public class SelectParameter : INormalized
    {
        /// <summary>
        /// **Fields** selection mode (optional, i.e. `select.fields=balance` is the same as `select=balance`). \
        /// Specify a comma-separated list of fields to include into response.
        /// 
        /// Example: `?select=address,balance` => `[ { "address": "asd", "balance": 10 } ]`.
        /// </summary>
        public string[] Fields { get; set; }

        /// <summary>
        /// **Values** selection mode. \
        /// Specify a comma-separated list of fields to include their values into response.
        /// 
        /// Example: `?select.values=address,balance` => `[ [ "asd", 10 ] ]`.
        /// </summary>
        public string[] Values { get; set; }

        public string Normalize(string name)
        {
            // if (Fields == null && Values == null)
                // return "";

            return Values != null ? $"select.values={string.Join(",", Values)}&" : $"select.fields={string.Join(",", Fields)}&";
        }
    }
}
