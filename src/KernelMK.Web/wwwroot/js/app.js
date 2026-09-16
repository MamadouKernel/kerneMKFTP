window.kernelMK = {
    downloadFile: function (fileName, mimeType, base64Content) {
        const link = document.createElement('a');
        link.href = `data:${mimeType};base64,${base64Content}`;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },
    togglePasswordVisibility: function (button) {
        const wrapper = button.closest('.kmk-password-wrapper');
        if (!wrapper) return;
        const input = wrapper.querySelector('input');
        if (!input) return;
        const showing = input.type === 'text';
        input.type = showing ? 'password' : 'text';
        button.setAttribute('aria-label', showing ? 'Afficher le mot de passe' : 'Masquer le mot de passe');
        button.innerHTML = showing ? kernelMK._eyeIcon : kernelMK._eyeOffIcon;
    },
    _eyeIcon: '<svg class="h-5 w-5" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.6"><path stroke-linecap="round" stroke-linejoin="round" d="M2.036 12.322a1.012 1.012 0 0 1 0-.639C3.423 7.51 7.36 4.5 12 4.5c4.638 0 8.573 3.007 9.963 7.178.07.207.07.431 0 .639C20.577 16.49 16.64 19.5 12 19.5c-4.638 0-8.573-3.007-9.963-7.178Z" /><path stroke-linecap="round" stroke-linejoin="round" d="M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z" /></svg>',
    _eyeOffIcon: '<svg class="h-5 w-5" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="1.6"><path stroke-linecap="round" stroke-linejoin="round" d="M3.98 8.223A10.477 10.477 0 0 0 1.934 12C3.226 16.338 7.244 19.5 12 19.5c.993 0 1.953-.138 2.863-.395M6.228 6.228A10.45 10.45 0 0 1 12 4.5c4.756 0 8.773 3.162 10.065 7.498a10.523 10.523 0 0 1-4.293 5.774M6.228 6.228 3 3m3.228 3.228 3.65 3.65m7.894 7.894L21 21m-3.228-3.228-3.65-3.65m0 0a3 3 0 1 0-4.243-4.243m4.242 4.242L9.88 9.88" /></svg>',
    startIdleTimer: function (timeoutMs, warningMs) {
        if (window.__kmkIdleTimerStarted) return; // Un seul minuteur par session/onglet
        window.__kmkIdleTimerStarted = true;

        let idleTimer = null;
        let countdownInterval = null;
        let warningEl = null;

        function removeWarning() {
            if (warningEl) { warningEl.remove(); warningEl = null; }
            if (countdownInterval) { clearInterval(countdownInterval); countdownInterval = null; }
        }

        function doLogout() {
            const form = document.querySelector('form[action="Account/Logout"]');
            if (form) { form.submit(); } else { window.location.href = '/Account/Login'; }
        }

        function showWarning() {
            removeWarning();
            let remaining = Math.ceil(warningMs / 1000);
            warningEl = document.createElement('div');
            warningEl.setAttribute('role', 'alertdialog');
            warningEl.style.cssText = 'position:fixed;bottom:24px;right:24px;z-index:9999;background:#0f172a;color:#fff;padding:16px 20px;border-radius:14px;box-shadow:0 10px 30px rgba(0,0,0,.35);font:13px/1.4 system-ui,-apple-system,Segoe UI,sans-serif;max-width:320px;';
            warningEl.innerHTML = '<div style="font-weight:700;margin-bottom:6px;">Session sur le point d\'expirer</div>' +
                '<div>Déconnexion automatique par inactivité dans <span id="kmk-idle-count">' + remaining + '</span>s.</div>' +
                '<button id="kmk-idle-stay" type="button" style="margin-top:10px;background:#4f46e5;color:#fff;border:none;padding:6px 14px;border-radius:8px;cursor:pointer;font-weight:600;font-size:12px;">Rester connecté</button>';
            document.body.appendChild(warningEl);
            document.getElementById('kmk-idle-stay').addEventListener('click', reset);
            countdownInterval = setInterval(function () {
                remaining -= 1;
                const span = document.getElementById('kmk-idle-count');
                if (span) span.textContent = remaining;
                if (remaining <= 0) {
                    clearInterval(countdownInterval);
                    countdownInterval = null;
                    doLogout();
                }
            }, 1000);
        }

        function reset() {
            clearTimeout(idleTimer);
            removeWarning();
            idleTimer = setTimeout(showWarning, Math.max(0, timeoutMs - warningMs));
        }

        ['mousemove', 'keydown', 'mousedown', 'touchstart', 'scroll', 'wheel'].forEach(function (evt) {
            document.addEventListener(evt, reset, { passive: true });
        });

        reset();
    },
    copyToClipboard: async function (text) {
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return true;
            } else {
                const textArea = document.createElement("textarea");
                textArea.value = text;
                textArea.style.position = "fixed";
                textArea.style.left = "-999999px";
                textArea.style.top = "-999999px";
                document.body.appendChild(textArea);
                textArea.focus();
                textArea.select();
                const successful = document.execCommand('copy');
                textArea.remove();
                return successful;
            }
        } catch (err) {
            console.error("Clipboard copy failed:", err);
            return false;
        }
    },
    globalSearch: {
        dotNetRef: null,
        register: function (dotNetRef) {
            kernelMK.globalSearch.dotNetRef = dotNetRef;
        }
    },
    focusElementById: function (elementId) {
        var el = document.getElementById(elementId);
        if (el) el.focus();
    },
    push: {
        // La clé publique VAPID (format URL-safe base64) doit être convertie en Uint8Array pour l'API PushManager.
        _urlBase64ToUint8Array: function (base64String) {
            var padding = '='.repeat((4 - (base64String.length % 4)) % 4);
            var base64 = (base64String + padding).replace(/-/g, '+').replace(/_/g, '/');
            var rawData = window.atob(base64);
            var outputArray = new Uint8Array(rawData.length);
            for (var i = 0; i < rawData.length; ++i) {
                outputArray[i] = rawData.charCodeAt(i);
            }
            return outputArray;
        },
        isSupported: function () {
            return 'serviceWorker' in navigator && 'PushManager' in window
                && (window.isSecureContext || location.hostname === 'localhost');
        },
        // Retourne 'unsupported' | 'subscribed' | 'unsubscribed'
        getStatus: async function () {
            if (!kernelMK.push.isSupported()) return 'unsupported';
            try {
                var reg = await navigator.serviceWorker.getRegistration('/');
                if (!reg) return 'unsubscribed';
                var sub = await reg.pushManager.getSubscription();
                return sub ? 'subscribed' : 'unsubscribed';
            } catch {
                return 'unsubscribed';
            }
        },
        subscribe: async function () {
            if (!kernelMK.push.isSupported()) return false;
            try {
                var keyResponse = await fetch('/api/push/vapid-public-key', { credentials: 'same-origin' });
                var vapidPublicKey = (await keyResponse.text()).trim();
                if (!vapidPublicKey) {
                    console.error('Notifications push non configurées côté serveur (clé VAPID absente).');
                    return false;
                }

                var reg = await navigator.serviceWorker.register('/sw.js');
                await navigator.serviceWorker.ready;

                var subscription = await reg.pushManager.subscribe({
                    userVisibleOnly: true,
                    applicationServerKey: kernelMK.push._urlBase64ToUint8Array(vapidPublicKey)
                });

                var json = subscription.toJSON();
                var response = await fetch('/api/push/subscribe', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ endpoint: json.endpoint, keys: json.keys })
                });
                return response.ok;
            } catch (err) {
                console.error('Échec d\'abonnement aux notifications push :', err);
                return false;
            }
        },
        unsubscribe: async function () {
            if (!kernelMK.push.isSupported()) return false;
            try {
                var reg = await navigator.serviceWorker.getRegistration('/');
                if (!reg) return true;
                var subscription = await reg.pushManager.getSubscription();
                if (!subscription) return true;

                var endpoint = subscription.endpoint;
                await subscription.unsubscribe();
                await fetch('/api/push/unsubscribe', {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ endpoint: endpoint })
                });
                return true;
            } catch (err) {
                console.error('Échec de désabonnement des notifications push :', err);
                return false;
            }
        }
    }
};

// Raccourci clavier global Ctrl+K / Cmd+K : ouvre la recherche globale depuis n'importe quelle page.
document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
        e.preventDefault();
        if (kernelMK.globalSearch.dotNetRef) {
            kernelMK.globalSearch.dotNetRef.invokeMethodAsync('ToggleOpen');
        }
    }
});

// Fermeture automatique des menus <details class="kmk-dropdown"> lors d'un clic en dehors
document.addEventListener('click', function (e) {
    document.querySelectorAll('details.kmk-dropdown[open]').forEach(function (d) {
        if (!d.contains(e.target)) {
            d.removeAttribute('open');
        }
    });
});

