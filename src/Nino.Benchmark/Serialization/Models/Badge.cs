// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using ProtoBuf;
using MessagePack;
using Nino.Serialization;

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class Badge : IGenericEquality<Badge>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int badge_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public string name { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public string description { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public int award_count { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(8), Key(8 - 1), NinoMember(6)]
        public string link { get; set; }

        public bool Equals(Badge obj)
        {
            return
                this.award_count.TrueEquals(obj.award_count) &&
                this.badge_id.TrueEquals(obj.badge_id) &&
                this.description.TrueEqualsString(obj.description) &&
                this.link.TrueEqualsString(obj.link) &&
                this.name.TrueEqualsString(obj.name);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.award_count.TrueEquals((int)obj.award_count) &&
                this.badge_id.TrueEquals((int)obj.badge_id) &&
                this.description.TrueEqualsString((string)obj.description) &&
                this.link.TrueEqualsString((string)obj.link) &&
                this.name.TrueEqualsString((string)obj.name);
        }
    }
}
