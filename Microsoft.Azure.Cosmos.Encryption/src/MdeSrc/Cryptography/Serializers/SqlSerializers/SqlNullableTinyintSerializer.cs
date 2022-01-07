//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

// This file isn't generated, but this comment is necessary to exclude it from StyleCop analysis.
// <auto-generated/>

using System;

namespace Microsoft.Data.Encryption.Cryptography.Serializers
{
    /// <summary>
    /// Contains the methods for serializing and deserializing <see cref="byte"/>? type data objects
    /// that is compatible with the Always Encrypted feature in SQL Server and Azure SQL Database.
    /// </summary>
    internal class SqlNullableTinyIntSerializer : Serializer<byte?>
    {
        private static readonly SqlTinyIntSerializer serializer = new SqlTinyIntSerializer();

        /// <summary>
        /// The <see cref="Identifier"/> uniquely identifies a particular Serializer implementation.
        /// </summary>
        public override string Identifier => "SQL_TinyInt_Nullable";

        /// <summary>
        /// Deserializes the provided <paramref name="bytes"/>
        /// </summary>
        /// <param name="bytes">The data to be deserialized</param>
        /// <returns>The serialized data</returns>
        /// <exception cref="MicrosoftDataEncryptionException">
        /// The length of <paramref name="bytes"/> is less than 8.
        /// </exception>
        public override byte? Deserialize(byte[] bytes)
        {
            return bytes.IsNull() ? (byte?)null : serializer.Deserialize(bytes);
        }

        /// <summary>
        /// Serializes the provided <paramref name="value"/>
        /// </summary>
        /// <param name="value">The value to be serialized</param>
        /// <returns>
        /// An array of bytes with length 8.
        /// </returns>
        public override byte[] Serialize(byte? value)
        {
            return value.IsNull() ? null : serializer.Serialize(value.Value);
        }
    }
}
