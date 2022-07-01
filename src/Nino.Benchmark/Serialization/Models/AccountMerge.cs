// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using ProtoBuf;
using MessagePack;
using Nino.Serialization;

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class AccountMerge : IGenericEquality<AccountMerge>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int old_account_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int new_account_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime merge_date { get; set; }

        public bool Equals(AccountMerge obj)
        {
            return
                this.old_account_id.TrueEquals(obj.old_account_id) &&
                this.new_account_id.TrueEquals(obj.new_account_id) &&
                this.merge_date.TrueEquals(obj.merge_date);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.old_account_id.TrueEquals((int)obj.old_account_id) &&
                this.new_account_id.TrueEquals((int)obj.new_account_id) &&
                this.merge_date.TrueEquals((DateTime)obj.merge_date);
        }
    }
}
