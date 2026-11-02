/**
 * WebRtcStreamGateway Client Library & Web Component
 * Drop-in WebRTC player for go2rtc streams via WebRtcStreamGateway.
 */
(function (global) {
  'use strict';

  /**
   * WebRtcStreamPlayer
   * Handles ICE servers, SDP offer/answer negotiation, playback, and auto-reconnection.
   */
  class WebRtcStreamPlayer {
    constructor(options = {}) {
      this.gatewayUrl = (options.gatewayUrl || (typeof window !== 'undefined' ? window.location.origin : '')).replace(/\/+$/, '');
      this.stream = options.stream || null;
      this.videoElement = options.videoElement || null;
      this.autoplay = options.autoplay !== false;
      this.muted = options.muted !== false;
      this.autoReconnect = options.autoReconnect !== false;
      this.reconnectDelayMs = options.reconnectDelayMs || 2500;
      this.maxReconnectAttempts = options.maxReconnectAttempts || 15;
      this.iceServers = options.iceServers || null;
      this.onStatusChange = options.onStatusChange || null;
      this.onError = options.onError || null;

      this.peerConnection = null;
      this.status = 'idle'; // idle | connecting | connected | disconnected | reconnecting | error
      this.reconnectAttempts = 0;
      this.reconnectTimeoutId = null;
      this.isDisposed = false;
      this.statsIntervalId = null;

      if (this.videoElement) {
        this._configureVideoElement(this.videoElement);
      }
    }

    _configureVideoElement(el) {
      el.playsInline = true;
      el.setAttribute('playsinline', '');
      el.setAttribute('webkit-playsinline', '');
      if (this.muted) {
        el.muted = true;
      }
      if (this.autoplay) {
        el.autoplay = true;
      }
    }

    _setStatus(newStatus, detail = null) {
      if (this.status === newStatus && !detail) return;
      this.status = newStatus;
      if (typeof this.onStatusChange === 'function') {
        try {
          this.onStatusChange(newStatus, detail);
        } catch (err) {
          console.error('[WebRtcStreamPlayer] Error in onStatusChange callback:', err);
        }
      }
    }

    _emitError(error) {
      this._setStatus('error', { error });
      if (typeof this.onError === 'function') {
        try {
          this.onError(error);
        } catch (err) {
          console.error('[WebRtcStreamPlayer] Error in onError callback:', err);
        }
      }
    }

    getSnapshotUrl(streamName = null) {
      const target = streamName || this.stream;
      const param = target ? `?stream=${encodeURIComponent(target)}` : '';
      return `${this.gatewayUrl}/api/snapshot${param}`;
    }

    async resolveStreamName() {
      if (this.stream) return this.stream;
      try {
        const res = await fetch(`${this.gatewayUrl}/api/streams`);
        if (res.ok) {
          const data = await res.json();
          this.stream = data.defaultStream || (data.streams && data.streams[0]) || '';
          return this.stream;
        }
      } catch (err) {
        console.warn('[WebRtcStreamPlayer] Could not fetch default stream from gateway:', err);
      }
      return '';
    }

    async fetchIceServers() {
      if (this.iceServers && this.iceServers.length > 0) {
        return this.iceServers;
      }
      try {
        const res = await fetch(`${this.gatewayUrl}/api/ice-servers`);
        if (!res.ok) {
          throw new Error(`Failed to fetch ICE servers: HTTP ${res.status}`);
        }
        const data = await res.json();
        return data.iceServers || [];
      } catch (err) {
        console.warn('[WebRtcStreamPlayer] Error fetching ICE servers, falling back to public STUN:', err);
        return [{ urls: ['stun:stun.cloudflare.com:3478', 'stun:stun.l.google.com:19302'] }];
      }
    }

    async start() {
      if (this.isDisposed) return;
      this._cleanupConnection();
      this._setStatus(this.reconnectAttempts > 0 ? 'reconnecting' : 'connecting', {
        attempt: this.reconnectAttempts,
        maxAttempts: this.maxReconnectAttempts
      });

      try {
        await this.resolveStreamName();
        const iceServers = await this.fetchIceServers();

        const pc = new RTCPeerConnection({
          iceServers: iceServers.length > 0 ? iceServers : undefined,
          bundlePolicy: 'max-bundle'
        });
        this.peerConnection = pc;

        pc.ontrack = (event) => {
          if (this.videoElement && event.streams && event.streams[0]) {
            if (this.videoElement.srcObject !== event.streams[0]) {
              this.videoElement.srcObject = event.streams[0];
            }
            if (this.autoplay) {
              const playPromise = this.videoElement.play();
              if (playPromise !== undefined) {
                playPromise.catch((err) => {
                  console.warn('[WebRtcStreamPlayer] Autoplay was blocked, retrying muted:', err);
                  this.videoElement.muted = true;
                  this.videoElement.play().catch((playErr) => {
                    console.error('[WebRtcStreamPlayer] Muted playback also failed:', playErr);
                  });
                });
              }
            }
          }
        };

        pc.onconnectionstatechange = () => {
          const state = pc.connectionState;
          if (state === 'connected') {
            this.reconnectAttempts = 0;
            this._setStatus('connected');
          } else if (state === 'failed' || state === 'disconnected') {
            this._setStatus('disconnected', { state });
            this._handleDisconnect();
          }
        };

        pc.oniceconnectionstatechange = () => {
          const iceState = pc.iceConnectionState;
          if (iceState === 'failed' || iceState === 'disconnected') {
            if (pc.connectionState !== 'connected') {
              this._handleDisconnect();
            }
          }
        };

        pc.addTransceiver('video', { direction: 'recvonly' });
        pc.addTransceiver('audio', { direction: 'recvonly' });

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        // Wait for ICE gathering to complete or timeout
        await this._waitForIceGathering(pc, 1500);

        if (!pc.localDescription || !pc.localDescription.sdp) {
          throw new Error('Local SDP offer is missing or invalid.');
        }

        const negotiateResponse = await fetch(`${this.gatewayUrl}/api/webrtc`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            sdp: pc.localDescription.sdp,
            type: 'offer',
            stream: this.stream
          })
        });

        if (!negotiateResponse.ok) {
          throw new Error(`Gateway SDP negotiation failed: HTTP ${negotiateResponse.status}`);
        }

        const answer = await negotiateResponse.json();
        if (!answer || !answer.sdp) {
          throw new Error('Received empty or invalid SDP answer from gateway.');
        }

        await pc.setRemoteDescription(new RTCSessionDescription({
          type: answer.type || 'answer',
          sdp: answer.sdp
        }));

      } catch (err) {
        console.error('[WebRtcStreamPlayer] Connection error:', err);
        this._emitError(err);
        this._handleDisconnect();
      }
    }

    _waitForIceGathering(pc, timeoutMs) {
      return new Promise((resolve) => {
        if (pc.iceGatheringState === 'complete') {
          resolve();
          return;
        }

        let timer = null;
        const checkState = () => {
          if (pc.iceGatheringState === 'complete') {
            clearTimeout(timer);
            pc.removeEventListener('icegatheringstatechange', checkState);
            resolve();
          }
        };

        timer = setTimeout(() => {
          pc.removeEventListener('icegatheringstatechange', checkState);
          resolve(); // Resolve anyway on timeout so trickle/candidate SDP proceeds
        }, timeoutMs);

        pc.addEventListener('icegatheringstatechange', checkState);
      });
    }

    _handleDisconnect() {
      if (this.isDisposed) return;
      if (!this.autoReconnect) return;

      if (this.reconnectAttempts >= this.maxReconnectAttempts) {
        this._setStatus('error', { error: new Error('Maximum reconnection attempts reached.') });
        return;
      }

      this.reconnectAttempts++;
      const backoffDelay = Math.min(this.reconnectDelayMs * Math.pow(1.3, this.reconnectAttempts - 1), 15000);

      this._setStatus('reconnecting', {
        attempt: this.reconnectAttempts,
        maxAttempts: this.maxReconnectAttempts,
        delayMs: backoffDelay
      });

      clearTimeout(this.reconnectTimeoutId);
      this.reconnectTimeoutId = setTimeout(() => {
        if (!this.isDisposed) {
          this.start();
        }
      }, backoffDelay);
    }

    _cleanupConnection() {
      clearTimeout(this.reconnectTimeoutId);
      if (this.peerConnection) {
        try {
          this.peerConnection.ontrack = null;
          this.peerConnection.onconnectionstatechange = null;
          this.peerConnection.oniceconnectionstatechange = null;
          this.peerConnection.close();
        } catch (e) {
          // ignore cleanup errors
        }
        this.peerConnection = null;
      }
    }

    stop() {
      this._cleanupConnection();
      if (this.videoElement) {
        this.videoElement.srcObject = null;
      }
      this._setStatus('disconnected');
    }

    async reconnect() {
      this.reconnectAttempts = 0;
      await this.start();
    }

    async setStream(newStream) {
      if (this.stream === newStream && this.status === 'connected') return;
      this.stream = newStream;
      await this.reconnect();
    }

    async captureSnapshotBlob() {
      const url = this.getSnapshotUrl();
      const res = await fetch(url);
      if (!res.ok) throw new Error(`Snapshot capture failed: HTTP ${res.status}`);
      return await res.blob();
    }

    destroy() {
      this.isDisposed = true;
      this.stop();
      if (this.videoElement) {
        this.videoElement.srcObject = null;
        this.videoElement = null;
      }
    }
  }

  /**
   * <webrtc-stream> Custom Element
   * Encapsulates the video element, snapshot fallback, controls, and WebRtcStreamPlayer.
   */
  class WebRtcStreamElement extends HTMLElement {
    static get observedAttributes() {
      return ['stream', 'gateway', 'autoplay', 'muted', 'controls', 'poster', 'fit', 'reconnect'];
    }

    constructor() {
      super();
      this.attachShadow({ mode: 'open' });
      this.player = null;
      this._idleTimer = null;
      this._hasUserInteracted = false;
    }

    connectedCallback() {
      this._render();
      this._initPlayer();
      this._bindEvents();
    }

    disconnectedCallback() {
      if (this.player) {
        this.player.destroy();
        this.player = null;
      }
      clearTimeout(this._idleTimer);
    }

    attributeChangedCallback(name, oldValue, newValue) {
      if (oldValue === newValue || !this.player) return;

      if (name === 'stream' && this.player) {
        this.player.setStream(newValue);
        this._updatePoster();
      } else if (name === 'muted') {
        const isMuted = newValue !== 'false' && newValue !== null;
        if (this.video) this.video.muted = isMuted;
        this._updateAudioButton(isMuted);
      } else if (name === 'fit') {
        if (this.video) this.video.style.objectFit = newValue || 'contain';
      }
    }

    get stream() { return this.getAttribute('stream'); }
    set stream(val) { this.setAttribute('stream', val); }

    get gateway() { return this.getAttribute('gateway') || window.location.origin; }
    set gateway(val) { this.setAttribute('gateway', val); }

    get autoplay() { return this.getAttribute('autoplay') !== 'false'; }
    get muted() { return this.getAttribute('muted') !== 'false'; }
    get showControls() { return this.getAttribute('controls') !== 'false'; }
    get usePoster() { return this.getAttribute('poster') !== 'false'; }
    get fit() { return this.getAttribute('fit') || 'contain'; }

    _render() {
      const isControls = this.showControls;
      const objectFit = this.fit;

      this.shadowRoot.innerHTML = `
        <style>
          :host {
            display: inline-block;
            position: relative;
            background: #000;
            overflow: hidden;
            border-radius: inherit;
            width: 100%;
            height: 100%;
            min-height: 200px;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
            color: #fff;
            user-select: none;
            box-sizing: border-box;
          }

          *, *::before, *::after {
            box-sizing: inherit;
          }

          .player-root {
            position: relative;
            width: 100%;
            height: 100%;
            display: flex;
            align-items: center;
            justify-content: center;
            background: #090c10;
          }

          video {
            width: 100%;
            height: 100%;
            object-fit: ${objectFit};
            display: block;
          }

          .poster-image {
            position: absolute;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            object-fit: ${objectFit};
            transition: opacity 0.4s ease;
            pointer-events: none;
            opacity: 1;
          }

          .poster-image.hidden {
            opacity: 0;
          }

          /* Status Badges */
          .badge-overlay {
            position: absolute;
            top: 12px;
            left: 12px;
            display: flex;
            align-items: center;
            gap: 6px;
            padding: 4px 10px;
            border-radius: 9999px;
            font-size: 11px;
            font-weight: 700;
            letter-spacing: 0.05em;
            text-transform: uppercase;
            backdrop-filter: blur(8px);
            background: rgba(0, 0, 0, 0.65);
            border: 1px solid rgba(255, 255, 255, 0.15);
            box-shadow: 0 4px 12px rgba(0, 0, 0, 0.4);
            pointer-events: none;
            transition: opacity 0.3s ease;
            z-index: 10;
          }

          .status-dot {
            width: 8px;
            height: 8px;
            border-radius: 50%;
            background: #8b949e;
          }

          .status-dot.live {
            background: #2ea043;
            box-shadow: 0 0 8px #2ea043;
            animation: pulse 1.8s infinite;
          }

          .status-dot.connecting, .status-dot.reconnecting {
            background: #d29922;
            animation: pulse 1s infinite;
          }

          .status-dot.error {
            background: #f85149;
          }

          @keyframes pulse {
            0% { transform: scale(0.95); opacity: 0.8; }
            50% { transform: scale(1.15); opacity: 1; }
            100% { transform: scale(0.95); opacity: 0.8; }
          }

          /* Control bar */
          .controls-bar {
            position: absolute;
            bottom: 0;
            left: 0;
            right: 0;
            display: flex;
            align-items: center;
            justify-content: space-between;
            padding: 8px 14px;
            background: linear-gradient(to top, rgba(0, 0, 0, 0.85), rgba(0, 0, 0, 0));
            opacity: 1;
            transition: opacity 0.3s ease;
            z-index: 20;
            ${!isControls ? 'display: none !important;' : ''}
          }

          .controls-bar.autohide {
            opacity: 0;
            pointer-events: none;
          }

          .controls-left, .controls-right {
            display: flex;
            align-items: center;
            gap: 10px;
          }

          button.ctrl-btn {
            background: transparent;
            border: none;
            color: #f0f6fc;
            cursor: pointer;
            padding: 6px;
            border-radius: 6px;
            display: flex;
            align-items: center;
            justify-content: center;
            transition: background 0.2s, transform 0.1s;
          }

          button.ctrl-btn:hover {
            background: rgba(255, 255, 255, 0.2);
          }

          button.ctrl-btn:active {
            transform: scale(0.94);
          }

          button.ctrl-btn svg {
            width: 18px;
            height: 18px;
            fill: currentColor;
          }

          .stream-label {
            font-size: 12px;
            color: #c9d1d9;
            font-weight: 500;
            max-width: 160px;
            overflow: hidden;
            text-overflow: ellipsis;
            white-space: nowrap;
          }

          /* Center Big Button for Play/Unmute */
          .center-action {
            position: absolute;
            display: flex;
            flex-direction: column;
            align-items: center;
            gap: 10px;
            z-index: 15;
            cursor: pointer;
          }

          .center-action-btn {
            width: 56px;
            height: 56px;
            border-radius: 50%;
            background: rgba(0, 0, 0, 0.7);
            border: 2px solid rgba(255, 255, 255, 0.8);
            display: flex;
            align-items: center;
            justify-content: center;
            color: #fff;
            backdrop-filter: blur(8px);
            box-shadow: 0 4px 20px rgba(0,0,0,0.5);
            transition: transform 0.2s, background 0.2s;
          }

          .center-action:hover .center-action-btn {
            transform: scale(1.1);
            background: rgba(0, 0, 0, 0.85);
          }

          .center-action-text {
            font-size: 13px;
            font-weight: 600;
            background: rgba(0, 0, 0, 0.7);
            padding: 4px 10px;
            border-radius: 12px;
          }

          .hidden {
            display: none !important;
          }
        </style>

        <div class="player-root" id="playerRoot">
          <video id="videoElement" playsinline></video>
          <img id="posterImage" class="poster-image hidden" alt="Stream snapshot" />

          <!-- Status badge -->
          <div class="badge-overlay" id="badgeOverlay">
            <span class="status-dot connecting" id="statusDot"></span>
            <span id="statusText">CONNECTING</span>
          </div>

          <!-- Center Click Prompt (Autoplay/Unmute/Error) -->
          <div class="center-action hidden" id="centerAction">
            <div class="center-action-btn" id="centerActionBtn">
              <svg id="centerActionIcon" viewBox="0 0 24 24" width="28" height="28" fill="currentColor">
                <path d="M8 5v14l11-7z"/>
              </svg>
            </div>
            <span class="center-action-text" id="centerActionText">Click to Play</span>
          </div>

          <!-- Bottom Controls -->
          <div class="controls-bar" id="controlsBar">
            <div class="controls-left">
              <button class="ctrl-btn" id="playPauseBtn" title="Play / Pause">
                <svg id="playIcon" viewBox="0 0 24 24"><path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/></svg>
              </button>
              <button class="ctrl-btn" id="audioBtn" title="Mute / Unmute">
                <svg id="audioIcon" viewBox="0 0 24 24"><path d="M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"/></svg>
              </button>
              <span class="stream-label" id="streamLabel"></span>
            </div>
            <div class="controls-right">
              <button class="ctrl-btn" id="snapshotBtn" title="Download Snapshot">
                <svg viewBox="0 0 24 24"><path d="M12 15.2a3.2 3.2 0 100-6.4 3.2 3.2 0 000 6.4z"/><path d="M9 2L7.17 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2h-3.17L15 2H9zm3 15c-2.76 0-5-2.24-5-5s2.24-5 5-5 5 2.24 5 5-2.24 5-5 5z"/></svg>
              </button>
              <button class="ctrl-btn" id="reloadBtn" title="Reconnect Stream">
                <svg viewBox="0 0 24 24"><path d="M17.65 6.35A7.958 7.958 0 0012 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0112 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/></svg>
              </button>
              <button class="ctrl-btn" id="fullscreenBtn" title="Fullscreen">
                <svg viewBox="0 0 24 24"><path d="M7 14H5v5h5v-2H7v-3zm-2-4h2V7h3V5H5v5zm12 7h-3v2h5v-5h-2v3zM14 5v2h3v3h2V5h-5z"/></svg>
              </button>
            </div>
          </div>
        </div>
      `;

      this.video = this.shadowRoot.getElementById('videoElement');
      this.posterImg = this.shadowRoot.getElementById('posterImage');
      this.statusDot = this.shadowRoot.getElementById('statusDot');
      this.statusText = this.shadowRoot.getElementById('statusText');
      this.controlsBar = this.shadowRoot.getElementById('controlsBar');
      this.streamLabel = this.shadowRoot.getElementById('streamLabel');
      this.centerAction = this.shadowRoot.getElementById('centerAction');
      this.centerActionText = this.shadowRoot.getElementById('centerActionText');
      this.centerActionIcon = this.shadowRoot.getElementById('centerActionIcon');
      this.playPauseBtn = this.shadowRoot.getElementById('playPauseBtn');
      this.playIcon = this.shadowRoot.getElementById('playIcon');
      this.audioBtn = this.shadowRoot.getElementById('audioBtn');
      this.audioIcon = this.shadowRoot.getElementById('audioIcon');
      this.snapshotBtn = this.shadowRoot.getElementById('snapshotBtn');
      this.reloadBtn = this.shadowRoot.getElementById('reloadBtn');
      this.fullscreenBtn = this.shadowRoot.getElementById('fullscreenBtn');
      this.playerRoot = this.shadowRoot.getElementById('playerRoot');
    }

    _updatePoster() {
      if (!this.usePoster || !this.posterImg) return;
      const targetStream = this.stream || '';
      const url = `${this.gateway}/api/snapshot${targetStream ? `?stream=${encodeURIComponent(targetStream)}` : ''}&t=${Date.now()}`;
      this.posterImg.src = url;
      this.posterImg.classList.remove('hidden');
    }

    _initPlayer() {
      this._updatePoster();

      this.player = new WebRtcStreamPlayer({
        gatewayUrl: this.gateway,
        stream: this.stream,
        videoElement: this.video,
        autoplay: this.autoplay,
        muted: this.muted,
        autoReconnect: this.getAttribute('reconnect') !== 'false',
        onStatusChange: (status, detail) => this._onPlayerStatusChange(status, detail),
        onError: (err) => this._onPlayerError(err)
      });

      this.player.start();
    }

    _onPlayerStatusChange(status, detail) {
      if (this.streamLabel && this.player.stream) {
        this.streamLabel.textContent = this.player.stream;
      }

      this.statusDot.className = 'status-dot ' + status;

      switch (status) {
        case 'connected':
          this.statusText.textContent = 'LIVE';
          this.centerAction.classList.add('hidden');
          // Hide poster once video plays
          this.video.addEventListener('playing', () => {
            if (this.posterImg) this.posterImg.classList.add('hidden');
          }, { once: true });
          this._dispatchEvent('webrtc:connected', { stream: this.player.stream });
          break;

        case 'connecting':
          this.statusText.textContent = 'CONNECTING';
          this.centerAction.classList.add('hidden');
          break;

        case 'reconnecting':
          this.statusText.textContent = detail && detail.attempt ? `RECONNECTING (${detail.attempt})` : 'RECONNECTING';
          this.centerAction.classList.add('hidden');
          this._dispatchEvent('webrtc:reconnecting', detail);
          break;

        case 'disconnected':
          this.statusText.textContent = 'DISCONNECTED';
          this._dispatchEvent('webrtc:disconnected');
          break;

        case 'error':
          this.statusText.textContent = 'ERROR';
          this._showCenterPrompt('Retry Connection', 'retry');
          this._dispatchEvent('webrtc:error', detail);
          break;
      }

      this._dispatchEvent('webrtc:statuschange', { status, detail });
    }

    _onPlayerError(err) {
      this._showCenterPrompt('Retry Connection', 'retry');
    }

    _showCenterPrompt(text, type) {
      if (!this.centerAction) return;
      this.centerActionText.textContent = text;
      this.centerAction.classList.remove('hidden');
      if (type === 'play') {
        this.centerActionIcon.innerHTML = '<path d="M8 5v14l11-7z"/>';
      } else if (type === 'unmute') {
        this.centerActionIcon.innerHTML = '<path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"/>';
      } else {
        this.centerActionIcon.innerHTML = '<path d="M17.65 6.35A7.958 7.958 0 0012 4c-4.42 0-7.99 3.58-7.99 8s3.57 8 7.99 8c3.73 0 6.84-2.55 7.73-6h-2.08A5.99 5.99 0 0112 18c-3.31 0-6-2.69-6-6s2.69-6 6-6c1.66 0 3.14.69 4.22 1.78L13 11h7V4l-2.35 2.35z"/>';
      }
    }

    _bindEvents() {
      // Play / Pause
      this.playPauseBtn.addEventListener('click', () => {
        if (this.video.paused) {
          this.video.play();
          this.playIcon.innerHTML = '<path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>';
        } else {
          this.video.pause();
          this.playIcon.innerHTML = '<path d="M8 5v14l11-7z"/>';
        }
      });

      this.video.addEventListener('pause', () => {
        this.playIcon.innerHTML = '<path d="M8 5v14l11-7z"/>';
      });

      this.video.addEventListener('play', () => {
        this.playIcon.innerHTML = '<path d="M6 19h4V5H6v14zm8-14v14h4V5h-4z"/>';
      });

      // Audio mute/unmute
      this.audioBtn.addEventListener('click', () => {
        this.video.muted = !this.video.muted;
        this._updateAudioButton(this.video.muted);
      });

      // Center Prompt Click
      this.centerAction.addEventListener('click', () => {
        if (this.player.status === 'error' || this.player.status === 'disconnected') {
          this.player.reconnect();
        } else if (this.video.muted) {
          this.video.muted = false;
          this._updateAudioButton(false);
          this.centerAction.classList.add('hidden');
        } else {
          this.video.play();
          this.centerAction.classList.add('hidden');
        }
      });

      // Reload
      this.reloadBtn.addEventListener('click', () => {
        this._updatePoster();
        this.player.reconnect();
      });

      // Snapshot Download
      this.snapshotBtn.addEventListener('click', async () => {
        try {
          const blob = await this.player.captureSnapshotBlob();
          const url = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = url;
          a.download = `snapshot-${this.player.stream || 'stream'}-${Date.now()}.jpg`;
          a.click();
          URL.revokeObjectURL(url);
        } catch (err) {
          console.error('[WebRtcStream] Snapshot download failed:', err);
        }
      });

      // Fullscreen
      this.fullscreenBtn.addEventListener('click', () => {
        if (!document.fullscreenElement) {
          if (this.playerRoot.requestFullscreen) {
            this.playerRoot.requestFullscreen();
          } else if (this.playerRoot.webkitRequestFullscreen) {
            this.playerRoot.webkitRequestFullscreen();
          }
        } else {
          if (document.exitFullscreen) {
            document.exitFullscreen();
          }
        }
      });

      // Controls autohide on mouse idle
      if (this.showControls) {
        const resetIdle = () => {
          this.controlsBar.classList.remove('autohide');
          clearTimeout(this._idleTimer);
          this._idleTimer = setTimeout(() => {
            if (!this.video.paused) {
              this.controlsBar.classList.add('autohide');
            }
          }, 2500);
        };

        this.playerRoot.addEventListener('mousemove', resetIdle);
        this.playerRoot.addEventListener('touchstart', resetIdle, { passive: true });
        this.playerRoot.addEventListener('mouseleave', () => {
          if (!this.video.paused) {
            this.controlsBar.classList.add('autohide');
          }
        });
      }
    }

    _updateAudioButton(isMuted) {
      if (isMuted) {
        this.audioIcon.innerHTML = '<path d="M4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z"/>';
      } else {
        this.audioIcon.innerHTML = '<path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z"/>';
      }
    }

    _dispatchEvent(name, detail = {}) {
      this.dispatchEvent(new CustomEvent(name, {
        bubbles: true,
        composed: true,
        detail
      }));
    }

    // Public API
    play() { return this.video ? this.video.play() : Promise.resolve(); }
    pause() { if (this.video) this.video.pause(); }
    reconnect() { return this.player ? this.player.reconnect() : Promise.resolve(); }
    captureSnapshot() { return this.player ? this.player.captureSnapshotBlob() : Promise.reject('Not ready'); }
  }

  // Register Custom Element
  if (typeof window !== 'undefined' && window.customElements && !window.customElements.get('webrtc-stream')) {
    window.customElements.define('webrtc-stream', WebRtcStreamElement);
  }

  // Global namespace exports
  global.WebRtcStreamPlayer = WebRtcStreamPlayer;
  global.WebRtcStreamElement = WebRtcStreamElement;
  global.WebRtcStreamGateway = {
    WebRtcStreamPlayer,
    WebRtcStreamElement
  };

})(typeof window !== 'undefined' ? window : globalThis);
