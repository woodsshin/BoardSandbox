using System.Collections.Generic;

namespace BoardSandbox.Core.Json
{
    public static class JsonMerge
    {
        // A copy of the base with the overlay laid over it: objects merge member by member, anything else is replaced.
        public static JsonValue Deep(JsonValue baseValue, JsonValue overlay)
        {
            if (overlay == null)
                return baseValue == null ? JsonValue.Null : baseValue.DeepClone();
            if (baseValue == null || !baseValue.IsObject || !overlay.IsObject)
                return overlay.DeepClone();

            JsonValue result = baseValue.DeepClone();
            foreach (KeyValuePair<string, JsonValue> pair in overlay.Members)
            {
                JsonValue current = result[pair.Key];
                result.Set(pair.Key, current.IsObject && pair.Value.IsObject ? Deep(current, pair.Value) : pair.Value.DeepClone());
            }
            return result;
        }
    }
}
