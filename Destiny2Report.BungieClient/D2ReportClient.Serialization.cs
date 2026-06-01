using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace D2Report.BungieClient;

public partial class D2ReportClient
{
    static partial void UpdateJsonSerializerSettings(JsonSerializerSettings settings)
    {
        settings.ContractResolver = new BungieApiContractResolver();
        settings.Converters.Add(new BungieInt32Converter());
    }

    private sealed class BungieApiContractResolver : DefaultContractResolver
    {
        protected override JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            if (property.Required is Required.Always or Required.DisallowNull)
            {
                property.Required = Required.Default;
            }

            return property;
        }
    }

    private sealed class BungieInt32Converter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            var targetType = Nullable.GetUnderlyingType(objectType) ?? objectType;
            return targetType == typeof(int);
        }

        public override object? ReadJson(
            JsonReader reader,
            Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType is JsonToken.Null or JsonToken.Undefined)
            {
                return Nullable.GetUnderlyingType(objectType) is null ? 0 : null;
            }

            var value = JToken.Load(reader);
            if (value.Type == JTokenType.Integer)
            {
                return ToInt32(value.Value<long>());
            }

            if (value.Type == JTokenType.String
                && long.TryParse(value.Value<string>(), out var parsed))
            {
                return ToInt32(parsed);
            }

            return value.ToObject<int>();
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            writer.WriteValue(value);
        }

        private static int ToInt32(long value)
        {
            return value is >= int.MinValue and <= int.MaxValue
                ? (int)value
                : unchecked((int)(uint)value);
        }
    }
}
