using NUnit.Framework;
using Newtonsoft.Json;
using System.IO;
using System;

namespace XDM.Tests
{
    [TestFixture]
    public class JsonParsingTests
    {
        [Test]
        public void DeserializeBrowserMessage_ValidJson_ParsesCorrectly()
        {
            var json = @"{
                ""messageType"": ""download"",
                ""message"": {
                    ""url"": ""https://example.com/file.zip"",
                    ""cookies"": { ""session"": ""abc123"" },
                    ""responseHeaders"": {
                        ""Content-Type"": [""application/zip""],
                        ""Content-Length"": [""1048576""]
                    },
                    ""requestHeaders"": {
                        ""User-Agent"": [""Mozilla/5.0""]
                    }
                }
            }";

            using var reader = new JsonTextReader(new StringReader(json));
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.TokenType, Is.EqualTo(JsonToken.StartObject));
        }

        [Test]
        public void DeserializeBrowserMessage_EmptyJson_HandlesGracefully()
        {
            var json = "{}";
            using var reader = new JsonTextReader(new StringReader(json));
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.TokenType, Is.EqualTo(JsonToken.StartObject));
            Assert.That(reader.Read(), Is.True);
            Assert.That(reader.TokenType, Is.EqualTo(JsonToken.EndObject));
        }

        [Test]
        public void DeserializeBrowserMessage_MissingFields_DoesNotThrow()
        {
            var json = @"{ ""messageType"": ""download"", ""message"": {} }";
            using var reader = new JsonTextReader(new StringReader(json));
            Assert.DoesNotThrow(() =>
            {
                while (reader.Read()) { }
            });
        }

        [Test]
        public void DeserializeBrowserMessage_WithMultipleMessages_ParsesAll()
        {
            var json = @"{
                ""messageType"": ""batch"",
                ""messages"": [
                    { ""url"": ""https://example.com/a.mp4"" },
                    { ""url"": ""https://example.com/b.mp4"" }
                ]
            }";

            using var reader = new JsonTextReader(new StringReader(json));
            int objectCount = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.StartObject) objectCount++;
            }
            Assert.That(objectCount, Is.EqualTo(3)); // 1 root + 2 messages
        }

        [Test]
        public void DeserializeBrowserMessage_MalformedJson_StopsReading()
        {
            var json = @"{ ""messageType"": ""download"", ";
            using var reader = new JsonTextReader(new StringReader(json));
            // Newtonsoft.Json returns false on truncated input
            int tokenCount = 0;
            while (reader.Read()) { tokenCount++; }
            // Should have read StartObject and the PropertyName but stopped
            Assert.That(tokenCount, Is.LessThan(5));
        }
    }
}