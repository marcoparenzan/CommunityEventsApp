window.eventsAccount = {
    async post(url, body) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });
        const result = response.status === 204 ? {} : await response.json().catch(() => ({}));
        return {
            ok: response.ok,
            message: result.message || (response.ok ? "" : "Richiesta non riuscita."),
            warning: result.warning === true
        };
    },
    requestPin(form) {
        return this.post("/api/auth/request-pin", form);
    },
    // True quando il sito gira come app installata (PWA): l'accesso dura molto di più.
    isInstalledApp() {
        return window.matchMedia?.("(display-mode: standalone)")?.matches === true || window.navigator.standalone === true;
    },
    verifyPin(form) {
        return this.post("/api/auth/verify-pin", { ...form, installed: window.eventsAccount.isInstalledApp() });
    },
    async account() {
        const response = await fetch("/api/account", { credentials: "same-origin" });
        if (response.status === 401) {
            return null;
        }

        if (!response.ok) {
            throw new Error(`Impossibile caricare l'account (HTTP ${response.status}).`);
        }

        if (!response.headers.get("content-type")?.includes("application/json")) {
            throw new Error("Il server ha restituito una risposta non JSON per l'account.");
        }

        return response.json();
    },
    register(eventId, preferences) {
        return this.post(`/api/events/${eventId}/register`, preferences);
    },
    async profile() {
        const response = await fetch("/api/account/profile", { credentials: "same-origin" });
        return response.ok ? await response.json() : null;
    },
    async setNotifications(allow) {
        const response = await fetch("/api/account/notifications", {
            method: "PUT",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ allow })
        });
        const result = await response.json().catch(() => ({}));
        return { ok: response.ok, message: result.message || (response.ok ? "" : "Operazione non riuscita.") };
    },
    async deleteAccount() {
        const response = await fetch("/api/account", { method: "DELETE", credentials: "same-origin" });
        const result = await response.json().catch(() => ({}));
        return { ok: response.ok, message: result.message || (response.ok ? "" : "Non è stato possibile cancellare l'account.") };
    },
    async updatePreferences(eventId, preferences) {
        const response = await fetch(`/api/events/${eventId}/preferences`, {
            method: "PUT",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(preferences)
        });
        const result = await response.json().catch(() => ({}));
        return {
            ok: response.ok,
            message: result.message || (response.ok ? "" : "Non è stato possibile aggiornare le preferenze."),
            warning: result.warning === true
        };
    },
    async cancelRegistration(eventId) {
        const response = await fetch(`/api/events/${eventId}/register`, {
            method: "DELETE",
            credentials: "same-origin"
        });
        const result = await response.json().catch(() => ({}));
        return {
            ok: response.ok,
            message: result.message || (response.ok ? "" : "Non è stato possibile annullare l'iscrizione."),
            warning: result.warning === true
        };
    },
    signOut() {
        return this.post("/api/auth/sign-out", {});
    }
};

window.eventsSpeaker = {
    requestPin(email) {
        return window.eventsAccount.post("/api/speaker-auth/request-pin", { email });
    },
    verifyPin(form) {
        return window.eventsAccount.post("/api/speaker-auth/verify-pin", { ...form, installed: window.eventsAccount.isInstalledApp() });
    },
    async account() {
        const response = await fetch("/api/speaker/account", { credentials: "same-origin" });
        if (response.status === 401 || response.status === 403) {
            return null;
        }

        if (!response.ok) {
            throw new Error(`Impossibile caricare il profilo speaker (HTTP ${response.status}).`);
        }

        if (!response.headers.get("content-type")?.includes("application/json")) {
            throw new Error("Il server ha restituito una risposta non JSON per lo speaker.");
        }

        return response.json();
    },
    async sessions() {
        const response = await fetch("/api/speaker/sessions", { credentials: "same-origin" });
        if (!response.ok) {
            throw new Error(`Impossibile caricare le sessioni speaker (HTTP ${response.status}).`);
        }

        return response.json();
    },
    updateSessionLinks(request) {
        return window.eventsAccount.post("/api/speaker/sessions/resources", request);
    },
    signOut() {
        return window.eventsAccount.signOut();
    }
};

window.eventsCheckIn = {
    scannerStates: new WeakMap(),
    async post(url, body) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify(body)
        });
        const result = await response.json().catch(() => ({}));
        return {
            ok: response.ok,
            message: result.message || (response.ok ? "" : "Richiesta non riuscita."),
            warning: result.warning === true
        };
    },
    request(token, form) {
        return this.post(`/api/checkin/${encodeURIComponent(token)}`, form);
    },
    confirm(token) {
        return this.post("/api/checkin/confirm", { token });
    },
    addParticipant(eventId, participant) {
        return this.post(`/api/speaker/events/${eventId}/attendees`, participant);
    },
    async attendees(eventId) {
        const response = await fetch(`/api/speaker/events/${eventId}/attendees`, {
            credentials: "same-origin"
        });
        if (!response.ok) {
            throw new Error(`Impossibile caricare i partecipanti (HTTP ${response.status}).`);
        }
        return response.json();
    },
    async searchPeople(eventId, query) {
        const response = await fetch(`/api/speaker/events/${eventId}/people?q=${encodeURIComponent(query)}`, {
            credentials: "same-origin"
        });
        if (!response.ok) {
            throw new Error(`Ricerca non riuscita (HTTP ${response.status}).`);
        }
        return response.json();
    },
    registerExisting(eventId, body) {
        return this.post(`/api/speaker/events/${eventId}/attendees/existing`, body);
    },
    sendConfirmation(eventId, registrationId) {
        return this.post(`/api/speaker/events/${eventId}/attendees/${registrationId}/send-confirmation`, {});
    },
    manualCheckIn(eventId, registrationId) {
        return this.post(`/api/speaker/events/${eventId}/attendees/${registrationId}/check-in`, {});
    },
    authenticatedSelfCheckIn(eventId, signature) {
        return this.post(`/api/checkin/events/${eventId}/self`, { signature });
    },
    async startScanner(video, dotNetReference) {
        if (!navigator.mediaDevices?.getUserMedia) {
            return {
                ok: false,
                message: "Il browser non permette l'uso della fotocamera: apri la pagina con HTTPS e un browser aggiornato."
            };
        }

        this.stopScanner(video);
        let stream;
        try {
            stream = await navigator.mediaDevices.getUserMedia({
                audio: false,
                video: { facingMode: { ideal: "environment" }, width: { ideal: 1280 }, height: { ideal: 720 } }
            });
        } catch (error) {
            return { ok: false, message: this.cameraError(error) };
        }

        const state = {
            stream,
            timer: 0,
            active: true,
            lastValue: "",
            lastAt: 0,
            canvas: document.createElement("canvas"),
            detector: null
        };
        this.scannerStates.set(video, state);
        video.muted = true;
        video.setAttribute("playsinline", "");
        video.srcObject = stream;
        try {
            await video.play();
        } catch (error) {
            this.stopScanner(video);
            return { ok: false, message: "Non riesco ad avviare l'anteprima della fotocamera. Riprova." };
        }

        // Lettore nativo se c'è, altrimenti (o in aggiunta) la libreria jsQR: funziona su tutti i browser.
        if ("BarcodeDetector" in window) {
            try {
                state.detector = new BarcodeDetector({ formats: ["qr_code"] });
            } catch (error) {
                state.detector = null;
            }
        }
        try {
            await this.loadJsQr();
        } catch (error) {
            if (!state.detector) {
                this.stopScanner(video);
                return { ok: false, message: "Non riesco a caricare il lettore di QR. Ricarica la pagina e riprova." };
            }
        }

        // Se la fotocamera si interrompe (altra app, schermo bloccato) lo si dice, senza lasciare l'anteprima nera.
        const track = stream.getVideoTracks()[0];
        track?.addEventListener("ended", () => {
            if (state.active) {
                this.stopScanner(video);
                dotNetReference.invokeMethodAsync("OnScannerError", "La fotocamera si è interrotta. Premi \"Avvia fotocamera\" per riprovare.");
            }
        });

        const scan = async () => {
            if (!state.active) {
                return;
            }

            let value = null;
            try {
                if (video.readyState >= 2 && video.videoWidth > 0) {
                    if (state.detector) {
                        try {
                            const codes = await state.detector.detect(video);
                            value = codes.find(code => code.rawValue)?.rawValue ?? null;
                        } catch (error) {
                            state.detector = null; // il lettore nativo non funziona su questo dispositivo: resta jsQR
                        }
                    }
                    if (!value) {
                        value = this.decodeWithJsQr(state, video);
                    }
                }
            } catch (error) {
                value = null; // un fotogramma illeggibile non deve spegnere la fotocamera
            }

            // La fotocamera resta accesa: sarà la pagina a fermarla quando il QR è quello giusto.
            // Lo stesso QR non viene segnalato più di una volta ogni 3 secondi.
            if (value && state.active && (value !== state.lastValue || Date.now() - state.lastAt > 3000)) {
                state.lastValue = value;
                state.lastAt = Date.now();
                try {
                    await dotNetReference.invokeMethodAsync("OnQrCodeDetected", value);
                } catch (error) {
                    // circuito chiuso: non c'è nessuno a cui rispondere
                }
            }

            if (state.active) {
                state.timer = window.setTimeout(scan, 150);
            }
        };

        state.timer = window.setTimeout(scan, 150);
        return { ok: true, message: "Inquadra il QR del check-in stampato all'evento." };
    },
    jsQrLoading: null,
    loadJsQr() {
        if (window.jsQR) {
            return Promise.resolve();
        }
        if (!this.jsQrLoading) {
            this.jsQrLoading = new Promise((resolve, reject) => {
                const script = document.createElement("script");
                script.src = "/lib/jsqr/jsQR.js";
                script.onload = () => resolve();
                script.onerror = () => { this.jsQrLoading = null; reject(new Error("jsQR non disponibile")); };
                document.head.appendChild(script);
            });
        }
        return this.jsQrLoading;
    },
    decodeWithJsQr(state, video) {
        if (!window.jsQR) {
            return null;
        }
        const scale = Math.min(1, 720 / video.videoWidth);
        const width = Math.max(1, Math.round(video.videoWidth * scale));
        const height = Math.max(1, Math.round(video.videoHeight * scale));
        state.canvas.width = width;
        state.canvas.height = height;
        const context = state.canvas.getContext("2d", { willReadFrequently: true });
        context.drawImage(video, 0, 0, width, height);
        const image = context.getImageData(0, 0, width, height);
        return window.jsQR(image.data, width, height, { inversionAttempts: "attemptBoth" })?.data || null;
    },
    cameraError(error) {
        switch (error?.name) {
            case "NotAllowedError":
            case "SecurityError":
                return "Permesso alla fotocamera negato: abilitalo per questo sito nelle impostazioni del browser e riprova (serve HTTPS).";
            case "NotFoundError":
            case "OverconstrainedError":
                return "Non trovo nessuna fotocamera su questo dispositivo.";
            case "NotReadableError":
            case "AbortError":
                return "La fotocamera è in uso da un'altra app: chiudila e riprova.";
            default:
                return "Fotocamera non disponibile. Verifica i permessi e che la pagina sia aperta tramite HTTPS.";
        }
    },
    stopScanner(video) {
        const state = this.scannerStates.get(video);
        if (!state) {
            return;
        }

        state.active = false;
        window.clearTimeout(state.timer);
        for (const track of state.stream.getTracks()) {
            track.stop();
        }
        video.srcObject = null;
        this.scannerStates.delete(video);
    }
};

window.eventsAdmin = {
    async downloadFile(filename, streamReference, contentType) {
        const buffer = await streamReference.arrayBuffer();
        const blob = new Blob([buffer], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    downloadText(filename, text) {
        const blob = new Blob(["\uFEFF" + text], { type: "text/csv;charset=utf-8" });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(url);
    },
    async sync(eventId) {
        const response = await fetch(`/api/speaker/events/${eventId}/sync`, {
            method: "POST",
            credentials: "same-origin"
        });
        const result = await response.json().catch(() => ({}));
        return {
            ok: response.ok,
            message: response.ok
                ? `${result.synchronizedSessions || 0} sessioni sincronizzate.`
                : result.message || `Richiesta non riuscita (HTTP ${response.status}).`
        };
    }
};
