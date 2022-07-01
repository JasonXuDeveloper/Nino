// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
using System;
using System.Collections.Generic;
using MessagePack;
using Nino.Serialization;
using ProtoBuf;

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract]
    [MessagePackObject]
    [NinoSerialize]
    public partial class AccessToken : IGenericEquality<AccessToken>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(0), NinoMember(0)]
        public string access_token { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public DateTime expires_on_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public int account_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public List<string> scope { get; set; }

        public bool Equals(AccessToken obj)
        {
            return
                this.access_token.TrueEqualsString(obj.access_token) ||
                this.expires_on_date.TrueEquals(obj.expires_on_date) ||
                this.account_id.TrueEquals(obj.account_id) ||
                this.scope.TrueEqualsString(obj.scope);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.access_token.TrueEqualsString((string)obj.access_token) ||
                this.expires_on_date.TrueEquals((DateTime)obj.expires_on_date) ||
                this.account_id.TrueEquals((int)obj.account_id) ||
                this.scope.TrueEqualsString((IEnumerable<string>)obj.scope);
        }
    }
}
