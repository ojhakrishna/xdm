using NUnit.Framework;
using System.Text.Json;
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

            using var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(doc.RootElement.GetProperty("messageType").GetString(), Is.EqualTo("download"));
        }

        [Test]
        public void DeserializeBrowserMessage_EmptyJson_HandlesGracefully()
        {
            var json = "{}";
            using var doc = JsonDocument.Parse(json);
            Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(doc.RootElement.EnumerateObject().MoveNext(), Is.False);
        }

        [Test]
        public void DeserializeBrowserMessage_MissingFields_DoesNotThrow()
        {
            var json = @"{ ""messageType"": ""download"", ""message"": {} }";
            Assert.DoesNotThrow(() =>
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                // Access properties without throwing
                _ = root.GetProperty("messageType").GetString();
                _ = root.GetProperty("message");
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

            using var doc = JsonDocument.Parse(json);
            var messages = doc.RootElement.GetProperty("messages");
            Assert.That(messages.GetArrayLength(), Is.EqualTo(2));
        }

        [Test]
        public void DeserializeBrowserMessage_MalformedJson_ThrowsException()
        {
            var json = @"{ ""messageType"": ""download"", ""url"": ";
            // System.Text.Json throws JsonException on malformed input
            Assert.Catch<Exception>(() =>
            {
                using var doc = JsonDocument.Parse(json);
            });
        }
    }
}