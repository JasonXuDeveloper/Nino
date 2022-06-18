// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable IDE1006
#pragma warning disable SA1516

using System;
using System.Collections.Generic;
using ProtoBuf;
using MessagePack;
using Nino.Serialization;

namespace Benchmark.Models
{
    [ProtoContract, System.Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class Answer : IGenericEquality<Answer>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int question_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int answer_id { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime locked_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public DateTime creation_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public DateTime last_edit_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(6), Key(6 - 1), NinoMember(5)]
        public DateTime last_activity_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(7), Key(7 - 1), NinoMember(6)]
        public int score { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(8), Key(8 - 1), NinoMember(7)]
        public DateTime community_owned_date { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(9), Key(9 - 1), NinoMember(8)]
        public bool is_accepted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(10), Key(10 - 1), NinoMember(9)]
        public string body { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(12), Key(12 - 1), NinoMember(10)]
        public string title { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(13), Key(13 - 1), NinoMember(11)]
        public int up_vote_count { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(14), Key(14 - 1), NinoMember(12)]
        public int down_vote_count { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(15), Key(15 - 1), NinoMember(13)]
        public List<Comment> comments { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(16), Key(16 - 1), NinoMember(14)]
        public string link { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(17), Key(17 - 1), NinoMember(15)]
        public List<string> tags { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(18), Key(18 - 1), NinoMember(16)]
        public bool upvoted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(19), Key(19 - 1), NinoMember(17)]
        public bool downvoted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(20), Key(20 - 1), NinoMember(18)]
        public bool accepted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(22), Key(22 - 1), NinoMember(19)]
        public int comment_count { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(23), Key(23 - 1), NinoMember(20)]
        public string body_markdown { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(24), Key(24 - 1), NinoMember(21)]
        public string share_link { get; set; }

        public bool Equals(Answer obj)
        {
            return
                this.accepted.TrueEquals(obj.accepted) &&
                this.answer_id.TrueEquals(obj.answer_id) &&
                this.body.TrueEqualsString(obj.body) &&
                this.body_markdown.TrueEqualsString(obj.body_markdown) &&
                this.comment_count.TrueEquals(obj.comment_count) &&
                this.comments.TrueEqualsList(obj.comments) &&
                this.community_owned_date.TrueEquals(obj.community_owned_date) &&
                this.creation_date.TrueEquals(obj.creation_date) &&
                this.down_vote_count.TrueEquals(obj.down_vote_count) &&
                this.downvoted.TrueEquals(obj.downvoted) &&
                this.is_accepted.TrueEquals(obj.is_accepted) &&
                this.last_activity_date.TrueEquals(obj.last_activity_date) &&
                this.last_edit_date.TrueEquals(obj.last_edit_date) &&
                this.link.TrueEqualsString(obj.link) &&
                this.locked_date.TrueEquals(obj.locked_date) &&
                this.question_id.TrueEquals(obj.question_id) &&
                this.score.TrueEquals(obj.score) &&
                this.share_link.TrueEqualsString(obj.share_link) &&
                this.tags.TrueEqualsString(obj.tags) &&
                this.title.TrueEqualsString(obj.title) &&
                this.up_vote_count.TrueEquals(obj.up_vote_count) &&
                this.upvoted.TrueEquals(obj.upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.accepted.TrueEquals((bool)obj.accepted) &&
                this.answer_id.TrueEquals((int)obj.answer_id) &&
                this.body.TrueEqualsString((string)obj.body) &&
                this.body_markdown.TrueEqualsString((string)obj.body_markdown) &&
                this.comment_count.TrueEquals((int)obj.comment_count) &&
                this.comments.TrueEqualsListDynamic((IEnumerable<dynamic>)obj.comments) &&
                this.community_owned_date.TrueEquals((DateTime)obj.community_owned_date) &&
                this.creation_date.TrueEquals((DateTime)obj.creation_date) &&
                this.down_vote_count.TrueEquals((int)obj.down_vote_count) &&
                this.downvoted.TrueEquals((bool)obj.downvoted) &&
                this.is_accepted.TrueEquals((bool)obj.is_accepted) &&
                this.last_activity_date.TrueEquals((DateTime)obj.last_activity_date) &&
                this.last_edit_date.TrueEquals((DateTime)obj.last_edit_date) &&
                this.link.TrueEqualsString((string)obj.link) &&
                this.locked_date.TrueEquals((DateTime)obj.locked_date) &&
                this.question_id.TrueEquals((int)obj.question_id) &&
                this.score.TrueEquals((int)obj.score) &&
                this.share_link.TrueEqualsString((string)obj.share_link) &&
                this.tags.TrueEqualsString((IEnumerable<string>)obj.tags) &&
                this.title.TrueEqualsString((string)obj.title) &&
                this.up_vote_count.TrueEquals((int)obj.up_vote_count) &&
                this.upvoted.TrueEquals((bool)obj.upvoted);
        }
    }

    [MessagePackObject(true)]
    public class Answer2 : IGenericEquality<Answer2>
    {
        public int question_id { get; set; }
        public int answer_id { get; set; }
        public DateTime locked_date { get; set; }
        public DateTime creation_date { get; set; }
        public DateTime last_edit_date { get; set; }
        public DateTime last_activity_date { get; set; }
        public int score { get; set; }
        public DateTime community_owned_date { get; set; }
        public bool is_accepted { get; set; }
        public string body { get; set; }
        public string title { get; set; }
        public int up_vote_count { get; set; }
        public int down_vote_count { get; set; }
        public List<Comment2> comments { get; set; }
        public string link { get; set; }
        public List<string> tags { get; set; }
        public bool upvoted { get; set; }
        public bool downvoted { get; set; }
        public bool accepted { get; set; }
        public int comment_count { get; set; }
        public string body_markdown { get; set; }
        public string share_link { get; set; }

        public bool Equals(Answer2 obj)
        {
            return
                this.accepted.TrueEquals(obj.accepted) &&
                this.answer_id.TrueEquals(obj.answer_id) &&
                this.body.TrueEqualsString(obj.body) &&
                this.body_markdown.TrueEqualsString(obj.body_markdown) &&
                this.comment_count.TrueEquals(obj.comment_count) &&
                this.comments.TrueEqualsList(obj.comments) &&
                this.community_owned_date.TrueEquals(obj.community_owned_date) &&
                this.creation_date.TrueEquals(obj.creation_date) &&
                this.down_vote_count.TrueEquals(obj.down_vote_count) &&
                this.downvoted.TrueEquals(obj.downvoted) &&
                this.is_accepted.TrueEquals(obj.is_accepted) &&
                this.last_activity_date.TrueEquals(obj.last_activity_date) &&
                this.last_edit_date.TrueEquals(obj.last_edit_date) &&
                this.link.TrueEqualsString(obj.link) &&
                this.locked_date.TrueEquals(obj.locked_date) &&
                this.question_id.TrueEquals(obj.question_id) &&
                this.score.TrueEquals(obj.score) &&
                this.share_link.TrueEqualsString(obj.share_link) &&
                this.tags.TrueEqualsString(obj.tags) &&
                this.title.TrueEqualsString(obj.title) &&
                this.up_vote_count.TrueEquals(obj.up_vote_count) &&
                this.upvoted.TrueEquals(obj.upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.accepted.TrueEquals((bool)obj.accepted) &&
                this.answer_id.TrueEquals((int)obj.answer_id) &&
                this.body.TrueEqualsString((string)obj.body) &&
                this.body_markdown.TrueEqualsString((string)obj.body_markdown) &&
                this.comment_count.TrueEquals((int)obj.comment_count) &&
                this.comments.TrueEqualsListDynamic((IEnumerable<dynamic>)obj.comments) &&
                this.community_owned_date.TrueEquals((DateTime)obj.community_owned_date) &&
                this.creation_date.TrueEquals((DateTime)obj.creation_date) &&
                this.down_vote_count.TrueEquals((int)obj.down_vote_count) &&
                this.downvoted.TrueEquals((bool)obj.downvoted) &&
                this.is_accepted.TrueEquals((bool)obj.is_accepted) &&
                this.last_activity_date.TrueEquals((DateTime)obj.last_activity_date) &&
                this.last_edit_date.TrueEquals((DateTime)obj.last_edit_date) &&
                this.link.TrueEqualsString((string)obj.link) &&
                this.locked_date.TrueEquals((DateTime)obj.locked_date) &&
                this.question_id.TrueEquals((int)obj.question_id) &&
                this.score.TrueEquals((int)obj.score) &&
                this.share_link.TrueEqualsString((string)obj.share_link) &&
                this.tags.TrueEqualsString((IEnumerable<string>)obj.tags) &&
                this.title.TrueEqualsString((string)obj.title) &&
                this.up_vote_count.TrueEquals((int)obj.up_vote_count) &&
                this.upvoted.TrueEquals((bool)obj.upvoted);
        }
    }
}
