// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable IDE1006
#pragma warning disable SA1516

using System;
using MessagePack;
using Nino.Serialization;
using ProtoBuf;

namespace Benchmark.Models
{
    public enum PostType : byte
    {
        question = 1,
        answer = 2,
    }

    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class Comment : IGenericEquality<Comment>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int comment_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int post_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime creation_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public PostType post_type { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public int score { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(6), Key(6 - 1), NinoMember(5)]
        public bool edited { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(7), Key(7 - 1), NinoMember(6)]
        public string body { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(10), Key(10 - 1), NinoMember(7)]
        public string link { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(11), Key(11 - 1), NinoMember(8)]
        public string body_markdown { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(12), Key(12 - 1), NinoMember(9)]
        public bool upvoted { get; set; }

        public bool Equals(Comment obj)
        {
            return
                this.body.TrueEqualsString(obj.body) &&
                this.body_markdown.TrueEqualsString(obj.body_markdown) &&
                this.comment_id.TrueEquals(obj.comment_id) &&
                this.creation_date.TrueEquals(obj.creation_date) &&
                this.edited.TrueEquals(obj.edited) &&
                this.link.TrueEqualsString(obj.link) &&
                this.post_id.TrueEquals(obj.post_id) &&
                this.post_type.TrueEquals(obj.post_type) &&
                this.score.TrueEquals(obj.score) &&
                this.upvoted.TrueEquals(obj.upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.body.TrueEqualsString((string)obj.body) &&
                this.body_markdown.TrueEqualsString((string)obj.body_markdown) &&
                this.comment_id.TrueEquals((int)obj.comment_id) &&
                this.creation_date.TrueEquals((DateTime)obj.creation_date) &&
                this.edited.TrueEquals((bool)obj.edited) &&
                this.link.TrueEqualsString((string)obj.link) &&
                this.post_id.TrueEquals((int)obj.post_id) &&
                this.post_type.TrueEquals((PostType)obj.post_type) &&
                this.score.TrueEquals((int)obj.score) &&
                this.upvoted.TrueEquals((bool)obj.upvoted);
        }
    }

    [MessagePackObject(true)]
    public class Comment2 : IGenericEquality<Comment2>
    {
        public int comment_id { get; set; }
        public int post_id { get; set; }
        public DateTime creation_date { get; set; }
        public PostType post_type { get; set; }
        public int score { get; set; }
        public bool edited { get; set; }
        public string body { get; set; }
        public string link { get; set; }
        public string body_markdown { get; set; }
        public bool upvoted { get; set; }

        public bool Equals(Comment2 obj)
        {
            return
                this.body.TrueEqualsString(obj.body) &&
                this.body_markdown.TrueEqualsString(obj.body_markdown) &&
                this.comment_id.TrueEquals(obj.comment_id) &&
                this.creation_date.TrueEquals(obj.creation_date) &&
                this.edited.TrueEquals(obj.edited) &&
                this.link.TrueEqualsString(obj.link) &&
                this.post_id.TrueEquals(obj.post_id) &&
                this.post_type.TrueEquals(obj.post_type) &&
                this.score.TrueEquals(obj.score) &&
                this.upvoted.TrueEquals(obj.upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.body.TrueEqualsString((string)obj.body) &&
                this.body_markdown.TrueEqualsString((string)obj.body_markdown) &&
                this.comment_id.TrueEquals((int)obj.comment_id) &&
                this.creation_date.TrueEquals((DateTime)obj.creation_date) &&
                this.edited.TrueEquals((bool)obj.edited) &&
                this.link.TrueEqualsString((string)obj.link) &&
                this.post_id.TrueEquals((int)obj.post_id) &&
                this.post_type.TrueEquals((PostType)obj.post_type) &&
                this.score.TrueEquals((int)obj.score) &&
                this.upvoted.TrueEquals((bool)obj.upvoted);
        }
    }
}
