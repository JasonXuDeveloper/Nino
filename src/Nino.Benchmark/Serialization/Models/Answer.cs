// Copyright (c) All contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#pragma warning disable IDE1006
#pragma warning disable SA1516

using System;
using System.Collections.Generic;
using ProtoBuf;
using MessagePack;
using Nino.Serialization;
#pragma warning disable 8618

namespace Nino.Benchmark.Models
{
    [ProtoContract, Serializable, System.Runtime.Serialization.DataContract, MessagePackObject]
    [NinoSerialize]
    public partial class Answer : IGenericEquality<Answer>
    {
        [System.Runtime.Serialization.DataMember, ProtoMember(1), Key(1 - 1), NinoMember(0)]
        public int QuestionId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(2), Key(2 - 1), NinoMember(1)]
        public int AnswerId { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(3), Key(3 - 1), NinoMember(2)]
        public DateTime LockedDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(4), Key(4 - 1), NinoMember(3)]
        public DateTime CreationDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(5), Key(5 - 1), NinoMember(4)]
        public DateTime LastEditDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(6), Key(6 - 1), NinoMember(5)]
        public DateTime LastActivityDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(7), Key(7 - 1), NinoMember(6)]
        public int Score { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(8), Key(8 - 1), NinoMember(7)]
        public DateTime CommunityOwnedDate { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(9), Key(9 - 1), NinoMember(8)]
        public bool IsAccepted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(10), Key(10 - 1), NinoMember(9)]
        public string Body { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(12), Key(12 - 1), NinoMember(10)]
        public string Title { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(13), Key(13 - 1), NinoMember(11)]
        public int UpVoteCount { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(14), Key(14 - 1), NinoMember(12)]
        public int DownVoteCount { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(15), Key(15 - 1), NinoMember(13)]
        public List<Comment> Comments { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(16), Key(16 - 1), NinoMember(14)]
        public string Link { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(17), Key(17 - 1), NinoMember(15)]
        public List<string> Tags { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(18), Key(18 - 1), NinoMember(16)]
        public bool Upvoted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(19), Key(19 - 1), NinoMember(17)]
        public bool Downvoted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(20), Key(20 - 1), NinoMember(18)]
        public bool Accepted { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(22), Key(22 - 1), NinoMember(19)]
        public int CommentCount { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(23), Key(23 - 1), NinoMember(20)]
        public string BodyMarkdown { get; set; }

        [System.Runtime.Serialization.DataMember, ProtoMember(24), Key(24 - 1), NinoMember(21)]
        public string ShareLink { get; set; }

        public bool Equals(Answer obj)
        {
            return
                this.Accepted.TrueEquals(obj.Accepted) &&
                this.AnswerId.TrueEquals(obj.AnswerId) &&
                this.Body.TrueEqualsString(obj.Body) &&
                this.BodyMarkdown.TrueEqualsString(obj.BodyMarkdown) &&
                this.CommentCount.TrueEquals(obj.CommentCount) &&
                this.Comments.TrueEqualsList(obj.Comments) &&
                this.CommunityOwnedDate.TrueEquals(obj.CommunityOwnedDate) &&
                this.CreationDate.TrueEquals(obj.CreationDate) &&
                this.DownVoteCount.TrueEquals(obj.DownVoteCount) &&
                this.Downvoted.TrueEquals(obj.Downvoted) &&
                this.IsAccepted.TrueEquals(obj.IsAccepted) &&
                this.LastActivityDate.TrueEquals(obj.LastActivityDate) &&
                this.LastEditDate.TrueEquals(obj.LastEditDate) &&
                this.Link.TrueEqualsString(obj.Link) &&
                this.LockedDate.TrueEquals(obj.LockedDate) &&
                this.QuestionId.TrueEquals(obj.QuestionId) &&
                this.Score.TrueEquals(obj.Score) &&
                this.ShareLink.TrueEqualsString(obj.ShareLink) &&
                this.Tags.TrueEqualsString(obj.Tags) &&
                this.Title.TrueEqualsString(obj.Title) &&
                this.UpVoteCount.TrueEquals(obj.UpVoteCount) &&
                this.Upvoted.TrueEquals(obj.Upvoted);
        }

        public bool EqualsDynamic(dynamic obj)
        {
            return
                this.Accepted.TrueEquals((bool)obj.accepted) &&
                this.AnswerId.TrueEquals((int)obj.answer_id) &&
                this.Body.TrueEqualsString((string)obj.body) &&
                this.BodyMarkdown.TrueEqualsString((string)obj.body_markdown) &&
                this.CommentCount.TrueEquals((int)obj.comment_count) &&
                this.Comments.TrueEqualsListDynamic((IEnumerable<dynamic>)obj.comments) &&
                this.CommunityOwnedDate.TrueEquals((DateTime)obj.community_owned_date) &&
                this.CreationDate.TrueEquals((DateTime)obj.creation_date) &&
                this.DownVoteCount.TrueEquals((int)obj.down_vote_count) &&
                this.Downvoted.TrueEquals((bool)obj.downvoted) &&
                this.IsAccepted.TrueEquals((bool)obj.is_accepted) &&
                this.LastActivityDate.TrueEquals((DateTime)obj.last_activity_date) &&
                this.LastEditDate.TrueEquals((DateTime)obj.last_edit_date) &&
                this.Link.TrueEqualsString((string)obj.link) &&
                this.LockedDate.TrueEquals((DateTime)obj.locked_date) &&
                this.QuestionId.TrueEquals((int)obj.question_id) &&
                this.Score.TrueEquals((int)obj.score) &&
                this.ShareLink.TrueEqualsString((string)obj.share_link) &&
                this.Tags.TrueEqualsString((IEnumerable<string>)obj.tags) &&
                this.Title.TrueEqualsString((string)obj.title) &&
                this.UpVoteCount.TrueEquals((int)obj.up_vote_count) &&
                this.Upvoted.TrueEquals((bool)obj.upvoted);
        }
    }
}