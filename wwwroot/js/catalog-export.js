// Download on direct click only. UTF-8 BOM enables Vietnamese CSV in Excel for Windows.
window.tnmDownloadCsv = function (fileName, content) {
    const blob = new Blob(['\uFEFF', content], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = String(fileName).replace(/[^a-zA-Z0-9_.-]/g, '_');
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    setTimeout(() => URL.revokeObjectURL(url), 2000);
};

window.tnmScrollCatalogEditor = function () {
    document.getElementById('catalog-editor')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

(function () {
    const STORAGE_INPUTS_KEY = 'tnm_pending_inputs_v1';
    const STORAGE_CLICKS_KEY = 'tnm_pending_clicks_v1';

    window.__tnmConnected = true;
    let reconnectingLoop = false;
    let isFlushingQueue = false;

    function loadJson(key, fallback) {
        try {
            const raw = localStorage.getItem(key);
            return raw ? JSON.parse(raw) : fallback;
        } catch {
            return fallback;
        }
    }

    function saveJson(key, value) {
        try {
            localStorage.setItem(key, JSON.stringify(value));
        } catch {
            // ignore quota errors
        }
    }

    function getElementLocator(el) {
        if (!el || !el.tagName) return null;
        if (el.id) return { by: 'id', value: el.id };
        const path = [];
        let cur = el;
        while (cur && cur !== document.body && cur.parentElement) {
            const tag = cur.tagName.toLowerCase();
            const siblings = Array.from(cur.parentElement.children).filter(c => c.tagName === cur.tagName);
            const idx = siblings.indexOf(cur);
            path.unshift(`${tag}:nth-of-type(${idx + 1})`);
            cur = cur.parentElement;
        }
        return { by: 'selector', value: path.join(' > ') };
    }

    function resolveLocator(loc) {
        if (!loc) return null;
        try {
            if (loc.by === 'id') return document.getElementById(loc.value);
            if (loc.by === 'selector') return document.querySelector(loc.value);
        } catch {
            return null;
        }
        return null;
    }

    function recordInputChange(el) {
        const loc = getElementLocator(el);
        if (!loc) return;
        const queue = loadJson(STORAGE_INPUTS_KEY, []);
        const entry = {
            pathname: location.pathname,
            locator: loc,
            tag: el.tagName.toLowerCase(),
            type: (el.type || '').toLowerCase(),
            value: el.value,
            checked: !!el.checked,
            ts: Date.now()
        };
        const existingIdx = queue.findIndex(
            x => x.pathname === entry.pathname && x.locator.by === loc.by && x.locator.value === loc.value
        );
        if (existingIdx >= 0) {
            queue[existingIdx] = entry;
        } else {
            queue.push(entry);
        }
        saveJson(STORAGE_INPUTS_KEY, queue);
    }

    function recordButtonClick(btn) {
        const loc = getElementLocator(btn);
        if (!loc) return;
        const queue = loadJson(STORAGE_CLICKS_KEY, []);
        queue.push({
            pathname: location.pathname,
            locator: loc,
            text: (btn.innerText || btn.textContent || '').trim(),
            ts: Date.now()
        });
        saveJson(STORAGE_CLICKS_KEY, queue);
    }

    // Listen to user edits & button clicks in capture phase.
    // When disconnected, buffer locally so actions are replayed into the database once reconnected.
    document.addEventListener('input', function (e) {
        if (isFlushingQueue || e.__tnmReplay) return;
        const t = e.target;
        if (!t || !['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return;
        if (!window.__tnmConnected) {
            recordInputChange(t);
            triggerImmediateReconnect();
        }
    }, true);

    document.addEventListener('change', function (e) {
        if (isFlushingQueue || e.__tnmReplay) return;
        const t = e.target;
        if (!t || !['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return;
        if (!window.__tnmConnected) {
            recordInputChange(t);
            triggerImmediateReconnect();
        }
    }, true);

    document.addEventListener('click', function (e) {
        if (isFlushingQueue || e.__tnmReplay) return;
        const btn = e.target && e.target.closest ? e.target.closest('button') : null;
        if (!btn || btn.disabled) return;
        if (!window.__tnmConnected) {
            // Capture all current input values on the page first so @bind state is complete
            document.querySelectorAll('input, select, textarea').forEach(el => {
                if (!el.readOnly && !el.disabled) recordInputChange(el);
            });
            recordButtonClick(btn);
            triggerImmediateReconnect();
        }
    }, true);

    async function flushOfflineQueue() {
        if (isFlushingQueue || !window.__tnmConnected) return;
        const inputs = loadJson(STORAGE_INPUTS_KEY, []).filter(x => x.pathname === location.pathname);
        const clicks = loadJson(STORAGE_CLICKS_KEY, []).filter(x => x.pathname === location.pathname);
        if (inputs.length === 0 && clicks.length === 0) return;

        isFlushingQueue = true;
        try {
            // 1. Replay buffered input/select/textarea values so Blazor Server @bind updates component fields
            for (const item of inputs) {
                const el = resolveLocator(item.locator);
                if (!el) continue;
                if (item.type === 'checkbox' || item.type === 'radio') {
                    el.checked = !!item.checked;
                } else {
                    el.value = item.value ?? '';
                }
                const evInput = new Event('input', { bubbles: true });
                evInput.__tnmReplay = true;
                el.dispatchEvent(evInput);

                const evChange = new Event('change', { bubbles: true });
                evChange.__tnmReplay = true;
                el.dispatchEvent(evChange);

                await new Promise(r => setTimeout(r, 60));
            }
            saveJson(STORAGE_INPUTS_KEY, []);

            // 2. Wait briefly for Blazor Server render cycle to process @bind updates, then replay queued clicks
            if (clicks.length > 0) {
                await new Promise(r => setTimeout(r, 180));
                for (const item of clicks) {
                    let btn = resolveLocator(item.locator);
                    if (!btn && item.text) {
                        btn = Array.from(document.querySelectorAll('button')).find(
                            b => (b.innerText || b.textContent || '').trim() === item.text
                        );
                    }
                    if (btn && !btn.disabled) {
                        const evClick = new MouseEvent('click', { bubbles: true, cancelable: true, view: window });
                        evClick.__tnmReplay = true;
                        btn.dispatchEvent(evClick);
                        await new Promise(r => setTimeout(r, 250));
                    }
                }
                saveJson(STORAGE_CLICKS_KEY, []);
            }
        } finally {
            isFlushingQueue = false;
        }
    }

    async function triggerImmediateReconnect() {
        if (window.__tnmConnected || reconnectingLoop) return;
        reconnectingLoop = true;
        try {
            while (!window.__tnmConnected) {
                try {
                    if (window.Blazor && typeof window.Blazor.reconnect === 'function') {
                        const ok = await window.Blazor.reconnect();
                        if (ok) {
                            window.__tnmConnected = true;
                            await flushOfflineQueue();
                            return;
                        }
                    }
                } catch {
                    // ignore and retry silently
                }
                await new Promise(r => setTimeout(r, 1000));
            }
        } finally {
            reconnectingLoop = false;
        }
    }

    // Background Web Worker heartbeat so Chrome background tab throttling never freezes keep-alive
    try {
        const workerCode = `setInterval(() => postMessage('tick'), 15000);`;
        const blob = new Blob([workerCode], { type: 'application/javascript' });
        const worker = new Worker(URL.createObjectURL(blob));
        worker.onmessage = function () {
            if (!window.__tnmConnected) {
                triggerImmediateReconnect();
            }
        };
    } catch {
        // Fallback if Worker is restricted
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible' && !window.__tnmConnected) {
            triggerImmediateReconnect();
        }
    });
    window.addEventListener('focus', function () {
        if (!window.__tnmConnected) triggerImmediateReconnect();
    });
    window.addEventListener('online', function () {
        if (!window.__tnmConnected) triggerImmediateReconnect();
    });

    // Start Blazor with custom silent reconnection handler
    function startBlazorSilent() {
        if (!window.Blazor || typeof window.Blazor.start !== 'function') return;
        window.Blazor.start({
            circuit: {
                reconnectionOptions: {
                    maxRetries: 1000000,
                    retryIntervalMilliseconds: 1000
                },
                reconnectionHandler: {
                    onConnectionDown: function () {
                        window.__tnmConnected = false;
                        // Hide any modal if ever created
                        const modal = document.getElementById('components-reconnect-modal');
                        if (modal) modal.style.display = 'none';
                        triggerImmediateReconnect();
                    },
                    onConnectionUp: function () {
                        window.__tnmConnected = true;
                        flushOfflineQueue();
                    }
                }
            }
        }).then(() => {
            window.__tnmConnected = true;
            flushOfflineQueue();
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', startBlazorSilent);
    } else {
        startBlazorSilent();
    }
})();
