using System;
using BoardSandbox.Core.Json;
using NUnit.Framework;

namespace BoardSandbox.Tests
{
    [TestFixture]
    public class JsonValueTests
    {
        [Test]
        public void ParsesScalars()
        {
            Assert.AreEqual(1.5, JsonValue.Parse("1.5").AsDouble());
            Assert.AreEqual(-7, JsonValue.Parse(" -7 ").AsInt());
            Assert.AreEqual(true, JsonValue.Parse("true").AsBool());
            Assert.IsTrue(JsonValue.Parse("null").IsNull);
            Assert.AreEqual("hi", JsonValue.Parse("\"hi\"").AsString());
        }

        [Test]
        public void WritesCanonicalCompactJson()
        {
            JsonValue value = JsonValue.Parse("{\"b\": 1, \"a\": [true, null, \"x\", {\"z\": 2, \"y\": []}]}");
            Assert.AreEqual("{\"a\":[true,null,\"x\",{\"y\":[],\"z\":2}],\"b\":1}", value.ToJson());
        }

        [TestCase("0.1", "0.1")]
        [TestCase("123456789012", "123456789012")]
        [TestCase("-0", "0")]
        [TestCase("1e21", "1E+21")]
        [TestCase("2.5e-3", "0.0025")]
        [TestCase("1E5", "100000")]
        public void FormatsNumbers(string input, string expected)
        {
            Assert.AreEqual(expected, JsonValue.Parse(input).ToJson());
        }

        [Test]
        public void EscapesAndUnescapesStrings()
        {
            JsonValue value = JsonValue.Parse("\"a\\nb\\t\\\"q\\\" \\\\ \\/ \\u0001 \\u00e9 \\ud83d\\ude00\"");
            string text = value.AsString();
            Assert.AreEqual("a\nb\t\"q\" \\ / \u0001 \u00e9 \U0001F600", text);
            Assert.AreEqual("\"a\\nb\\t\\\"q\\\" \\\\ / \\u0001 \u00e9 \U0001F600\"", value.ToJson());
            Assert.AreEqual(text, JsonValue.Parse(value.ToJson()).AsString());
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{")]
        [TestCase("[1,]")]
        [TestCase("{\"a\":1,}")]
        [TestCase("01")]
        [TestCase("+1")]
        [TestCase(".5")]
        [TestCase("1.")]
        [TestCase("1e")]
        [TestCase("NaN")]
        [TestCase("1e999")]
        [TestCase("\"unterminated")]
        [TestCase("\"bad \\x escape\"")]
        [TestCase("\"\\u12\"")]
        [TestCase("tru")]
        [TestCase("{'a':1}")]
        [TestCase("[1] [2]")]
        [TestCase("{\"a\" 1}")]
        [TestCase("\"tab\there\"")]
        public void RejectsMalformedInput(string text)
        {
            Assert.Throws<JsonParseException>(() => JsonValue.Parse(text));
            Assert.IsFalse(JsonValue.TryParse(text, out JsonValue value, out string error));
            Assert.IsNull(value);
            Assert.IsNotNull(error);
        }

        [Test]
        public void ErrorsCarryTheOffset()
        {
            JsonParseException e = Assert.Throws<JsonParseException>(() => JsonValue.Parse("[1, 2, x]"));
            Assert.AreEqual(7, e.Offset);
        }

        [Test]
        public void EnforcesTheDepthLimit()
        {
            string ok = new string('[', JsonValue.MaxDepth) + new string(']', JsonValue.MaxDepth);
            string tooDeep = new string('[', JsonValue.MaxDepth + 1) + new string(']', JsonValue.MaxDepth + 1);
            Assert.IsTrue(JsonValue.Parse(ok).IsArray);
            Assert.Throws<JsonParseException>(() => JsonValue.Parse(tooDeep));
        }

        [Test]
        public void MissingKeysAndIndexesGiveNull()
        {
            JsonValue value = JsonValue.Parse("{\"a\":{\"b\":[1,2]}}");
            Assert.AreEqual(2, value["a"]["b"][1].AsInt());
            Assert.IsTrue(value["x"]["y"][3].IsNull);
            Assert.AreEqual(9, value["a"]["b"][5].AsInt(9));
            Assert.AreEqual("none", value["a"]["b"].AsString("none"));
            Assert.IsFalse(value.Has("x"));
            Assert.IsTrue(value.Has("a"));
        }

        [Test]
        public void EmptyArrayCountsAsAnObject()
        {
            JsonValue empty = JsonValue.Parse("[]");
            Assert.IsTrue(empty.IsObjectLike);
            Assert.IsFalse(empty.IsObject);
            Assert.IsFalse(JsonValue.Parse("[1]").IsObjectLike);
            Assert.IsTrue(JsonValue.Parse("{}").IsObjectLike);
            int members = 0;
            foreach (var _ in empty.Members)
                members++;
            Assert.AreEqual(0, members);
        }

        [Test]
        public void BuildsValues()
        {
            JsonValue value = JsonValue.NewObject().Set("name", "x").Set("n", 3).Set("on", true).Set("list", JsonValue.NewArray().Add(1).Add("two").Add(false));
            Assert.AreEqual("{\"list\":[1,\"two\",false],\"n\":3,\"name\":\"x\",\"on\":true}", value.ToJson());
            Assert.IsTrue(value.Remove("n"));
            Assert.IsFalse(value.Has("n"));
        }

        [Test]
        public void SingletonsAreImmutable()
        {
            Assert.Throws<InvalidOperationException>(() => JsonValue.Null.Set("a", 1));
            Assert.Throws<InvalidOperationException>(() => JsonValue.True.Add(1));
            Assert.Throws<InvalidOperationException>(() => JsonValue.NewArray().Set("a", 1));
        }

        [Test]
        public void CannotWriteNonFiniteNumbers()
        {
            Assert.Throws<InvalidOperationException>(() => JsonValue.Of(double.NaN).ToJson());
            Assert.Throws<InvalidOperationException>(() => JsonValue.Of(double.PositiveInfinity).ToJson());
        }

        [Test]
        public void DeepCloneIsIndependent()
        {
            JsonValue original = JsonValue.Parse("{\"a\":[1,{\"b\":2}]}");
            JsonValue copy = original.DeepClone();
            Assert.IsTrue(original.StructurallyEquals(copy));
            copy["a"][1].Set("b", 3);
            Assert.AreEqual(2, original["a"][1]["b"].AsInt());
            Assert.IsFalse(original.StructurallyEquals(copy));
        }

        [Test]
        public void PrettyPrintingParsesBack()
        {
            JsonValue value = JsonValue.Parse("{\"a\":[1,2,{\"b\":[]}],\"c\":\"d\"}");
            string pretty = value.ToJson(true);
            StringAssert.Contains("\n", pretty);
            Assert.IsTrue(value.StructurallyEquals(JsonValue.Parse(pretty)));
        }

        [Test]
        public void AsIntSaturatesToTheFallback()
        {
            Assert.AreEqual(5, JsonValue.Parse("1e12").AsInt(5));
            Assert.AreEqual(1000000000000L, JsonValue.Parse("1e12").AsLong());
        }
    }
}
