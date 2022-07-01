// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable IDE1006
#pragma warning disable SA1516

using System;
using MessagePack;
using Nino.Serialization;
using ProtoBuf;
#pragma warning disable 8618

namespace Nino.Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class Comment : IGenericEquality<Comment>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int CommentId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int PostId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime CreationDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public int Score { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(6), Key(6 - 1), NinoMember(5)]
        public bool Edited { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(7), Key(7 - 1), NinoMember(6)]
        public string Body { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(10), Key(10 - 1), NinoMember(7)]
        public string Link { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(11), Key(11 - 1), NinoMember(8)]
        public string BodyMarkdown { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(12), Key(12 - 1), NinoMember(9)]
        public bool Upvoted { get; set; }

        public bool Equals(Comment obj)
        {
            return
                this.Body.TrueEqualsString(obj.Body) &&
                this.BodyMarkdown.TrueEqualsString(obj.BodyMarkdown) &&
                this.CommentId.TrueEquals(obj.CommentId) &&
                this.CreationDate.TrueEquals(obj.CreationDate) &&
                this.Edited.TrueEquals(obj.Edited) &&
                this.Link.TrueEqualsString(obj.Link) &&
                this.PostId.TrueEquals(obj.PostId) &&
                this.Score.TrueEquals(obj.Score) &&
                this.Upvoted.TrueEquals(obj.Upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.Body.TrueEqualsString((string)obj.body) &&
                this.BodyMarkdown.TrueEqualsString((string)obj.body_markdown) &&
                this.CommentId.TrueEquals((int)obj.comment_id) &&
                this.CreationDate.TrueEquals((DateTime)obj.creation_date) &&
                this.Edited.TrueEquals((bool)obj.edited) &&
                this.Link.TrueEqualsString((string)obj.link) &&
                this.PostId.TrueEquals((int)obj.post_id) &&
                this.Score.TrueEquals((int)obj.score) &&
                this.Upvoted.TrueEquals((bool)obj.upvoted);
        }
    }
}
