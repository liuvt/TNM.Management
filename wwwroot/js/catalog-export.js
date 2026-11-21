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

window.tnmOpenDateTimePicker = function (inputId) {
    const el = document.getElementById(inputId);
    if (!el) return;
    try {
        if (typeof el.showPicker === 'function') {
            el.showPicker();
            return;
        }
    } catch (_) { }
    el.focus();
    el.click();
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
        if (btn.closest('#tnm-confirm-overlay, #tnm-row-detail-overlay')) return null;
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
        if (btn.closest('.sidebar, .topbar, .tabs, .device-summary-bar, .device-due10-note, .table-pager, .catalog-pagination, .erp-filter-bar, .erp-picker-popover, .erp-segmented-tabs, .erp-master-period')) return null;
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
            lower === 'xem chi tiết' ||
            lower.startsWith('bảng kê') ||
            lower === '👁 xem' ||
            lower === '👁 xem chi tiết' ||
            lower === '👁 xem / sửa imei' ||
            lower === 'nhập imei' ||
            lower.startsWith('📝 bổ sung imei') ||
            lower.startsWith('⚙ điều chỉnh') ||
            lower === 'làm mới' ||
            lower === 'làm mới danh mục' ||
            lower === 'nạp quyền user' ||
            lower === 'nạp quyền tài khoản' ||
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
                lower === 'chỉnh sửa quyền vai trò' ||
                lower === 'đóng quyền vai trò' ||
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
            rawText === 'chi trả' ||
            rawText === 'chi tiết' ||
            rawText.startsWith('👁 xem')
        ) {
            setTimeout(() => {
                const invDetail = document.getElementById('finance-invoice-detail-panel');
                if (invDetail && (rawText === 'chi tiết' || rawText.startsWith('👁 xem'))) {
                    invDetail.scrollIntoView({ behavior: 'smooth', block: 'start' });
                    return;
                }
                const panels = Array.from(document.querySelectorAll('.edit-panel:not([hidden])'));
                const targetPanel = panels[panels.length - 1];
                if (targetPanel) {
                    targetPanel.scrollIntoView({ behavior: 'smooth', block: 'center' });
                }
            }, 120);
        }
    }, true);

    // ==================== GLOBAL DOUBLE-CLICK TABLE ROW DETAIL MODAL (SYSTEM-WIDE) ====================
    let rowDetailOverlayEl = null;
    let currentDetailRow = null;
    let currentDataRows = [];
    let currentRowIndex = -1;

    function copyTextToClipboard(text) {
        const val = String(text || '').trim();
        if (!val) return Promise.resolve(false);
        if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
            return navigator.clipboard.writeText(val).then(() => true).catch(() => fallbackCopyText(val));
        }
        return Promise.resolve(fallbackCopyText(val));
    }

    function fallbackCopyText(val) {
        try {
            const ta = document.createElement('textarea');
            ta.value = val;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            ta.remove();
            return true;
        } catch {
            return false;
        }
    }

    function isDataTableRow(tr) {
        if (!tr || tr.tagName !== 'TR') return false;
        const parent = tr.parentElement;
        if (!parent || parent.tagName !== 'TBODY') return false;
        if (tr.hidden || tr.style.display === 'none') return false;
        if (tr.closest('#tnm-row-detail-overlay, #tnm-confirm-overlay, .erp-picker-popover')) return false;
        const cells = tr.cells;
        if (!cells || cells.length <= 1) return false;
        if (cells.length === 1 && (cells[0].colSpan || 1) > 1) return false;
        // Skip inline multi-input form rows so typing/double-clicking inside form grids is never interrupted
        const editableInputs = tr.querySelectorAll('input:not([type="checkbox"]):not([type="radio"]):not([readonly]), select:not(:disabled), textarea:not([readonly])');
        if (editableInputs.length >= 2) return false;
        return true;
    }

    function extractTableHeaders(table) {
        const headers = [];
        if (!table) return headers;
        const thead = table.tHead;
        if (thead && thead.rows && thead.rows.length > 0) {
            const hRow = thead.rows[thead.rows.length - 1];
            Array.from(hRow.cells).forEach(th => {
                const span = Math.max(1, th.colSpan || 1);
                const rawLabel = (th.textContent || '').replace(/\s+/g, ' ').trim();
                const hasOnlyCheckbox = !!th.querySelector('input[type="checkbox"]') && !rawLabel;
                const isDetailOnly = th.classList.contains('col-detail-only');
                for (let i = 0; i < span; i++) {
                    headers.push({ label: rawLabel, isCheckboxCol: hasOnlyCheckbox, isDetailOnly });
                }
            });
        }
        return headers;
    }

    function extractStructuredPlainText(rootEl) {
        if (!rootEl) return '';
        const textClone = rootEl.cloneNode(true);
        textClone.querySelectorAll('br').forEach(br => br.replaceWith('\n'));
        textClone.querySelectorAll('div, p, li, .tnm-rd-line-entry').forEach(block => {
            block.prepend('\n');
        });
        return (textClone.textContent || '')
            .replace(/[ \t]+/g, ' ')
            .replace(/\n\s+/g, '\n')
            .replace(/\n{2,}/g, '\n')
            .trim();
    }

    function isActionColumnOrCell(headerLabel, td) {
        if (td.classList.contains('col-detail-only')) return false;
        const h = (headerLabel || '').toLowerCase();
        if (/^(thao tác|thao tác nhanh|thao tác kế toán|hành động|tác vụ|xử lý|chức năng|nhận hàng)$/i.test(h)) return true;
        if (td.classList.contains('catalog-row-actions')) return true;
        const btns = td.querySelectorAll('button, a.btn');
        if (btns.length > 0) {
            const clone = td.cloneNode(true);
            clone.querySelectorAll('button, a.btn').forEach(b => b.remove());
            const leftover = (clone.textContent || '').replace(/\s+/g, ' ').trim();
            if (!leftover) return true;
        }
        return false;
    }

    function sanitizeCellForDetail(td) {
        const clone = td.cloneNode(true);
        clone.querySelectorAll('button, input, textarea').forEach(el => el.remove());
        // Convert <select> (e.g. permission state dropdown) into readable badge text
        clone.querySelectorAll('select').forEach(sel => {
            const origSel = td.querySelector('select');
            const selectedOpt = origSel && origSel.selectedIndex >= 0 ? origSel.options[origSel.selectedIndex] : null;
            const span = document.createElement('span');
            span.className = 'chip blue-chip';
            span.textContent = selectedOpt ? selectedOpt.text.trim() : (origSel ? origSel.value : '—');
            sel.replaceWith(span);
        });
        clone.querySelectorAll('[style]').forEach(el => {
            el.style.maxWidth = '';
            el.style.overflow = '';
            el.style.textOverflow = '';
            el.style.whiteSpace = '';
        });
        const plainText = extractStructuredPlainText(clone);
        const html = plainText ? clone.innerHTML.trim() : '<span style="color:#94a3b8">—</span>';
        return { html, plainText };
    }

    function ensureRowDetailModal() {
        if (rowDetailOverlayEl && document.body.contains(rowDetailOverlayEl)) return rowDetailOverlayEl;
        const overlay = document.createElement('div');
        overlay.id = 'tnm-row-detail-overlay';
        overlay.className = 'tnm-rd-overlay';
        overlay.hidden = true;
        overlay.innerHTML = `
            <div class="tnm-rd-dialog" role="dialog" aria-modal="true" aria-labelledby="tnm-rd-title">
                <div class="tnm-rd-header">
                    <div class="tnm-rd-head-left">
                        <div id="tnm-rd-eyebrow" class="tnm-rd-eyebrow">CHI TIẾT BẢN GHI</div>
                        <div class="tnm-rd-title-row">
                            <h3 id="tnm-rd-title" class="tnm-rd-title">Chi tiết dòng dữ liệu</h3>
                            <span id="tnm-rd-status-slot"></span>
                        </div>
                    </div>
                    <div class="tnm-rd-head-right">
                        <div class="tnm-rd-nav">
                            <button type="button" id="tnm-rd-prev" class="tnm-rd-nav-btn" data-confirm="none" title="Xem dòng phía trên (Phím ↑)">‹ Trước</button>
                            <span id="tnm-rd-pos" class="tnm-rd-pos">1 / 1</span>
                            <button type="button" id="tnm-rd-next" class="tnm-rd-nav-btn" data-confirm="none" title="Xem dòng tiếp theo (Phím ↓)">Sau ›</button>
                        </div>
                        <button type="button" id="tnm-rd-close-top" class="tnm-rd-close" data-confirm="none" title="Đóng (ESC)">✕</button>
                    </div>
                </div>
                <div id="tnm-rd-body" class="tnm-rd-body"></div>
                <div class="tnm-rd-footer">
                    <div class="tnm-rd-foot-left">
                        <button type="button" id="tnm-rd-copy-all" class="btn secondary btn-sm" data-confirm="none">📋 Sao chép toàn bộ</button>
                        <span class="tnm-rd-hint">Phím ↑ / ↓ để chuyển dòng · ESC để đóng</span>
                    </div>
                    <div id="tnm-rd-actions" class="tnm-rd-actions"></div>
                </div>
            </div>
        `;
        document.body.appendChild(overlay);

        overlay.addEventListener('click', function (ev) {
            if (ev.target === overlay) closeRowDetailModal();
        });
        overlay.querySelector('#tnm-rd-close-top').addEventListener('click', closeRowDetailModal);
        overlay.querySelector('#tnm-rd-prev').addEventListener('click', function () { stepDetailRow(-1); });
        overlay.querySelector('#tnm-rd-next').addEventListener('click', function () { stepDetailRow(1); });

        window.addEventListener('keydown', function (ev) {
            if (!rowDetailOverlayEl || rowDetailOverlayEl.hidden) return;
            if (confirmOverlayEl && !confirmOverlayEl.hidden) return;
            if (ev.key === 'Escape') {
                ev.preventDefault();
                closeRowDetailModal();
            } else if (ev.key === 'ArrowUp') {
                ev.preventDefault();
                stepDetailRow(-1);
            } else if (ev.key === 'ArrowDown') {
                ev.preventDefault();
                stepDetailRow(1);
            }
        });

        rowDetailOverlayEl = overlay;
        return overlay;
    }

    function clearActiveDetailRowHighlight() {
        document.querySelectorAll('tr.tnm-rd-active-row').forEach(r => r.classList.remove('tnm-rd-active-row'));
    }

    function closeRowDetailModal() {
        if (rowDetailOverlayEl) {
            rowDetailOverlayEl.hidden = true;
        }
        clearActiveDetailRowHighlight();
        currentDetailRow = null;
        currentDataRows = [];
        currentRowIndex = -1;
    }

    function stepDetailRow(delta) {
        if (!currentDataRows.length) return;
        const nextIdx = currentRowIndex + delta;
        if (nextIdx < 0 || nextIdx >= currentDataRows.length) return;
        const targetTr = currentDataRows[nextIdx];
        if (targetTr && document.body.contains(targetTr)) {
            targetTr.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            openRowDetailModal(targetTr);
        }
    }

    function openRowDetailModal(tr) {
        if (!isDataTableRow(tr)) return;
        const overlay = ensureRowDetailModal();

        clearActiveDetailRowHighlight();
        tr.classList.add('tnm-rd-active-row');
        currentDetailRow = tr;

        const tbody = tr.parentElement;
        const table = tr.closest('table');
        currentDataRows = Array.from(tbody.children).filter(isDataTableRow);
        currentRowIndex = currentDataRows.indexOf(tr);

        // Update Prev / Next state
        const prevBtn = overlay.querySelector('#tnm-rd-prev');
        const nextBtn = overlay.querySelector('#tnm-rd-next');
        const posEl = overlay.querySelector('#tnm-rd-pos');
        prevBtn.disabled = currentRowIndex <= 0;
        nextBtn.disabled = currentRowIndex < 0 || currentRowIndex >= currentDataRows.length - 1;
        posEl.textContent = currentRowIndex >= 0 ? `Dòng ${currentRowIndex + 1} / ${currentDataRows.length}` : '1 / 1';

        // Section / Panel Title
        const container = tr.closest('.so-modal-dialog, .panel, section');
        const headingEl = (container && container.querySelector('.so-modal-header h2, .panel-head h2, h2, h3'))
            || document.querySelector('.page-head h1, h1');
        const sectionTitle = headingEl
            ? (headingEl.innerText || headingEl.textContent || '').replace(/\s+/g, ' ').trim()
            : 'Dữ liệu bảng';
        overlay.querySelector('#tnm-rd-eyebrow').textContent = `CHI TIẾT DÒNG DỮ LIỆU · ${sectionTitle}`;

        // Extract headers & cells
        const headers = extractTableHeaders(table);
        const fields = [];
        const rowActionElements = [];
        let statusChipHtml = '';
        let colCursor = 0;

        Array.from(tr.cells).forEach((td, cellIdx) => {
            const span = Math.max(1, td.colSpan || 1);
            const hMeta = headers[colCursor] || { label: `Cột ${cellIdx + 1}`, isCheckboxCol: false };
            colCursor += span;

            const hasOnlyCheckbox = !!td.querySelector('input[type="checkbox"]') && !(td.innerText || td.textContent || '').trim();
            if (hMeta.isCheckboxCol || hasOnlyCheckbox) return;

            if (isActionColumnOrCell(hMeta.label, td)) {
                td.querySelectorAll('button:not(:disabled), a[href]').forEach(actEl => {
                    if (actEl.offsetParent !== null || !actEl.hidden) {
                        rowActionElements.push(actEl);
                    }
                });
                return;
            }

            // Also capture any inline action button inside a normal cell before sanitizing
            td.querySelectorAll('button:not(:disabled)').forEach(actEl => {
                const txt = (actEl.innerText || actEl.textContent || '').trim();
                if (txt && txt.length <= 36) rowActionElements.push(actEl);
            });

            if (!statusChipHtml && /(trạng thái|tình trạng)/i.test(hMeta.label)) {
                const chip = td.querySelector('.chip');
                if (chip) statusChipHtml = chip.outerHTML;
            }

            const { html, plainText } = sanitizeCellForDetail(td);
            const label = hMeta.label || `Trường ${fields.length + 1}`;
            const isDetailOnlyCell = !!hMeta.isDetailOnly || td.classList.contains('col-detail-only');
            const isLong = isDetailOnlyCell
                || plainText.length > 68
                || (plainText.match(/\n/g) || []).length >= 2
                || /(ghi chú|nội dung|diễn giải|chi tiết|địa chỉ|luồng kế toán|mô tả|danh sách|lịch sử|sản phẩm|định mức)/i.test(label);

            fields.push({ label, html, plainText, isLong });
        });

        // Determine Primary Headline
        let primaryField = fields.find(f =>
            f.plainText &&
            !/^(stt|#|\d+)$/i.test(f.label) &&
            /(imei|mã|số phiếu|chứng từ|biển số|số po|đơn hàng|tên|đối tác|khách hàng|tài khoản|nhân viên)/i.test(f.label)
        );
        if (!primaryField) {
            primaryField = fields.find(f => f.plainText && !/^(stt|#)$/i.test(f.label)) || fields[0];
        }
        const firstLineTitle = primaryField && primaryField.plainText
            ? primaryField.plainText.split('\n')[0].trim()
            : `Bản ghi #${currentRowIndex + 1}`;
        overlay.querySelector('#tnm-rd-title').textContent = firstLineTitle.length > 90 ? firstLineTitle.slice(0, 90) + '…' : firstLineTitle;
        overlay.querySelector('#tnm-rd-status-slot').innerHTML = statusChipHtml;

        // Render structured field grid
        const bodyEl = overlay.querySelector('#tnm-rd-body');
        bodyEl.innerHTML = '';
        const gridEl = document.createElement('div');
        gridEl.className = 'tnm-rd-grid';

        fields.forEach(f => {
            const item = document.createElement('div');
            item.className = 'tnm-rd-item' + (f.isLong ? ' full-span' : '');

            const labelRow = document.createElement('div');
            labelRow.className = 'tnm-rd-label-row';

            const lbl = document.createElement('span');
            lbl.className = 'tnm-rd-label';
            lbl.textContent = f.label;
            labelRow.appendChild(lbl);

            if (f.plainText) {
                const copyBtn = document.createElement('button');
                copyBtn.type = 'button';
                copyBtn.className = 'tnm-rd-copy-btn';
                copyBtn.dataset.confirm = 'none';
                copyBtn.textContent = '📋 Sao chép';
                copyBtn.addEventListener('click', function (ev) {
                    ev.stopPropagation();
                    copyTextToClipboard(f.plainText).then(ok => {
                        if (!ok) return;
                        copyBtn.textContent = '✓ Đã chép';
                        copyBtn.classList.add('copied');
                        setTimeout(() => {
                            copyBtn.textContent = '📋 Sao chép';
                            copyBtn.classList.remove('copied');
                        }, 1300);
                    });
                });
                labelRow.appendChild(copyBtn);
            }

            const valDiv = document.createElement('div');
            valDiv.className = 'tnm-rd-value';
            valDiv.innerHTML = f.html;

            item.appendChild(labelRow);
            item.appendChild(valDiv);
            gridEl.appendChild(item);
        });
        bodyEl.appendChild(gridEl);

        // Capture adjacent expanded detail row if currently open in table (e.g. Activity Before/After JSON or Purchase lines)
        const nextSibling = tr.nextElementSibling;
        if (
            nextSibling &&
            nextSibling.tagName === 'TR' &&
            nextSibling.cells.length === 1 &&
            (nextSibling.cells[0].colSpan || 1) > 1
        ) {
            const sibText = (nextSibling.innerText || nextSibling.textContent || '').trim();
            if (sibText && !sibText.includes('KỲ CÔNG NỢ ')) {
                const extraDiv = document.createElement('div');
                extraDiv.className = 'tnm-rd-extra';
                const extraClone = nextSibling.cells[0].cloneNode(true);
                extraClone.querySelectorAll('button').forEach(b => b.remove());
                extraDiv.innerHTML = `<div class="tnm-rd-extra-title">Thông tin mở rộng đi kèm</div>` + extraClone.innerHTML;
                bodyEl.appendChild(extraDiv);
            }
        }

        // Bind Copy All button
        const copyAllBtn = overlay.querySelector('#tnm-rd-copy-all');
        copyAllBtn.textContent = '📋 Sao chép toàn bộ';
        copyAllBtn.onclick = function () {
            const summaryText = fields
                .filter(f => f.plainText)
                .map(f => `${f.label}: ${f.plainText.replace(/\n+/g, ' | ')}`)
                .join('\n');
            copyTextToClipboard(summaryText).then(ok => {
                if (!ok) return;
                copyAllBtn.textContent = '✓ Đã sao chép dòng!';
                setTimeout(() => { copyAllBtn.textContent = '📋 Sao chép toàn bộ'; }, 1500);
            });
        };

        // Render row action buttons in Footer
        const actionsContainer = overlay.querySelector('#tnm-rd-actions');
        actionsContainer.innerHTML = '';

        const seenLabels = new Set();
        rowActionElements.forEach(origEl => {
            const onclickAttr = origEl.getAttribute('onclick') || '';
            if (onclickAttr.includes('tnmOpenRowDetail')) return;
            const text = (origEl.innerText || origEl.textContent || '').replace(/\s+/g, ' ').trim();
            if (!text || seenLabels.has(text)) return;
            seenLabels.add(text);

            const proxyBtn = document.createElement('button');
            proxyBtn.type = 'button';
            proxyBtn.dataset.confirm = 'none';
            proxyBtn.className = origEl.className && origEl.className.includes('btn') ? origEl.className : 'btn secondary btn-sm';
            if (origEl.getAttribute('style')) {
                proxyBtn.setAttribute('style', origEl.getAttribute('style'));
                proxyBtn.style.flex = '';
            }
            proxyBtn.textContent = text;
            proxyBtn.addEventListener('click', function (ev) {
                ev.stopPropagation();
                closeRowDetailModal();
                if (document.body.contains(origEl) && !origEl.disabled) {
                    origEl.click();
                }
            });
            actionsContainer.appendChild(proxyBtn);
        });

        const closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'btn secondary btn-sm';
        closeBtn.dataset.confirm = 'none';
        closeBtn.textContent = 'Đóng';
        closeBtn.addEventListener('click', closeRowDetailModal);
        actionsContainer.appendChild(closeBtn);

        overlay.hidden = false;
    }

    // Listen to double-click on any table row across the entire application
    document.addEventListener('dblclick', function (e) {
        if (e.defaultPrevented) return;
        const target = e.target;
        if (!target || typeof target.closest !== 'function') return;
        // Do not hijack double-clicks directly on buttons, links, inputs, selects, or labels
        if (target.closest('button, a, input, select, textarea, label')) return;

        const tr = target.closest('tbody > tr');
        if (!tr || !isDataTableRow(tr)) return;

        try {
            const sel = window.getSelection && window.getSelection();
            if (sel && sel.removeAllRanges) sel.removeAllRanges();
        } catch {
            // ignore selection cleanup errors
        }

        openRowDetailModal(tr);
    });

    window.tnmOpenRowDetail = function (el) {
        if (!el || typeof el.closest !== 'function') return;
        const tr = el.closest('tbody > tr') || el.closest('tr');
        if (tr) openRowDetailModal(tr);
    };

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
        closeRowDetailModal();

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
        closeRowDetailModal();
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
