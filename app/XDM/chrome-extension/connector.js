"use strict";
import Logger from './logger.js';

const APP_BASE_URL = "http://127.0.0.1:8597";
const MAX_RETRY_DELAY_MS = 60000;
const INITIAL_RETRY_DELAY_MS = 1000;

export default class Connector {
    constructor(onMessage, onDisconnect) {
        this.logger = new Logger();
        this.onMessage = onMessage;
        this.onDisconnect = onDisconnect;
        this.connected = undefined;
        this.retryDelay = INITIAL_RETRY_DELAY_MS;
        this.consecutiveFailures = 0;
    }

    connect() {
        for (let i = 0; i < 12; i++) {
            chrome.alarms.create("alerm-" + i, {
                periodInMinutes: 1,
                when: Date.now() + 1000 + ((i + 1) * 5000)
            });
        }
        chrome.alarms.onAlarm.addListener(this.onTimer.bind(this));
    }

    onTimer() {
        fetch(APP_BASE_URL + "/sync", {
            signal: AbortSignal.timeout(5000)
        })
            .then(this.onResponse.bind(this))
            .catch(err => {
                this.consecutiveFailures++;
                this.disconnect();
            });
    }

    disconnect() {
        if (this.connected !== false) {
            this.connected = false;
            this.onDisconnect();
        }
    }

    isConnected() {
        return this.connected;
    }

    onResponse(res) {
        this.connected = true;
        this.consecutiveFailures = 0;
        this.retryDelay = INITIAL_RETRY_DELAY_MS;
        res.json().then(json => this.onMessage(json)).catch(err => this.disconnect());
    }

    postMessage(url, data) {
        fetch(APP_BASE_URL + url, {
            method: "POST",
            body: JSON.stringify(data),
            headers: { "Content-Type": "application/json" },
            signal: AbortSignal.timeout(10000)
        })
            .then(this.onResponse.bind(this))
            .catch(err => {
                this.logger.log("Post failed: " + err);
                this.disconnect();
            });
    }

    /**
     * Attempt to launch the XDM desktop app via native messaging.
     * This sends a message to the native messaging host which will
     * start the XDM process if it's not already running.
     */
    launchApp() {
        try {
            // Try connecting via native messaging to launch the app
            let port = chrome.runtime.connectNative("xdm_chrome.native_host");
            port.onDisconnect.addListener(() => {
                this.logger.log("Native messaging disconnected (app launch attempt)");
                // After attempting launch, retry connection via HTTP
                setTimeout(() => this.onTimer(), 2000);
            });
            port.postMessage({ action: "launch" });
        } catch (ex) {
            this.logger.log("Failed to launch app via native messaging: " + ex);
        }
    }
}