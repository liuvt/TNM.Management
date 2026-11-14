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

// Download standard OpenXML Excel (.xlsx) from Base64 bytes.
window.tnmDownloadExcel = function (fileName, base64Data) {
    const binary = window.atob(base64Data);
    const len = binary.length;
    const bytes = new Uint8Array(len);
    for (let i = 0; i < len; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    const blob = new Blob([bytes], {
        type: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'
    });
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

    // ==================== GLOBAL CONFIRMATION DIALOG (ADD / EDIT / DELETE) ====================
    let confirmOverlayEl = null;
    let pendingTargetBtn = null;

    function ensureConfirmModal() {
        if (confirmOverlayEl && document.body.contains(confirmOverlayEl)) return confirmOverlayEl;
        const overlay = document.createElement('div');
        overlay.id = 'tnm-confirm-overlay';
        overlay.className = 'tnm-confirm-overlay';
        overlay.hidden = true;
        overlay.innerHTML = `
            <div class="tnm-confirm-dialog" role="dialog" aria-modal="true" aria-labelledby="tnm-confirm-title">
                <div class="tnm-confirm-header">
                    <div id="tnm-confirm-icon" class="tnm-confirm-icon type-edit">!</div>
                    <div style="min-width:0;flex:1">
                        <span id="tnm-confirm-badge" class="tnm-confirm-badge type-edit">XÁC NHẬN THAO TÁC</span>
                        <h3 id="tnm-confirm-title" class="tnm-confirm-title">Xác nhận thực hiện</h3>
                    </div>
                </div>
                <div id="tnm-confirm-body" class="tnm-confirm-body"></div>
                <div class="tnm-confirm-actions">
                    <button type="button" id="tnm-confirm-cancel" class="btn secondary" data-confirm="none">Hủy bỏ</button>
                    <button type="button" id="tnm-confirm-ok" class="btn" data-confirm="none">Xác nhận thực hiện</button>
                </div>
            </div>
        `;
        document.body.appendChild(overlay);

        const cancelBtn = overlay.querySelector('#tnm-confirm-cancel');
        const okBtn = overlay.querySelector('#tnm-confirm-ok');

        cancelBtn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            closeConfirmModal();
        });

        overlay.addEventListener('click', function (ev) {
            if (ev.target === overlay) {
                closeConfirmModal();
            }
        });

        okBtn.addEventListener('click', function (ev) {
            ev.stopPropagation();
            const btnToClick = pendingTargetBtn;
            closeConfirmModal();
            if (btnToClick && document.body.contains(btnToClick) && !btnToClick.disabled) {
                btnToClick.dataset.tnmConfirmed = '1';
                btnToClick.click();
            }
        });

        window.addEventListener('keydown', function (ev) {
            if (!confirmOverlayEl || confirmOverlayEl.hidden) return;
            if (ev.key === 'Escape') {
                ev.preventDefault();
                closeConfirmModal();
            }
        });

        confirmOverlayEl = overlay;
        return overlay;
    }

    function closeConfirmModal() {
        if (confirmOverlayEl) {
            confirmOverlayEl.hidden = true;
        }
        pendingTargetBtn = null;
    }

    function classifyButtonForConfirm(btn) {
        if (!btn || btn.disabled) return null;
        if (btn.closest('#tnm-confirm-overlay')) return null;
        if (btn.dataset.confirm === 'none') return null;

        const rawText = (btn.innerText || btn.textContent || '').replace(/\s+/g, ' ').trim();
        const lower = rawText.toLowerCase();

        // Explicit data-confirm attribute takes highest priority
        const explicitType = (btn.dataset.confirm || '').toLowerCase();
        if (explicitType === 'add' || explicitType === 'edit' || explicitType === 'delete') {
            return {
                type: explicitType,
                title: btn.dataset.confirmTitle || (
                    explicitType === 'delete' ? 'Xác nhận Xóa dữ liệu' :
                    explicitType === 'add' ? 'Xác nhận Thêm mới dữ liệu' :
                    'Xác nhận Cập nhật / Chỉnh sửa'
                ),
                message: btn.dataset.confirmMsg || `Bạn có chắc chắn muốn thực hiện thao tác "${rawText}" không?`
            };
        }

        // Ignore navigation, sidebar, topbar, tabs, summary filter bars, and pagination controls
        if (btn.closest('.sidebar, .topbar, .tabs, .device-summary-bar, .device-due10-note, .table-pager, .catalog-pagination')) return null;
        if (btn.classList.contains('mobile-scrim') || btn.classList.contains('mobile-toggle')) return null;
        if (/^(«|‹|›|»|\d+$)/.test(rawText) || rawText === '« Đầu' || rawText === '‹ Trước' || rawText === 'Sau ›' || rawText === 'Cuối »') return null;

        // Ignore pure UI toggle / filter / search / close / cancel / view detail buttons
        if (!rawText) return null;
        if (
            lower === 'đóng' ||
            lower === '✕ đóng' ||
            lower.startsWith('đóng ') ||
            lower.startsWith('đóng ·') ||
            lower === 'hủy' ||
            lower === 'hủy bỏ' ||
            lower === 'tìm kiếm' ||
            lower === 'xóa lọc' ||
            lower === 'xóa lọc sp' ||
            lower === 'trước / sau' ||
            lower === 'chi tiết' ||
            lower === '👁 xem' ||
            lower === '👁 xem chi tiết' ||
            lower === '👁 xem / sửa imei' ||
            lower === 'nhập imei' ||
            lower.startsWith('📝 bổ sung imei') ||
            lower.startsWith('⚙ điều chỉnh') ||
            lower === 'làm mới' ||
            lower === 'làm mới danh mục' ||
            lower === 'nạp quyền user' ||
            lower === 'hiện tại' ||
            lower === 'điền mẫu thử nghiệm' ||
            lower === 'tự động điền vào đơn hàng' ||
            lower.startsWith('xuất ') ||
            lower.startsWith('📥 xuất') ||
            lower.startsWith('⬇ xuất') ||
            lower.includes('xuất excel') ||
            lower.includes('xuất data') ||
            lower.includes('dán nhanh tin nhắn') ||
            rawText.endsWith('→')
        ) {
            return null;
        }

        // Ignore buttons in .page-head or .crud-actionbar that only open/toggle a form panel
        const inActionbarOrHead = !!btn.closest('.crud-actionbar, .page-head');
        if (inActionbarOrHead && !lower.includes('đồng bộ') && !lower.includes('chốt số')) {
            return null;
        }

        // Ignore table row buttons that only load/open the edit or quick-pay form without mutating DB yet
        const inFormActions = !!btn.closest('.form-actions, .so-modal-footer');
        if (!inFormActions) {
            if (
                lower === 'sửa' ||
                lower === '✏️ sửa' ||
                lower === 'cấu hình & sửa' ||
                lower === 'phân quyền / sửa' ||
                lower === 'sửa hạn / tt' ||
                lower === '+ gia hạn' ||
                lower === 'gửi ncc' ||
                lower === 'hoàn tất / trả' ||
                lower === 'chỉnh sửa quyền role' ||
                lower === '+ thêm nhân viên' ||
                lower === '+ tạo nhà cung cấp mới' ||
                lower === 'thu khách' ||
                lower === 'chi nhập hàng' ||
                lower === 'chi trả' ||
                lower.startsWith('+ lập chứng từ công nợ') ||
                lower.startsWith('+ lập công nợ hoa hồng')
            ) {
                return null;
            }
        }

        // Ignore helper preset buttons inside forms (e.g. "Tất toán 100%")
        if (lower.startsWith('tất toán 100%')) return null;

        // 1. DELETE / CANCEL / DISABLE actions
        if (
            lower === 'xóa' ||
            lower === '🗑 xóa' ||
            lower.includes('xóa cứng') ||
            lower.includes('hủy & xóa') ||
            lower.includes('hủy đơn') ||
            lower.startsWith('xác nhận xóa') ||
            lower === 'ngừng bán' ||
            lower.startsWith('xác nhận ngừng bán') ||
            lower === 'đặt lại 0 ₫'
        ) {
            return {
                type: 'delete',
                title: lower.includes('hủy') ? 'Xác nhận Hủy / Xóa đơn hàng' :
                       lower.includes('ngừng bán') ? 'Xác nhận Ngừng bán sản phẩm' :
                       'Xác nhận Xóa dữ liệu',
                message: `Bạn có chắc chắn muốn thực hiện thao tác <strong>"${rawText}"</strong> không?`
            };
        }

        // 2. ADD / CREATE / RECEIVE / RENEW actions
        if (
            lower.startsWith('+ tạo') ||
            lower.startsWith('tạo ') ||
            lower.startsWith('+ thêm') ||
            lower.startsWith('+ lập') ||
            lower.startsWith('+ nhập kho') ||
            lower.startsWith('+ mở phiên') ||
            lower.startsWith('+ gia hạn') ||
            lower.startsWith('+ ghi nhận') ||
            lower === 'ghi phiếu' ||
            lower === 'lưu nhân viên' ||
            lower === 'lưu và chọn ncc'
        ) {
            return {
                type: 'add',
                title: 'Xác nhận Thêm mới / Ghi nhận dữ liệu',
                message: `Bạn có chắc chắn muốn thực hiện thao tác <strong>"${rawText}"</strong> không?`
            };
        }

        // 3. EDIT / SAVE / UPDATE / COMPLETE / APPROVE / TRANSFER / LOCK actions
        if (
            inFormActions ||
            lower.includes('lưu') ||
            lower.includes('cập nhật') ||
            lower.includes('hoàn thành') ||
            lower.includes('điều chuyển') ||
            lower.includes('duyệt') ||
            lower.includes('nhận đủ') ||
            lower.includes('chốt số') ||
            lower.includes('mở khóa') ||
            lower.includes('đồng bộ') ||
            lower.includes('import') ||
            lower === 'áp dụng' ||
            /^\d+k$/i.test(rawText)
        ) {
            return {
                type: 'edit',
                title: 'Xác nhận Cập nhật / Thực thi',
                message: `Bạn có chắc chắn muốn thực hiện thao tác <strong>"${rawText}"</strong> không?`
            };
        }

        return null;
    }

    function openConfirmModal(btn, info) {
        const overlay = ensureConfirmModal();
        pendingTargetBtn = btn;

        const iconEl = overlay.querySelector('#tnm-confirm-icon');
        const badgeEl = overlay.querySelector('#tnm-confirm-badge');
        const titleEl = overlay.querySelector('#tnm-confirm-title');
        const bodyEl = overlay.querySelector('#tnm-confirm-body');
        const okBtn = overlay.querySelector('#tnm-confirm-ok');

        iconEl.className = `tnm-confirm-icon type-${info.type}`;
        badgeEl.className = `tnm-confirm-badge type-${info.type}`;

        if (info.type === 'delete') {
            iconEl.textContent = '✕';
            badgeEl.textContent = 'XÁC NHẬN XÓA / HỦY';
            okBtn.textContent = 'Xác nhận xóa';
            okBtn.classList.add('tnm-confirm-btn-danger');
        } else if (info.type === 'add') {
            iconEl.textContent = '+';
            badgeEl.textContent = 'XÁC NHẬN THÊM MỚI';
            okBtn.textContent = 'Xác nhận thêm mới';
            okBtn.classList.remove('tnm-confirm-btn-danger');
        } else {
            iconEl.textContent = '✓';
            badgeEl.textContent = 'XÁC NHẬN CẬP NHẬT';
            okBtn.textContent = 'Xác nhận lưu / thực hiện';
            okBtn.classList.remove('tnm-confirm-btn-danger');
        }

        titleEl.textContent = info.title;
        if (btn.dataset.confirmMsg) {
            bodyEl.textContent = info.message;
        } else {
            bodyEl.innerHTML = info.message;
        }

        overlay.hidden = false;
        setTimeout(() => okBtn.focus(), 20);
    }

    // Intercept button clicks in window capture phase (runs before document / Blazor event delegation)
    window.addEventListener('click', function (e) {
        if (isFlushingQueue || e.__tnmReplay) return;
        const btn = e.target && e.target.closest ? e.target.closest('button') : null;
        if (!btn || btn.disabled) return;

        if (btn.dataset.tnmConfirmed === '1') {
            delete btn.dataset.tnmConfirmed;
            return;
        }

        const confirmInfo = classifyButtonForConfirm(btn);
        if (confirmInfo) {
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();
            openConfirmModal(btn, confirmInfo);
            return;
        }

        // Auto-scroll to opened .edit-panel when clicking row-level edit/open buttons
        const rawText = (btn.innerText || btn.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
        if (
            rawText === 'sửa' ||
            rawText === '✏️ sửa' ||
            rawText === 'cấu hình & sửa' ||
            rawText === 'phân quyền / sửa' ||
            rawText === 'sửa hạn / tt' ||
            rawText === '+ gia hạn' ||
            rawText === 'gửi ncc' ||
            rawText === 'hoàn tất / trả' ||
            rawText === 'thu khách' ||
            rawText === 'chi nhập hàng' ||
            rawText === 'chi trả'
        ) {
            setTimeout(() => {
                const panels = Array.from(document.querySelectorAll('.edit-panel:not([hidden])'));
                const targetPanel = panels[panels.length - 1];
                if (targetPanel) {
                    targetPanel.scrollIntoView({ behavior: 'smooth', block: 'center' });
                }
            }, 120);
        }
    }, true);

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

    function normPath(p) {
        if (!p) return '/';
        const clean = String(p).split('?')[0].split('#')[0].replace(/\/+$/, '');
        return clean.startsWith('/') ? (clean || '/') : ('/' + clean);
    }

    function getRenderedPath() {
        const mainEl = document.querySelector('main.content[data-rendered-path]');
        if (!mainEl) return null;
        return normPath(mainEl.getAttribute('data-rendered-path'));
    }

    let navWatchdogTimer1 = null;
    let navWatchdogTimer2 = null;

    function clearNavWatchdogs() {
        if (navWatchdogTimer1) { clearTimeout(navWatchdogTimer1); navWatchdogTimer1 = null; }
        if (navWatchdogTimer2) { clearTimeout(navWatchdogTimer2); navWatchdogTimer2 = null; }
    }

    function verifyRouteSyncedOrReload(expectedHref) {
        const currentPath = normPath(window.location.pathname);
        const renderedPath = getRenderedPath();
        let expectedPath = currentPath;
        if (expectedHref) {
            try {
                expectedPath = normPath(new URL(expectedHref, window.location.origin).pathname);
            } catch {
                expectedPath = currentPath;
            }
        }
        if (!window.__tnmConnected) {
            window.location.replace(expectedHref || window.location.href);
            return true;
        }
        if (renderedPath && (renderedPath !== currentPath || renderedPath !== expectedPath)) {
            window.location.replace(expectedHref || window.location.href);
            return true;
        }
        return false;
    }

    let lastPingOkAt = Date.now();
    let pingInFlight = false;

    async function verifyCircuitAlive(pendingBtn) {
        if (!window.__tnmConnected) {
            triggerImmediateReconnect();
            return false;
        }
        if (!window.DotNet || typeof window.DotNet.invokeMethodAsync !== 'function') {
            return true;
        }
        if (pingInFlight) return true;
        pingInFlight = true;
        try {
            const res = await Promise.race([
                window.DotNet.invokeMethodAsync('TNM.Management', 'TnmPing'),
                new Promise((_, reject) => setTimeout(() => reject(new Error('ping-timeout')), 1500))
            ]);
            if (res === 'ok') {
                lastPingOkAt = Date.now();
                return true;
            }
        } catch {
            // Circuit is dead or unresponsive
        } finally {
            pingInFlight = false;
        }

        window.__tnmConnected = false;
        if (pendingBtn && !pendingBtn.disabled) {
            document.querySelectorAll('input, select, textarea').forEach(el => {
                if (!el.readOnly && !el.disabled) recordInputChange(el);
            });
            recordButtonClick(pendingBtn);
        }
        triggerImmediateReconnect();
        return false;
    }

    function scheduleNavWatchdog(targetHref) {
        clearNavWatchdogs();
        navWatchdogTimer1 = setTimeout(() => {
            if (!window.__tnmConnected) {
                window.location.replace(targetHref || window.location.href);
            }
        }, 350);
        navWatchdogTimer2 = setTimeout(() => {
            verifyRouteSyncedOrReload(targetHref || window.location.href);
        }, 850);
    }

    // Intercept internal navigation <a> clicks in capture phase so a sleeping/dead circuit never leaves URL and DOM out of sync
    window.addEventListener('click', function (e) {
        if (e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) return;
        const anchor = e.target && e.target.closest ? e.target.closest('a[href]') : null;
        if (!anchor) return;
        if (anchor.target && anchor.target !== '_self') return;
        if (anchor.hasAttribute('download') || anchor.getAttribute('rel') === 'external') return;

        const rawHref = anchor.getAttribute('href') || '';
        if (!rawHref || rawHref.startsWith('#') || rawHref.startsWith('javascript:') || rawHref.startsWith('mailto:') || rawHref.startsWith('tel:')) return;

        let targetUrl;
        try {
            targetUrl = new URL(anchor.href, window.location.origin);
        } catch {
            return;
        }
        if (targetUrl.origin !== window.location.origin) return;

        // If circuit is currently disconnected, bypass Blazor's pushState interception and do a clean browser navigation
        if (!window.__tnmConnected) {
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();
            window.location.assign(targetUrl.href);
            return;
        }

        // Even if __tnmConnected appears true (e.g. tab just woke up from sleep), watch the route transition
        // and force full navigation if the server circuit fails to render the new route within 850ms.
        scheduleNavWatchdog(targetUrl.href);
    }, true);

    window.addEventListener('popstate', function () {
        if (!window.__tnmConnected) {
            window.location.replace(window.location.href);
            return;
        }
        scheduleNavWatchdog(window.location.href);
    });

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
            return;
        }
        if (Date.now() - lastPingOkAt > 10000) {
            verifyCircuitAlive(btn);
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

    function saveCurrentInputsForReload() {
        try {
            document.querySelectorAll('input, select, textarea').forEach(el => {
                if (!el.readOnly && !el.disabled && el.value) recordInputChange(el);
            });
        } catch {
            // ignore
        }
    }

    async function reconnectWithTimeout(timeoutMs) {
        if (!window.Blazor || typeof window.Blazor.reconnect !== 'function') return null;
        return await Promise.race([
            window.Blazor.reconnect(),
            new Promise((_, reject) => setTimeout(() => reject(new Error('timeout')), timeoutMs))
        ]);
    }

    async function triggerImmediateReconnect() {
        if (window.__tnmConnected || reconnectingLoop) return;
        reconnectingLoop = true;
        let attempts = 0;
        try {
            while (!window.__tnmConnected) {
                attempts++;
                try {
                    const ok = await reconnectWithTimeout(3500);
                    if (ok === true) {
                        window.__tnmConnected = true;
                        verifyRouteSyncedOrReload(window.location.href);
                        await flushOfflineQueue();
                        return;
                    }
                    if (ok === false) {
                        // Circuit was rejected/expired on the server (e.g., long idle or server restart).
                        // Reload immediately so a fresh circuit is established at the current URL.
                        saveCurrentInputsForReload();
                        window.location.replace(window.location.href);
                        return;
                    }
                } catch {
                    // Network error or timeout
                }
                if (attempts >= 3 && navigator.onLine !== false) {
                    saveCurrentInputsForReload();
                    window.location.replace(window.location.href);
                    return;
                }
                await new Promise(r => setTimeout(r, 900));
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
            } else if (document.visibilityState === 'visible' && Date.now() - lastPingOkAt > 20000) {
                verifyCircuitAlive();
            }
        };
    } catch {
        // Fallback if Worker is restricted
    }

    function onTabWakeUp() {
        if (verifyRouteSyncedOrReload(window.location.href)) return;
        if (!window.__tnmConnected) {
            triggerImmediateReconnect();
            return;
        }
        verifyCircuitAlive();
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            onTabWakeUp();
        }
    });
    window.addEventListener('focus', onTabWakeUp);
    window.addEventListener('pageshow', function (ev) {
        if (ev.persisted) {
            window.location.reload();
            return;
        }
        onTabWakeUp();
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
                        verifyRouteSyncedOrReload(window.location.href);
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
