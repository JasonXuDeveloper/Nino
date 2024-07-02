// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using MessagePack;
using Nino.Core;
using ProtoBuf;
#pragma warning disable 8618

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract]
    [MessagePackObject]
    [NinoType]
    public partial class AccessToken : IGenericEquality<AccessToken>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(0), NinoMember(0)]
        public string Token { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public DateTime ExpiresOnDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public int AccountId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public List<string> Scope { get; set; }

        public bool Equals(AccessToken obj)
        {
            return
                this.Token.TrueEqualsString(obj.Token) ||
                this.ExpiresOnDate.TrueEquals(obj.ExpiresOnDate) ||
                this.AccountId.TrueEquals(obj.AccountId) ||
                this.Scope.TrueEqualsString(obj.Scope);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.Token.TrueEqualsString((string)obj.access_token) ||
                this.ExpiresOnDate.TrueEquals((DateTime)obj.expires_on_date) ||
                this.AccountId.TrueEquals((int)obj.account_id) ||
                this.Scope.TrueEqualsString((IEnumerable<string>)obj.scope);
        }
    }
}
