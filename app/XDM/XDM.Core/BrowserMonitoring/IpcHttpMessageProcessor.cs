using System;
using System.Text;
using System.Net;
using System.Linq;
using System.Text.Json;
using System.IO;
using XDM.Core.Util;
using XDM.Core.HttpServer;
using System.Threading;
using TraceLog;
using Translations;
using System.Collections.Generic;

namespace XDM.Core.BrowserMonitoring
{
    public class IpcHttpMessageProcessor
    {
        private NanoServer server;
        private static string[] blockedHeaders = { "accept", "if", "authorization", "proxy", "connection", "expect", "TE",
            "upgrade", "range", "cookie", "transfer-encoding", "content-type", "content-length","content-encoding" };

        // Newtonsoft.Json was case-insensitive by default; System.Text.Json is not.
        // The browser extension sends camelCase JSON keys but C# models use PascalCase.
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public IpcHttpMessageProcessor()
        {
            server = new NanoServer(IPAddress.Loopback, 8597);
            server.RequestReceived += (sender, args) =>
            {
                HandleRequest(args.RequestContext);
            };
        }

        public void Run()
        {
            new Thread(() =>
            {
                try
                {
                    server.Start();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex.ToString());
                    ApplicationContext.Application.ShowMessageBox(null, TextResource.GetText("MSG_ALREADY_RUNNING"));
                }
            }).Start();
        }

        public void HandleRequest(RequestContext context)
        {
            try
            {
                switch (context.RequestPath)
                {
                    case "/sync":
                        break;
                    case "/download":
                        OnDownloadMessage(context);
                        break;
                    case "/media":
                        OnMediaMessage(context);
                        break;
                    case "/tab-update":
                        OnTabUpdateMessage(context);
                        break;
                    case "/vid":
                        OnVideoDownloadMessage(context);
                        break;
                    case "/clear":
                        ApplicationContext.VideoTracker.ClearVideoList();
                        break;
                    case "/link":
                        OnBatchMessage(context);
                        break;
                    case "/args":
                        OnArgsMessage(context);
                        break;
                    default:
                        throw new ArgumentException("Unsupported request: " + context.RequestPath);
                }
                OnSyncMessage(context);
            }
            catch (Exception ex)
            {
                Log.Debug(ex.ToString());
                throw;
            }
        }

        private void OnArgsMessage(RequestContext context)
        {
            var args = JsonSerializer.Deserialize<List<string>>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (args == null || args.Count == 0)
            {
                return;
            }
            ArgsProcessor.Process(args);
        }

        private void OnVideoDownloadMessage(RequestContext context)
        {
            var msg = JsonSerializer.Deserialize<ExtensionData>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (msg == null)
            {
                return;
            }
            ApplicationContext.VideoTracker.AddVideoDownload(msg.Vid);
        }

        private void OnTabUpdateMessage(RequestContext context)
        {
            var msg = JsonSerializer.Deserialize<ExtensionData>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (msg == null)
            {
                return;
            }
            ApplicationContext.VideoTracker.UpdateMediaTitle(msg.TabUrl, msg.TabTitle);
        }

        private void OnDownloadMessage(RequestContext context)
        {
            var msg = JsonSerializer.Deserialize<ExtensionData>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (msg == null)
            {
                return;
            }

            // Server-side filtering: block thumbnail MIME types
            if (IsBlockedMimeType(msg.ResponseHeaders))
            {
                Log.Debug($"Blocked download (MIME type): {msg.Url}");
                return;
            }

            // Server-side filtering: block known CDN thumbnail URL patterns
            if (IsBlockedUrlPattern(msg.Url))
            {
                Log.Debug($"Blocked download (URL pattern): {msg.Url}");
                return;
            }

            // Server-side filtering: enforce minimum download size
            if (IsBelowMinimumSize(msg.ResponseHeaders))
            {
                Log.Debug($"Blocked download (below min size): {msg.Url}");
                return;
            }

            var dmsg = new Message();
            dmsg.Url = msg.Url;
            dmsg.RequestMethod = msg.Method;
            dmsg.RequestHeaders = msg.RequestHeaders;
            dmsg.ResponseHeaders = msg.ResponseHeaders;
            dmsg.Cookies = msg.Cookie;
            dmsg.File = FileHelper.SanitizeFileName(msg.File)!;
            dmsg.TabUrl = msg.TabUrl;
            dmsg.TabId = msg.TabId;
            RemoveBlockedHeaders(dmsg);
            ApplicationContext.CoreService.AddDownload(dmsg);
        }

        private void OnMediaMessage(RequestContext context)
        {
            var msg = JsonSerializer.Deserialize<ExtensionData>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (msg == null)
            {
                return;
            }
            var dmsg = new Message();
            dmsg.Url = msg.Url;
            dmsg.RequestMethod = msg.Method;
            dmsg.RequestHeaders = msg.RequestHeaders;
            dmsg.ResponseHeaders = msg.ResponseHeaders;
            dmsg.Cookies = msg.Cookie;
            dmsg.File = FileHelper.SanitizeFileName(msg.File)!;
            dmsg.TabUrl = msg.TabUrl;
            dmsg.TabId = msg.TabId;
            RemoveBlockedHeaders(dmsg);
            VideoUrlHelper.ProcessMediaMessage(dmsg);
        }

        private void OnBatchMessage(RequestContext context)
        {
            var msgArr = JsonSerializer.Deserialize<ExtensionData[]>(Encoding.UTF8.GetString(context.RequestBody!), _jsonOptions);
            if (msgArr == null)
            {
                return;
            }
            ApplicationContext.CoreService.AddBatchLinks(msgArr.Select(msg =>
            {
                var dmsg = new Message();
                dmsg.Url = msg.Url;
                dmsg.RequestMethod = msg.Method;
                dmsg.RequestHeaders = msg.RequestHeaders;
                dmsg.ResponseHeaders = msg.ResponseHeaders;
                dmsg.Cookies = msg.Cookie;
                dmsg.File = FileHelper.SanitizeFileName(msg.File)!;
                dmsg.TabUrl = msg.TabUrl;
                dmsg.TabId = msg.TabId;
                RemoveBlockedHeaders(dmsg);
                return dmsg;
            }).ToList());
        }

        //public void HandleRequest2(RequestContext context)
        //{
        //    if (context.RequestPath == "/204")
        //    {
        //        context.ResponseStatus = new ResponseStatus
        //        {
        //            StatusCode = 204,
        //            StatusMessage = "No Content"
        //        };
        //        context.AddResponseHeader("Cache-Control", "max-age=0, no-cache, must-revalidate");
        //        context.SendResponse();
        //        return;
        //    }

        //    try
        //    {
        //        switch (context.RequestPath)
        //        {
        //            case "/download":
        //                {
        //                    var text = Encoding.UTF8.GetString(context.RequestBody!);
        //                    Log.Debug(text);
        //                    var message = Message.ParseMessage(text);
        //                    if (!(Helpers.IsBlockedHost(message.Url) || Helpers.IsCompressedJSorCSS(message.Url)))
        //                    {
        //                        ApplicationContext.CoreService.AddDownload(message);
        //                    }
        //                    break;
        //                }
        //            case "/video":
        //                {
        //                    var text = Encoding.UTF8.GetString(context.RequestBody!);
        //                    Log.Debug(text);
        //                    var message2 = Message.ParseMessage(Encoding.UTF8.GetString(context.RequestBody!));
        //                    var contentType = message2.GetResponseHeaderFirstValue("Content-Type")?.ToLowerInvariant() ?? string.Empty;
        //                    if (VideoUrlHelper.IsHLS(contentType))
        //                    {
        //                        VideoUrlHelper.ProcessHLSVideo(message2);
        //                    }
        //                    if (VideoUrlHelper.IsDASH(contentType))
        //                    {
        //                        VideoUrlHelper.ProcessDashVideo(message2);
        //                    }
        //                    if (!VideoUrlHelper.ProcessYtDashSegment(message2))
        //                    {
        //                        if (contentType != null && !(contentType.Contains("f4f") ||
        //                            contentType.Contains("m4s") ||
        //                            contentType.Contains("mp2t") || message2.Url.Contains("abst") ||
        //                            message2.Url.Contains("f4x") || message2.Url.Contains(".fbcdn")
        //                            || message2.Url.Contains("http://127.0.0.1:9614")))
        //                        {
        //                            VideoUrlHelper.ProcessNormalVideo(message2);
        //                        }
        //                    }
        //                    break;
        //                }
        //            case "/links":
        //                {
        //                    var text = Encoding.UTF8.GetString(context.RequestBody!);
        //                    Log.Debug(text);
        //                    var arr = text.Split(new string[] { "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        //                    ApplicationContext.CoreService.AddBatchLinks(arr.Select(str => Message.ParseMessage(str.Trim())).ToList());
        //                    break;
        //                }
        //            case "/item":
        //                {
        //                    foreach (var item in Encoding.UTF8.GetString(context.RequestBody!).Split(new char[] { '\r', '\n' }))
        //                    {
        //                        ApplicationContext.VideoTracker.AddVideoDownload(item);
        //                    }
        //                    break;
        //                }
        //            case "/clear":
        //                ApplicationContext.VideoTracker.ClearVideoList();
        //                break;
        //        }
        //    }
        //    finally
        //    {
        //        SendSyncResponse(context);
        //    }
        //}

        private void OnSyncMessage(RequestContext context)
        {
            var json = CreateConfigJson();
            context.ResponseStatus = new ResponseStatus
            {
                StatusCode = 200,
                StatusMessage = "OK"
            };
            context.AddResponseHeader("Content-Type", "application/json");
            context.AddResponseHeader("Cache-Control", "max-age=0, no-cache, must-revalidate");
            context.ResponseBody = Encoding.UTF8.GetBytes(json);
            context.SendResponse();
        }

        private string? CreateConfigJson()
        {
            try
            {
                using var ms = new MemoryStream();
                using var writer = new Utf8JsonWriter(ms);

                writer.WriteStartObject();

                writer.WriteBoolean("enabled", Config.Instance.IsBrowserMonitoringEnabled);

                writer.WriteStartArray("fileExts");
                foreach (var ext in Config.Instance.FileExtensions)
                    writer.WriteStringValue(ext);
                writer.WriteEndArray();

                writer.WriteStartArray("blockedHosts");
                foreach (var host in Config.Instance.BlockedHosts)
                    writer.WriteStringValue(host);
                writer.WriteEndArray();

                writer.WriteStartArray("requestFileExts");
                foreach (var ext in Config.Instance.VideoExtensions)
                    writer.WriteStringValue(ext);
                writer.WriteEndArray();

                writer.WriteStartArray("mediaTypes");
                foreach (var ext in new[] { "audio/", "video/" })
                    writer.WriteStringValue(ext);
                writer.WriteEndArray();

                writer.WriteStartArray("tabsWatcher");
                foreach (var ext in new[] { ".youtube.", "/watch?v=" })
                    writer.WriteStringValue(ext);
                writer.WriteEndArray();

                var videoList = ApplicationContext.VideoTracker.GetVideoList();
                writer.WriteStartArray("videoList");
                foreach (var video in videoList)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", video.ID);
                    writer.WriteString("text", video.Name);
                    writer.WriteString("info", video.Description);
                    writer.WriteString("tabId", video.TabId);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteStartArray("matchingHosts");
                writer.WriteStringValue("googlevideo");
                writer.WriteEndArray();

                writer.WriteStartArray("blockedMimeTypes");
                foreach (var mime in Config.Instance.BlockedMimeTypes)
                    writer.WriteStringValue(mime);
                writer.WriteEndArray();

                writer.WriteStartArray("blockedUrlPatterns");
                foreach (var pattern in Config.Instance.BlockedUrlPatterns)
                    writer.WriteStringValue(pattern);
                writer.WriteEndArray();

                writer.WriteNumber("minDownloadSize", Config.Instance.MinDownloadSizeBytes);

                writer.WriteEndObject();
                writer.Flush();
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error sending config");
                return null;
            }
        }

        private void RemoveBlockedHeaders(Message message)
        {
            foreach (var header in blockedHeaders)
            {
                string? keyName = null;
                foreach (var key in message.RequestHeaders.Keys)
                {
                    if (key.Equals(header, StringComparison.InvariantCultureIgnoreCase))
                    {
                        keyName = key;
                        break;
                    }
                }
                if (!String.IsNullOrEmpty(keyName))
                {
                    message.RequestHeaders.Remove(keyName!);
                }
            }
        }

        private bool IsBlockedMimeType(Dictionary<string, List<string>> responseHeaders)
        {
            if (responseHeaders == null) return false;
            foreach (var key in responseHeaders.Keys)
            {
                if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    var values = responseHeaders[key];
                    if (values != null && values.Count > 0)
                    {
                        var contentType = values[0].ToLowerInvariant();
                        foreach (var blocked in Config.Instance.BlockedMimeTypes)
                        {
                            if (contentType.Contains(blocked.ToLowerInvariant()))
                            {
                                return true;
                            }
                        }
                    }
                    break;
                }
            }
            return false;
        }

        private bool IsBlockedUrlPattern(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var lowerUrl = url.ToLowerInvariant();
            foreach (var pattern in Config.Instance.BlockedUrlPatterns)
            {
                if (lowerUrl.Contains(pattern.ToLowerInvariant()))
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsBelowMinimumSize(Dictionary<string, List<string>> responseHeaders)
        {
            if (Config.Instance.MinDownloadSizeBytes <= 0) return false;
            if (responseHeaders == null) return false;
            foreach (var key in responseHeaders.Keys)
            {
                if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    var values = responseHeaders[key];
                    if (values != null && values.Count > 0 && long.TryParse(values[0], out long size))
                    {
                        return size > 0 && size < Config.Instance.MinDownloadSizeBytes;
                    }
                    break;
                }
            }
            return false;
        }
    }
}
