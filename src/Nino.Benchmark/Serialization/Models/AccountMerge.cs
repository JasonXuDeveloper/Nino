// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using ProtoBuf;
using MessagePack;
using Nino.Core;

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoType]
    public partial class AccountMerge : IGenericEquality<AccountMerge>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int OldAccountId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int NewAccountId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime MergeDate { get; set; }

        public bool Equals(AccountMerge obj)
        {
            return
                this.OldAccountId.TrueEquals(obj.OldAccountId) &&
                this.NewAccountId.TrueEquals(obj.NewAccountId) &&
                this.MergeDate.TrueEquals(obj.MergeDate);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.OldAccountId.TrueEquals((int)obj.old_account_id) &&
                this.NewAccountId.TrueEquals((int)obj.new_account_id) &&
                this.MergeDate.TrueEquals((DateTime)obj.merge_date);
        }
    }
}
