using System;
using System.Collections.Generic;
using System.Linq;
using MemoryPack;
using MessagePack;
using Nino.Core;
using ProtoBuf;

#nullable disable
namespace Nino.Benchmark.Workloads;

// Two payloads of the common serializer benchmarks, so these numbers can be set beside theirs.

public enum MediaPlayer { Java, Flash }

public enum ImageSize { Small, Large }

/// <summary>jvm-serializers' Media (tpc/schema/media.proto), the payload every language's serializers are compared on.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Media
{
    [Key(0)] [ProtoMember(1)] public string Uri;
    [Key(1)] [ProtoMember(2)] public string Title;
    [Key(2)] [ProtoMember(3)] public int Width;
    [Key(3)] [ProtoMember(4)] public int Height;
    [Key(4)] [ProtoMember(5)] public string Format;
    [Key(5)] [ProtoMember(6)] public long Duration;
    [Key(6)] [ProtoMember(7)] public long Size;
    [Key(7)] [ProtoMember(8)] public int Bitrate;
    [Key(8)] [ProtoMember(9)] public List<string> Persons;
    [Key(9)] [ProtoMember(10)] public MediaPlayer Player;
    [Key(10)] [ProtoMember(11)] public string Copyright;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Image
{
    [Key(0)] [ProtoMember(1)] public string Uri;
    [Key(1)] [ProtoMember(2)] public string Title;
    [Key(2)] [ProtoMember(3)] public int Width;
    [Key(3)] [ProtoMember(4)] public int Height;
    [Key(4)] [ProtoMember(5)] public ImageSize Size;
}

/// <summary>jvm-serializers' MediaContent with its standard values (StdMediaSerializer's test data).</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class MediaContent
{
    [Key(0)] [ProtoMember(1)] public List<Image> Images;
    [Key(1)] [ProtoMember(2)] public Media Media;

    public static MediaContent Create(Random _) => new()
    {
        Media = new Media
        {
            Uri = "http://javaone.com/keynote.mpg", Title = "Javaone Keynote", Width = 640, Height = 480,
            Format = "video/mpg4", Duration = 18_000_000, Size = 58_982_400, Bitrate = 262_144,
            Persons = ["Bill Gates", "Steve Jobs"], Player = MediaPlayer.Java, Copyright = null,
        },
        Images =
        [
            new Image { Uri = "http://javaone.com/keynote_large.jpg", Title = "Javaone Keynote", Width = 1024, Height = 768, Size = ImageSize.Large },
            new Image { Uri = "http://javaone.com/keynote_small.jpg", Title = "Javaone Keynote", Width = 320, Height = 240, Size = ImageSize.Small },
        ],
    };
}

public enum UserType { Unregistered, Registered, Moderator, DoesNotExist }

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class BadgeCount
{
    [Key(0)] [ProtoMember(1)] public int Gold;
    [Key(1)] [ProtoMember(2)] public int Silver;
    [Key(2)] [ProtoMember(3)] public int Bronze;
}

/// <summary>The Stack Exchange API's shallow_user, as Jil's benchmark models (and MessagePack-CSharp's) carry it.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class ShallowUser
{
    [Key(0)] [ProtoMember(1)] public int? UserId;
    [Key(1)] [ProtoMember(2)] public string DisplayName;
    [Key(2)] [ProtoMember(3)] public int? Reputation;
    [Key(3)] [ProtoMember(4)] public UserType UserType;
    [Key(4)] [ProtoMember(5)] public string ProfileImage;
    [Key(5)] [ProtoMember(6)] public string Link;
    [Key(6)] [ProtoMember(7)] public int? AcceptRate;
    [Key(7)] [ProtoMember(8)] public BadgeCount BadgeCounts;

    public static ShallowUser Create(Random r)
    {
        int id = r.Next(1, 9_000_000);
        return new ShallowUser
        {
            UserId = id, DisplayName = Data.Name(r), Reputation = r.Next(1, 400_000), UserType = UserType.Registered,
            ProfileImage = $"https://i.sstatic.net/{id:x8}.jpg?s=256", Link = $"https://stackoverflow.com/users/{id}",
            AcceptRate = r.Next(3) == 0 ? null : r.Next(40, 100),
            BadgeCounts = new BadgeCount { Gold = r.Next(50), Silver = r.Next(500), Bronze = r.Next(900) },
        };
    }
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Comment
{
    [Key(0)] [ProtoMember(1)] public int CommentId;
    [Key(1)] [ProtoMember(2)] public int PostId;
    [Key(2)] [ProtoMember(3)] public DateTime CreationDate;
    [Key(3)] [ProtoMember(4)] public int Score;
    [Key(4)] [ProtoMember(5)] public bool Edited;
    [Key(5)] [ProtoMember(6)] public string Body;
    [Key(6)] [ProtoMember(7)] public ShallowUser Owner;
    [Key(7)] [ProtoMember(8)] public ShallowUser ReplyToUser;
    [Key(8)] [ProtoMember(9)] public string Link;
}

[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Answer
{
    [Key(0)] [ProtoMember(1)] public int AnswerId;
    [Key(1)] [ProtoMember(2)] public int QuestionId;
    [Key(2)] [ProtoMember(3)] public DateTime CreationDate;
    [Key(3)] [ProtoMember(4)] public DateTime LastActivityDate;
    [Key(4)] [ProtoMember(5)] public DateTime? LastEditDate;
    [Key(5)] [ProtoMember(6)] public int Score;
    [Key(6)] [ProtoMember(7)] public bool IsAccepted;
    [Key(7)] [ProtoMember(8)] public string Body;
    [Key(8)] [ProtoMember(9)] public ShallowUser Owner;
    [Key(9)] [ProtoMember(10)] public List<Comment> Comments;
    [Key(10)] [ProtoMember(11)] public int UpVoteCount;
    [Key(11)] [ProtoMember(12)] public int DownVoteCount;
    [Key(12)] [ProtoMember(13)] public string Link;
}

/// <summary>The Stack Exchange API's question, the fields Jil's benchmark model has that a page of questions fills.</summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class Question
{
    [Key(0)] [ProtoMember(1)] public int QuestionId;
    [Key(1)] [ProtoMember(2)] public DateTime CreationDate;
    [Key(2)] [ProtoMember(3)] public DateTime LastActivityDate;
    [Key(3)] [ProtoMember(4)] public DateTime? LastEditDate;
    [Key(4)] [ProtoMember(5)] public DateTime? LockedDate;
    [Key(5)] [ProtoMember(6)] public DateTime? ClosedDate;
    [Key(6)] [ProtoMember(7)] public string ClosedReason;
    [Key(7)] [ProtoMember(8)] public int AnswerCount;
    [Key(8)] [ProtoMember(9)] public int? AcceptedAnswerId;
    [Key(9)] [ProtoMember(10)] public int? BountyAmount;
    [Key(10)] [ProtoMember(11)] public DateTime? BountyClosesDate;
    [Key(11)] [ProtoMember(12)] public string Title;
    [Key(12)] [ProtoMember(13)] public string Body;
    [Key(13)] [ProtoMember(14)] public List<string> Tags;
    [Key(14)] [ProtoMember(15)] public int UpVoteCount;
    [Key(15)] [ProtoMember(16)] public int DownVoteCount;
    [Key(16)] [ProtoMember(17)] public int FavoriteCount;
    [Key(17)] [ProtoMember(18)] public int ViewCount;
    [Key(18)] [ProtoMember(19)] public int Score;
    [Key(19)] [ProtoMember(20)] public bool IsAnswered;
    [Key(20)] [ProtoMember(21)] public ShallowUser Owner;
    [Key(21)] [ProtoMember(22)] public List<Answer> Answers;
    [Key(22)] [ProtoMember(23)] public List<Comment> Comments;
    [Key(23)] [ProtoMember(24)] public string Link;
}

/// <summary>
/// A page of 20 questions with their answers and comments in the Stack Exchange API's wrapper, the models Jil's
/// benchmark suite brought to .NET and MessagePack-CSharp's and MemoryPack's benchmarks reuse.
/// </summary>
[NinoType, MemoryPackable, MessagePackObject, ProtoContract, CompatibilityLevel(CompatibilityLevel.Level300)]
public partial class StackOverflowPage
{
    [Key(0)] [ProtoMember(1)] public List<Question> Items;
    [Key(1)] [ProtoMember(2)] public bool HasMore;
    [Key(2)] [ProtoMember(3)] public int QuotaMax;
    [Key(3)] [ProtoMember(4)] public int QuotaRemaining;
    [Key(4)] [ProtoMember(5)] public int Page;
    [Key(5)] [ProtoMember(6)] public int PageSize;
    [Key(6)] [ProtoMember(7)] public int Total;

    private static readonly string[] TagNames = ["c#", ".net", "serialization", "performance", "asp.net-core",
        "unity3d", "protobuf", "json", "messagepack", "generics", "linq", "async-await"];

    private static List<Comment> Comments(Random r, int post, int count) => Enumerable.Range(0, count).Select(_ =>
    {
        int id = r.Next(100_000_000, 140_000_000);
        return new Comment
        {
            CommentId = id, PostId = post, CreationDate = Data.Epoch.AddMinutes(r.Next(200_000)), Score = r.Next(12),
            Edited = r.Next(5) == 0, Body = Data.Html(r, 1), Owner = ShallowUser.Create(r),
            ReplyToUser = r.Next(3) == 0 ? ShallowUser.Create(r) : null,
            Link = $"https://stackoverflow.com/questions/{post}#comment{id}_{post}",
        };
    }).ToList();

    public static StackOverflowPage Create(Random r) => new()
    {
        HasMore = true, QuotaMax = 10_000, QuotaRemaining = 9_312, Page = 1, PageSize = 20, Total = 24_118_563,
        Items = Enumerable.Range(0, 20).Select(_ =>
        {
            int id = r.Next(70_000_000, 79_000_000);
            var created = Data.Epoch.AddMinutes(r.Next(500_000));
            var answers = Enumerable.Range(0, r.Next(1, 5)).Select(_ =>
            {
                int answer = r.Next(70_000_000, 79_000_000);
                return new Answer
                {
                    AnswerId = answer, QuestionId = id, CreationDate = created.AddMinutes(r.Next(1, 600)),
                    LastActivityDate = created.AddMinutes(r.Next(600, 6_000)),
                    LastEditDate = r.Next(2) == 0 ? created.AddMinutes(r.Next(600, 6_000)) : null, Score = r.Next(-2, 90),
                    IsAccepted = r.Next(4) == 0, Body = Data.Html(r, r.Next(3, 9)), Owner = ShallowUser.Create(r),
                    Comments = Comments(r, answer, r.Next(1, 4)), UpVoteCount = r.Next(100), DownVoteCount = r.Next(4),
                    Link = $"https://stackoverflow.com/a/{answer}",
                };
            }).ToList();
            bool closed = r.Next(10) == 0;
            return new Question
            {
                QuestionId = id, CreationDate = created, LastActivityDate = created.AddMinutes(r.Next(10, 9_000)),
                LastEditDate = r.Next(2) == 0 ? created.AddMinutes(r.Next(5, 600)) : null, LockedDate = null,
                ClosedDate = closed ? created.AddDays(1) : null, ClosedReason = closed ? "Duplicate" : null,
                AnswerCount = answers.Count, AcceptedAnswerId = answers.FirstOrDefault(a => a.IsAccepted)?.AnswerId,
                BountyAmount = r.Next(8) == 0 ? 50 * r.Next(1, 10) : null, BountyClosesDate = null,
                Title = "How do I serialize a " + TagNames[r.Next(TagNames.Length)] + " type without allocating?",
                Body = Data.Html(r, r.Next(4, 12)),
                Tags = Enumerable.Range(0, r.Next(1, 5)).Select(_ => TagNames[r.Next(TagNames.Length)]).Distinct().ToList(),
                UpVoteCount = r.Next(200), DownVoteCount = r.Next(10), FavoriteCount = r.Next(40), ViewCount = r.Next(90_000),
                Score = r.Next(-3, 190), IsAnswered = answers.Count > 0, Owner = ShallowUser.Create(r), Answers = answers,
                Comments = Comments(r, id, r.Next(1, 4)), Link = $"https://stackoverflow.com/questions/{id}",
            };
        }).ToList(),
    };
}
