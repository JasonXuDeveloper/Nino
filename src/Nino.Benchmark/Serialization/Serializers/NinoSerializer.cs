using Nino.Benchmark.Models;

namespace Nino.Benchmark.Serializers
{
    public class NinoSerializer : SerializerBase
    {
        public override T Deserialize<T>(byte[] input)
        {
            Deserializer.Deserialize(input, out T ret);
            return ret;
        }

        public override byte[] SerializeAccessToken(AccessToken token)
        {
            return token.Serialize();
        }

        public override AccessToken DeserializeAccessToken(byte[] data)
        {
            Deserializer.Deserialize(data, out AccessToken token);
            return token;
        }

        public override byte[] SerializeAccountMerge(AccountMerge merge)
        {
            return merge.Serialize();
        }

        public override AccountMerge DeserializeAccountMerge(byte[] data)
        {
            Deserializer.Deserialize(data, out AccountMerge merge);
            return merge;
        }

        public override byte[] SerializeAnswer(Answer answer)
        {
            return answer.Serialize();
        }

        public override Answer DeserializeAnswer(byte[] data)
        {
            Deserializer.Deserialize(data, out Answer answer);
            return answer;
        }

        public override byte[] SerializeBadge(Badge badge)
        {
            return badge.Serialize();
        }

        public override Badge DeserializeBadge(byte[] data)
        {
            Deserializer.Deserialize(data, out Badge badge);
            return badge;
        }

        public override byte[] SerializeComment(Comment comment)
        {
            return comment.Serialize();
        }

        public override Comment DeserializeComment(byte[] data)
        {
            Deserializer.Deserialize(data, out Comment comment);
            return comment;
        }

        public override byte[] SerializeData(Data data)
        {
            return data.Serialize();
        }

        public override Data DeserializeData(byte[] data)
        {
            Deserializer.Deserialize(data, out Data dt);
            return dt;
        }

        public override byte[] SerializeString(string str)
        {
            return str.Serialize();
        }

        public override string DeserializeString(byte[] data)
        {
            Deserializer.Deserialize(data, out string str);
            return str;
        }

        public override byte[] SerializeNestedData(NestedData data)
        {
            return data.Serialize();
        }

        public override NestedData DeserializeNestedData(byte[] data)
        {
            Deserializer.Deserialize(data, out NestedData dt);
            return dt;
        }

        public override byte[] Serialize<T>(T input)
        {
            return input.Serialize();
        }

        public override string ToString()
        {
            return "Nino";
        }
    }
}