// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using ProtoBuf;
using MessagePack;
using Nino.Core;

#pragma warning disable 8618

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoType]
    public partial class Badge : IGenericEquality<Badge>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int BadgeId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public string Name { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public string Description { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public int AwardCount { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(8), Key(8 - 1), NinoMember(6)]
        public string Link { get; set; }

        public bool Equals(Badge obj)
        {
            return
                this.AwardCount.TrueEquals(obj.AwardCount) &&
                this.BadgeId.TrueEquals(obj.BadgeId) &&
                this.Description.TrueEqualsString(obj.Description) &&
                this.Link.TrueEqualsString(obj.Link) &&
                this.Name.TrueEqualsString(obj.Name);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.AwardCount.TrueEquals((int)obj.award_count) &&
                this.BadgeId.TrueEquals((int)obj.badge_id) &&
                this.Description.TrueEqualsString((string)obj.description) &&
                this.Link.TrueEqualsString((string)obj.link) &&
                this.Name.TrueEqualsString((string)obj.name);
        }
    }
}
