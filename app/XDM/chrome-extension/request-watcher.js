"use strict";
import Logger from './logger.js';

export default class RequestWatcher {
    constructor(callback) {
        this.logger = new Logger();
        this.blockedHosts = [];
        this.mediaExts = [];
        this.fileExts = [];
        this.requestMap = new Map();
        this.callback = callback;
        this.matchingHosts = [];
        this.mediaTypes = [];
        this.onSendHeadersEventCallback = this.onSendHeadersEvent.bind(this);
        this.onHeadersReceivedEventCallback = this.onHeadersReceivedEvent.bind(this);
        this.onErrorOccurredEventCallback = this.onErrorOccurredEvent.bind(this);
        this.urlPatterns = [];
        this.requestFileExts = [];

        // Download filtering config
        this.blockedMimeTypes = [
            "image/jpeg", "image/png", "image/webp", "image/gif",
            "image/svg+xml", "image/x-icon", "image/bmp", "image/tiff"
        ];
        this.blockedUrlPatterns = [];
        this.minDownloadSize = 1048576;
    }

    updateConfig(config) {
        if (config.blockedHosts) {
            this.blockedHosts = config.blockedHosts
        }
        if (config.fileExts) {
            this.fileExts = config.fileExts
        }
        if (config.mediaExts) {
            this.mediaExts = config.mediaExts
        }
        if (config.matchingHosts) {
            this.matchingHosts = config.matchingHosts
        }
        if (config.mediaTypes) {
            this.mediaTypes = config.mediaTypes
        }
        if (config.requestFileExts) {
            this.requestFileExts = config.requestFileExts
        }
        if (config.blockedMimeTypes) {
            this.blockedMimeTypes = config.blockedMimeTypes;
        }
        if (config.blockedUrlPatterns) {
            this.blockedUrlPatterns = config.blockedUrlPatterns;
        }
        if (config.minDownloadSize !== undefined) {
            this.minDownloadSize = config.minDownloadSize;
        }
        if (config.urlPatterns) {
            this.urlPatterns = config.urlPatterns.map(pattern => {
                try {
                    return new RegExp(pattern, "i");
                } catch { }
            }).filter(item => item || false);
        }
    }

    isMatchingRequest(res) {
        let u = new URL(res.url);

        let hostName = u.host;
        if (this.blockedHosts.find(h => hostName.indexOf(h) >= 0)) {
            return false;
        }

        // Check blocked URL patterns (CDN thumbnails, favicons, etc.)
        if (this.isBlockedUrlPattern(res.url)) {
            return false;
        }

        // Check blocked MIME types from response headers
        let mediaType = res.responseHeaders.find(h => h["name"].toUpperCase() === "CONTENT-TYPE");
        if (mediaType && this.isBlockedMimeType(mediaType["value"])) {
            return false;
        }

        // Check minimum download size from Content-Length header
        if (this.minDownloadSize > 0) {
            let contentLength = res.responseHeaders.find(h => h["name"].toUpperCase() === "CONTENT-LENGTH");
            if (contentLength) {
                let size = parseInt(contentLength["value"], 10);
                if (size > 0 && size < this.minDownloadSize) {
                    // Only apply size filter for non-streaming media requests
                    // (HLS/DASH manifests are small but should still be captured)
                    if (!this.isStreamingMedia(res)) {
                        return false;
                    }
                }
            }
        }

        let path = u.pathname;
        let upath = path.toUpperCase();
        if (this.mediaExts.find(e => upath.endsWith(e))) {
            return true;
        }

        if (this.requestFileExts.find(e => upath.endsWith(e))) {
            return true;
        }

        try {
            if (this.urlPatterns.find(re => re.test(res.url))) {
                return true;
            }
        } catch { }

        // Match media content types (audio/*, video/*)
        if (mediaType && this.mediaTypes.find(m => mediaType["value"].indexOf(m) >= 0)) {
            return true;
        }

        if (this.fileExts.find(e => upath.endsWith("." + e))) {
            return true;
        }

        // Content-Disposition: attachment detection — if server explicitly sends
        // a Content-Disposition header with "attachment", this is a real download
        let contentDisposition = res.responseHeaders.find(h => h["name"].toUpperCase() === "CONTENT-DISPOSITION");
        if (contentDisposition) {
            let dispositionValue = contentDisposition["value"].toUpperCase();
            // Check for file extensions in Content-Disposition
            if (this.fileExts.find(ext => dispositionValue.indexOf("." + ext) >= 0)) {
                return true;
            }
            // If Content-Disposition says "attachment", treat as downloadable
            if (dispositionValue.indexOf("ATTACHMENT") >= 0) {
                return true;
            }
        }

        // Match by known streaming hosts (e.g., googlevideo for YouTube)
        if (this.matchingHosts.find(h => hostName.indexOf(h) >= 0)) {
            return true;
        }

        // Detect HLS/DASH streaming manifests by content type
        if (mediaType) {
            let ct = mediaType["value"].toLowerCase();
            if (ct.includes("mpegurl") || ct.includes("m3u8") ||
                ct.includes("dash") || ct.includes("mpd")) {
                return true;
            }
        }

        // Detect HLS/DASH streaming manifests by URL extension
        let lowerUrl = res.url.toLowerCase();
        if (lowerUrl.includes(".m3u8") || lowerUrl.includes(".mpd")) {
            return true;
        }
    }

    /**
     * Check if a response is a streaming media manifest (HLS/DASH).
     * These should not be filtered by size since manifests are small files.
     */
    isStreamingMedia(res) {
        let lowerUrl = res.url.toLowerCase();
        if (lowerUrl.includes(".m3u8") || lowerUrl.includes(".mpd")) {
            return true;
        }
        let mediaType = res.responseHeaders.find(h => h["name"].toUpperCase() === "CONTENT-TYPE");
        if (mediaType) {
            let ct = mediaType["value"].toLowerCase();
            if (ct.includes("mpegurl") || ct.includes("dash") || ct.includes("mpd")) {
                return true;
            }
        }
        return false;
    }

    /**
     * Check if a MIME type is in the blocked list (thumbnails, small images).
     */
    isBlockedMimeType(mimeType) {
        if (!mimeType) return false;
        let lower = mimeType.toLowerCase();
        return this.blockedMimeTypes.some(blocked => lower.startsWith(blocked));
    }

    /**
     * Check if a URL matches blocked CDN/thumbnail patterns.
     */
    isBlockedUrlPattern(url) {
        if (!url || !this.blockedUrlPatterns || this.blockedUrlPatterns.length === 0) return false;
        let lower = url.toLowerCase();
        return this.blockedUrlPatterns.some(pattern => lower.includes(pattern.toLowerCase()));
    }

    onSendHeadersEvent(info) {
        if (info.method !== "GET" && !(this.matchingHosts
            && this.matchingHosts.find(matchingHost => info.url.indexOf(matchingHost) > 0))) {
            return;
        }
        this.requestMap.set(info.requestId, info);
    }

    onHeadersReceivedEvent(res) {
        let reqId = res.requestId;
        let req = this.requestMap.get(reqId);
        if (req) {
            this.requestMap.delete(reqId);
            if (this.callback && this.isMatchingRequest(res)) {
                if (req.tabId !== -1) {
                    chrome.tabs.get(
                        req.tabId,
                        tab => {
                            this.callback(this.createRequestData(req, res, tab.title, tab.url, req.tabId));
                        }
                    );
                } else {
                    this.callback(this.createRequestData(req, res, null, null, req.tabId));
                }
            }
        }
    }

    onErrorOccurredEvent(info) {
        let reqId = info.requestId;
        this.requestMap.delete(reqId);
    }

    register() {
        chrome.webRequest.onSendHeaders.addListener(
            this.onSendHeadersEventCallback,
            { urls: ["http://*/*", "https://*/*"] },
            ["extraHeaders", "requestHeaders"]
        );

        chrome.webRequest.onHeadersReceived.addListener(
            this.onHeadersReceivedEventCallback,
            { urls: ["http://*/*", "https://*/*"] },
            ["extraHeaders", "responseHeaders"]
        );

        chrome.webRequest.onErrorOccurred.addListener(
            this.onErrorOccurredEventCallback,
            { urls: ["http://*/*", "https://*/*"] }
        );
    }

    unRegister() {
        chrome.webRequest.onSendHeaders.removeListener(this.onSendHeadersEventCallback);
        chrome.webRequest.onHeadersReceived.removeListener(this.onHeadersReceivedEventCallback);
        chrome.webRequest.onErrorOccurred.removeListener(this.onErrorOccurredEventCallback);
    }

    createRequestData(req, res, title, tabUrl, tabId) {
        let data = {
            url: res.url,
            file: title,
            requestHeaders: {},
            responseHeaders: {},
            cookie: undefined,
            method: req.method,
            userAgent: navigator.userAgent,
            tabUrl: tabUrl,
            tabId: tabId + ""
        };

        let cookies = [];

        if (req.extraHeaders) {
            req.extraHeaders.forEach(h => {
                if (h.name === 'Cookie' || h.name === 'cookie') {
                    cookies.push(h.value);
                }
                this.addToDict(data.requestHeaders, h.name, h.value);
            });
        }
        if (req.requestHeaders) {
            req.requestHeaders.forEach(h => {
                if (h.name === 'Cookie' || h.name === 'cookie') {
                    cookies.push(h.value);
                }
                this.addToDict(data.requestHeaders, h.name, h.value);
            });
        }
        if (res.responseHeaders) {
            res.responseHeaders.forEach(h => {
                this.addToDict(data.responseHeaders, h.name, h.value);
            });
        }
        if (cookies.length > 0) {
            data.cookie = cookies.join(";");
        }
        return data;
    }

    addToDict(dict, key, value) {
        let values = dict[key];
        if (values) {
            values.push(value);
        } else {
            dict[key] = [value];
        }
    }
}