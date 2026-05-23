"use strict";
import Logger from './logger.js';
import RequestWatcher from './request-watcher.js';
import Connector from './connector.js';

export default class App {
    constructor() {
        this.logger = new Logger();
        this.videoList = [];
        this.blockedHosts = [];
        this.fileExts = [];
        this.requestWatcher = new RequestWatcher(this.onRequestDataReceived.bind(this));
        this.tabsWatcher = [];
        this.userDisabled = false;
        this.appEnabled = false;
        this.onDownloadCreatedCallback = this.onDownloadCreated.bind(this);
        this.onTabUpdateCallback = this.onTabUpdate.bind(this);
        this.activeTabId = -1;
        this.connector = new Connector(this.onMessage.bind(this), this.onDisconnect.bind(this));

        // Download filtering config from XDM server
        this.blockedMimeTypes = [
            "image/jpeg", "image/png", "image/webp", "image/gif",
            "image/svg+xml", "image/x-icon", "image/bmp", "image/tiff"
        ];
        this.blockedUrlPatterns = [];
        this.minDownloadSize = 1048576; // 1 MB default
    }

    start() {
        this.logger.log("starting...");
        this.starAppConnector();
        this.register();
        this.logger.log("started.");
    }

    starAppConnector() {
        this.connector.connect();
    }

    onMessage(msg) {
        this.logger.log("message from XDM");
        this.logger.log(msg);
        this.appEnabled = msg.enabled === true;
        this.fileExts = msg.fileExts;
        this.blockedHosts = msg.blockedHosts;
        this.tabsWatcher = msg.tabsWatcher;
        this.videoList = msg.videoList;
        this.requestWatcher.updateConfig({
            mediaExts: msg.requestFileExts,
            blockedHosts: msg.blockedHosts,
            matchingHosts: msg.matchingHosts,
            mediaTypes: msg.mediaTypes,
            blockedMimeTypes: msg.blockedMimeTypes || this.blockedMimeTypes,
            blockedUrlPatterns: msg.blockedUrlPatterns || this.blockedUrlPatterns,
            minDownloadSize: msg.minDownloadSize || this.minDownloadSize
        });
        // Update local filtering config from server
        if (msg.blockedMimeTypes) this.blockedMimeTypes = msg.blockedMimeTypes;
        if (msg.blockedUrlPatterns) this.blockedUrlPatterns = msg.blockedUrlPatterns;
        if (msg.minDownloadSize !== undefined) this.minDownloadSize = msg.minDownloadSize;
        this.updateActionIcon();
    }

    onDisconnect() {
        this.logger.log("Disconnected from native host!");
        this.logger.log("Disconnected...");
        this.updateActionIcon();
    }

    isMonitoringEnabled() {
        this.logger.log(this.appEnabled + " " + this.userDisabled);
        return this.appEnabled === true && this.userDisabled === false && this.connector.isConnected();
    }

    onRequestDataReceived(data) {
        //Streaming video data received, send to native messaging application
        this.logger.log("onRequestDataReceived");
        this.logger.log(data);
        this.isMonitoringEnabled() && this.connector.isConnected() && this.connector.postMessage("/media", data);
    }

    /**
     * Intercept downloads created by the browser. This replaces the deprecated
     * onDeterminingFilename approach with onCreated, which is supported in MV3.
     */
    onDownloadCreated(download) {
        this.logger.log("onDownloadCreated");
        if (!this.isMonitoringEnabled()) {
            return;
        }
        this.logger.log(download);
        let url = download.finalUrl || download.url;
        let filename = download.filename || "";

        if (!this.isSupportedProtocol(url)) {
            return;
        }

        // Check if this download should be intercepted
        if (!this.shouldTakeOver(url, filename, download.mime, download.fileSize)) {
            this.logger.log("Skipping download: " + url);
            return;
        }

        // Cancel the browser download and hand off to XDM
        chrome.downloads.cancel(
            download.id,
            () => chrome.downloads.erase({ id: download.id })
        );
        let referrer = download.referrer;
        if (!referrer && download.finalUrl !== download.url) {
            referrer = download.url;
        }
        this.triggerDownload(url, filename, referrer, download.fileSize, download.mime);
    }

    onTabUpdate(tabId, changeInfo, tab) {
        if (!this.isMonitoringEnabled()) {
            return;
        }
        if (changeInfo.title) {
            if (this.tabsWatcher &&
                this.tabsWatcher.find(t => tab.url.indexOf(t) > 0)) {
                this.logger.log("Tab changed: " + changeInfo.title + " => " + tab.url);
                try {
                    this.connector.postMessage("/tab-update", {
                        tabUrl: tab.url,
                        tabTitle: changeInfo.title
                    });
                } catch (ex) {
                    console.log(ex);
                }
            }
        }
    }

    register() {
        // Use onCreated instead of deprecated onDeterminingFilename (MV3 compatible)
        chrome.downloads.onCreated.addListener(
            this.onDownloadCreatedCallback
        );
        chrome.tabs.onUpdated.addListener(
            this.onTabUpdateCallback
        );
        chrome.runtime.onMessage.addListener(this.onPopupMessage.bind(this));
        this.requestWatcher.register();
        this.attachContextMenu();
        chrome.tabs.onActivated.addListener(this.onTabActivated.bind(this));
    }

    isSupportedProtocol(url) {
        if (!url) return false;
        try {
            let u = new URL(url);
            return u.protocol === 'http:' || u.protocol === 'https:';
        } catch {
            return false;
        }
    }

    /**
     * Determines if XDM should intercept this download.
     * Implements strict intent detection:
     * 1. Skip blocked MIME types (thumbnails, small images)
     * 2. Skip downloads below minimum size threshold
     * 3. Skip URLs matching blocked CDN/thumbnail patterns
     * 4. Only intercept if file extension matches known types
     */
    shouldTakeOver(url, file, mimeType, fileSize) {
        if (!this.isSupportedProtocol(url)) {
            return false;
        }

        let u;
        try {
            u = new URL(url);
        } catch {
            return false;
        }

        let hostName = u.host;

        // Check blocked hosts
        if (this.blockedHosts.find(item => hostName.indexOf(item) >= 0)) {
            return false;
        }

        // Check blocked MIME types (e.g., image/jpeg, image/png thumbnails)
        if (mimeType && this.isBlockedMimeType(mimeType)) {
            this.logger.log("Blocked MIME type: " + mimeType);
            return false;
        }

        // Check blocked URL patterns (CDN thumbnails, favicons, etc.)
        if (this.isBlockedUrlPattern(url)) {
            this.logger.log("Blocked URL pattern: " + url);
            return false;
        }

        // Check minimum file size threshold
        if (fileSize && fileSize > 0 && this.minDownloadSize > 0 && fileSize < this.minDownloadSize) {
            this.logger.log("Below min size (" + fileSize + " < " + this.minDownloadSize + "): " + url);
            return false;
        }

        // Check if file extension matches known downloadable types
        let path = file || u.pathname;
        let upath = path.toUpperCase();
        if (this.fileExts.find(ext => upath.endsWith(ext))) {
            return true;
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

    updateActionIcon() {
        chrome.action.setIcon({ path: this.getActionIcon() });
        let vc = "";
        if (this.videoList && this.videoList.length > 0) {
            let len = this.videoList.length;
            if (len > 0) {
                vc = len + "";
            }
        }
        chrome.action.setBadgeText({ text: vc });
        if (!this.connector.isConnected()) {
            this.logger.log("Not connected...");
            chrome.action.setPopup({ popup: "./error.html" });
            return;
        }
        if (!this.appEnabled) {
            chrome.action.setPopup({ popup: "./disabled.html" });
            return;
        }
        else {
            chrome.action.setPopup({ popup: "./popup.html" });
            return;
        }
    }

    getActionIconName(icon) {
        return this.isMonitoringEnabled() ? icon + ".png" : icon + "-mono.png";
    }

    getActionIcon() {
        return {
            "16": this.getActionIconName("icon16"),
            "48": this.getActionIconName("icon48"),
            "128": this.getActionIconName("icon128")
        }
    }

    triggerDownload(url, file, referer, size, mime) {
        chrome.cookies.getAll({ "url": url }, cookies => {
            let cookieStr = undefined;
            if (cookies) {
                cookieStr = cookies.map(cookie => cookie.name + "=" + cookie.value).join("; ");
            }
            let requestHeaders = { "User-Agent": [navigator.userAgent] };
            if (referer) {
                requestHeaders["Referer"] = [referer];
            }
            let responseHeaders = {};
            if (size) {
                let fz = +size;
                if (fz > 0) {
                    responseHeaders["Content-Length"] = [fz];
                }
            }
            if (mime) {
                responseHeaders["Content-Type"] = [mime];
            }
            let data = {
                url: url,
                cookie: cookieStr,
                requestHeaders: requestHeaders,
                responseHeaders: responseHeaders,
                filename: file,
                fileSize: size,
                mimeType: mime
            };
            this.logger.log(data);
            this.connector.postMessage("/download", data);
        });
    }

    diconnect() {
        this.onDisconnect();
    }

    onPopupMessage(request, sender, sendResponse) {
        this.logger.log(request.type);
        if (request.type === "stat") {
            let resp = {
                enabled: this.isMonitoringEnabled(),
                list: this.videoList
            };
            sendResponse(resp);
        }
        else if (request.type === "cmd") {
            this.userDisabled = request.enabled === false;
            this.logger.log("request.enabled:" + request.enabled);
            if (request.enabled && !this.connector.isConnected()) {
                this.connector.launchApp();
                return;
            }
            this.updateActionIcon();
        }
        else if (request.type === "vid") {
            let vid = request.itemId;
            this.connector.postMessage("/vid", {
                vid: vid + "",
            });
        }
        else if (request.type === "clear") {
            this.connector.postMessage("/clear", {});
        }
    }

    sendLinkToXDM(info, tab) {
        let url = info.linkUrl;
        if (!this.isSupportedProtocol(url)) {
            url = info.srcUrl;
        }
        if (!this.isSupportedProtocol(url)) {
            url = info.pageUrl;
        }
        if (!this.isSupportedProtocol(url)) {
            return;
        }
        this.triggerDownload(url, null, info.pageUrl, null, null);
    }

    sendImageToXDM(info, tab) {
        let url = info.srcUrl;
        if (!this.isSupportedProtocol(url))
            url = info.linkUrl;
        if (!this.isSupportedProtocol(url)) {
            url = info.pageUrl;
        }
        if (!this.isSupportedProtocol(url)) {
            return;
        }
        this.triggerDownload(url, null, info.pageUrl, null, null);
    }

    onMenuClicked(info, tab) {
        if (info.menuItemId == "download-any-link") {
            this.sendLinkToXDM(info, tab);
        }
        if (info.menuItemId == "download-image-link") {
            this.sendImageToXDM(info, tab);
        }
    }

    attachContextMenu() {
        chrome.contextMenus.create({
            id: 'download-any-link',
            title: "Download with XDM",
            contexts: ["link", "video", "audio", "all"]
        });

        chrome.contextMenus.create({
            id: 'download-image-link',
            title: "Download Image with XDM",
            contexts: ["image"]
        });

        chrome.contextMenus.onClicked.addListener(this.onMenuClicked.bind(this));
    }

    onTabActivated(activeInfo) {
        this.activeTabId = activeInfo.tabId + "";
        this.logger.log("Active tab: " + this.activeTabId);
        this.updateActionIcon();
    }
}
