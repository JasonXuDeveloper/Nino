/* this is generated by nino */
using System.Runtime.CompilerServices;

namespace Nino.Benchmark.Models
{
    public partial class Comment
    {
        public static Comment.SerializationHelper NinoSerializationHelper = new Comment.SerializationHelper();
        public unsafe class SerializationHelper: Nino.Serialization.NinoWrapperBase<Comment>
        {
            #region NINO_CODEGEN
            public SerializationHelper()
            {

            }
            
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override void Serialize(Comment value, ref Nino.Serialization.Writer writer)
            {
                if(value == null)
                {
                    writer.Write(false);
                    return;
                }
                writer.Write(true);
                writer.Write(value.CommentId);
                writer.Write(value.PostId);
                writer.Write(value.CreationDate);
                writer.Write(value.Score);
                writer.Write(value.Edited);
                writer.Write(value.Body);
                writer.Write(value.Link);
                writer.Write(value.BodyMarkdown);
                writer.Write(value.Upvoted);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override Comment Deserialize(Nino.Serialization.Reader reader)
            {
                if(!reader.ReadBool())
                    return null;
                Comment value = new Comment();
                value.CommentId = reader.Read<System.Int32>(sizeof(System.Int32));
                value.PostId = reader.Read<System.Int32>(sizeof(System.Int32));
                value.CreationDate = reader.Read<System.DateTime>(sizeof(System.DateTime));
                value.Score = reader.Read<System.Int32>(sizeof(System.Int32));
                value.Edited = reader.Read<System.Boolean>(sizeof(System.Boolean));
                value.Body = reader.ReadString();
                value.Link = reader.ReadString();
                value.BodyMarkdown = reader.ReadString();
                value.Upvoted = reader.Read<System.Boolean>(sizeof(System.Boolean));
                return value;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public override int GetSize(Comment value)
            {
                if(value == null)
                {
                    return 1;
                }
                int ret = 1;
                ret += Nino.Serialization.Serializer.GetSize(value.CommentId);
                ret += Nino.Serialization.Serializer.GetSize(value.PostId);
                ret += Nino.Serialization.Serializer.GetSize(value.CreationDate);
                ret += Nino.Serialization.Serializer.GetSize(value.Score);
                ret += Nino.Serialization.Serializer.GetSize(value.Edited);
                ret += Nino.Serialization.Serializer.GetSize(value.Body);
                ret += Nino.Serialization.Serializer.GetSize(value.Link);
                ret += Nino.Serialization.Serializer.GetSize(value.BodyMarkdown);
                ret += Nino.Serialization.Serializer.GetSize(value.Upvoted);
                return ret;
            }
            #endregion
        }
    }
}