// Custom Blazor (Server circuit) startup + reconnection handling.
//
// The framework's default reconnection UI flashes an "Attempting to connect to the server…" modal on
// every transient circuit blip — including the brief drops that happen during ordinary navigation —
// which looks broken to users. We replace it with a SILENT handler: when the circuit drops we quietly
// retry in the background and show NOTHING. Only if every retry fails do we reveal a real error card
// (#daleel-reconnect) that offers a reload and a link to the status page.
//
// Requires the Blazor script tag to carry `autostart="false"` so we can pass these options to start().
(() => {
    const maxRetries = 8;                 // ~8 silent attempts before we admit defeat…
    const retryIntervalMs = 2000;         // …spaced 2s apart (≈16s of quiet retrying).
    const modal = document.getElementById('daleel-reconnect');

    const showModal = () => { if (modal) modal.classList.add('daleel-reconnect-show'); };
    const hideModal = () => { if (modal) modal.classList.remove('daleel-reconnect-show'); };

    const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

    // Drives the silent retry loop. Returns an object with cancel() so onConnectionUp can stop it.
    const startReconnecting = () => {
        let cancelled = false;

        (async () => {
            for (let i = 0; i < maxRetries; i++) {
                await delay(retryIntervalMs);
                if (cancelled) return;

                try {
                    const reconnected = await Blazor.reconnect();
                    if (!reconnected) {
                        // Server was reachable but rejected the circuit (server restarted / state gone):
                        // a fresh load is the only recovery.
                        location.reload();
                        return;
                    }
                    return; // Reconnected silently — user never saw a thing.
                } catch {
                    // Couldn't reach the server at all; keep retrying quietly.
                }
            }

            // Exhausted every quick retry: surface the error card — but keep watching. A QA/prod
            // DEPLOY takes the server away for longer than the quick window (~16s); when it comes
            // back the circuit is gone for good, so the moment /health answers we reload and the
            // tab heals itself instead of sitting on a dead overlay.
            if (cancelled) return;
            showModal();
            while (!cancelled) {
                await delay(3000);
                if (cancelled) return;
                try {
                    const health = await fetch('/health', { cache: 'no-store' });
                    if (health.ok) { location.reload(); return; }
                } catch {
                    // Still down; keep waiting.
                }
            }
        })();

        return {
            cancel: () => { cancelled = true; hideModal(); },
        };
    };

    let current = null;

    // Manual "Try again" button: re-run the silent loop from scratch.
    const retryButton = document.getElementById('daleel-reconnect-retry');
    if (retryButton) {
        retryButton.addEventListener('click', () => {
            hideModal();
            current?.cancel();
            current = startReconnecting();
        });
    }

    Blazor.start({
        circuit: {
            reconnectionHandler: {
                onConnectionDown: () => { current ??= startReconnecting(); },
                onConnectionUp: () => { current?.cancel(); current = null; },
            },
        },
    });
})();
