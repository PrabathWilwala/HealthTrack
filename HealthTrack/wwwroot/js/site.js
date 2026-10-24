// HealthTrack client-side behaviour.
// - Renders Chart.js charts declared with <canvas data-chart="...json...">
// - Confirms destructive actions (forms with data-confirm)
// - Signs users out automatically after inactivity
// - Adds/removes rows in the plan editors
// - Keeps the notification badge up to date
(function () {
    'use strict';

    var palette = ['#2563eb', '#14b8a6', '#f59e0b', '#ef4444', '#8b5cf6', '#0ea5e9', '#10b981', '#64748b'];

    // ------------------------------------------------------------------ charts
    function renderCharts() {
        if (typeof Chart === 'undefined') {
            document.querySelectorAll('canvas[data-chart]').forEach(function (c) {
                var note = document.createElement('p');
                note.className = 'text-muted small mb-0';
                note.textContent = 'Charts need an internet connection (Chart.js CDN).';
                c.replaceWith(note);
            });
            return;
        }

        Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;
        Chart.defaults.color = '#667085';
        Chart.defaults.plugins.legend.labels.boxWidth = 10;
        Chart.defaults.plugins.legend.labels.usePointStyle = true;

        document.querySelectorAll('canvas[data-chart]').forEach(function (canvas) {
            var data;
            try { data = JSON.parse(canvas.getAttribute('data-chart')); } catch (e) { return; }
            var type = canvas.getAttribute('data-type') || 'line';
            var horizontal = canvas.hasAttribute('data-horizontal');
            var stacked = canvas.hasAttribute('data-stacked');
            var yLabel = canvas.getAttribute('data-y-label');
            var suggestedMax = canvas.getAttribute('data-max');

            var isPie = type === 'doughnut' || type === 'pie';
            (data.datasets || []).forEach(function (ds, i) {
                var color = palette[i % palette.length];
                if (isPie) {
                    ds.backgroundColor = palette.slice(0, (ds.data || []).length);
                    ds.borderColor = '#fff';
                    ds.borderWidth = 2;
                    if (ds.type == null) delete ds.type;
                    return;
                }
                var dsType = ds.type || type;
                ds.borderColor = color;
                ds.backgroundColor = dsType === 'line' ? color + '22' : color + 'cc';
                ds.borderWidth = dsType === 'line' ? 2.5 : 0;
                ds.borderRadius = dsType === 'bar' ? 6 : 0;
                ds.pointRadius = dsType === 'line' ? 2.5 : 0;
                ds.pointHoverRadius = 5;
                ds.tension = 0.35;
                ds.spanGaps = true;
                ds.fill = dsType === 'line' && i === 0 && (data.datasets.length === 1);
                if (ds.label && /target/i.test(ds.label)) {
                    ds.borderDash = [6, 5];
                    ds.pointRadius = 0;
                    ds.fill = false;
                    ds.borderColor = '#94a3b8';
                    ds.backgroundColor = '#94a3b8';
                    ds.borderWidth = 2;
                }
                if (ds.type == null) delete ds.type;
            });

            new Chart(canvas, {
                type: type,
                data: data,
                options: {
                    responsive: true,
                    maintainAspectRatio: false,
                    indexAxis: horizontal ? 'y' : 'x',
                    interaction: { mode: 'index', intersect: false },
                    plugins: { legend: { display: isPie || (data.datasets || []).length > 1, position: 'bottom' } },
                    scales: isPie ? {} : {
                        x: { stacked: stacked, grid: { display: false }, ticks: { maxRotation: 0, autoSkip: true, maxTicksLimit: 10 } },
                        y: {
                            stacked: stacked, beginAtZero: type === 'bar', suggestedMax: suggestedMax ? Number(suggestedMax) : undefined,
                            grid: { color: '#eef1f6' }, title: { display: !!yLabel, text: yLabel || '' }
                        }
                    }
                }
            });
        });
    }

    // ------------------------------------------------------------------ confirmations
    function wireConfirmations() {
        document.addEventListener('submit', function (e) {
            var form = e.target;
            var message = form.getAttribute('data-confirm');
            if (message && !window.confirm(message)) {
                e.preventDefault();
            }
        });
    }

    // ------------------------------------------------------------------ sidebar
    function wireSidebar() {
        document.querySelectorAll('[data-sidebar-toggle]').forEach(function (b) {
            b.addEventListener('click', function () { document.body.classList.toggle('sidebar-open'); });
        });
        document.querySelectorAll('[data-sidebar-close]').forEach(function (b) {
            b.addEventListener('click', function () { document.body.classList.remove('sidebar-open'); });
        });
    }

    // ------------------------------------------------------------------ inactivity sign-out
    function wireIdleTimeout() {
        var minutes = parseInt(document.body.getAttribute('data-idle-minutes') || '0', 10);
        var form = document.getElementById('idleLogoutForm');
        var modalEl = document.getElementById('idleModal');
        if (!minutes || !form) return;

        var limit = minutes * 60 * 1000;
        var warnAt = limit - 60 * 1000;
        var last = Date.now();
        var warned = false;
        var modal = modalEl && window.bootstrap ? new bootstrap.Modal(modalEl) : null;
        var countdown = document.getElementById('idleCountdown');

        function activity() {
            last = Date.now();
            if (warned && modal) { modal.hide(); }
            warned = false;
        }
        ['mousemove', 'keydown', 'click', 'scroll', 'touchstart'].forEach(function (evt) {
            window.addEventListener(evt, activity, { passive: true });
        });

        setInterval(function () {
            var idle = Date.now() - last;
            if (idle >= limit) {
                form.submit();
            } else if (idle >= warnAt) {
                if (!warned && modal) { modal.show(); }
                warned = true;
                if (countdown) countdown.textContent = Math.ceil((limit - idle) / 1000);
            }
        }, 1000);
    }

    // ------------------------------------------------------------------ dynamic rows (plan editors)
    // A container [data-rows] holds .row-editor children and a <template data-row-template>.
    // Inputs use the placeholder __i__ which is replaced by the row index.
    function reindex(container) {
        var prefix = container.getAttribute('data-rows');
        container.querySelectorAll('.row-editor').forEach(function (row, index) {
            row.querySelectorAll('[name]').forEach(function (input) {
                input.name = input.name.replace(new RegExp(prefix + '\\[(\\d+|__i__)\\]'), prefix + '[' + index + ']');
                if (input.id) input.id = input.id.replace(/_(\d+|__i__)__/, '_' + index + '__');
            });
            row.querySelectorAll('[data-valmsg-for]').forEach(function (span) {
                span.setAttribute('data-valmsg-for', span.getAttribute('data-valmsg-for').replace(/\[(\d+|__i__)\]/, '[' + index + ']'));
            });
            var label = row.querySelector('.row-number');
            if (label) label.textContent = index + 1;
        });
        updateTotals(container);
    }

    function updateTotals(container) {
        var target = container.getAttribute('data-total-target');
        if (!target) return;
        var sum = 0;
        container.querySelectorAll('[data-sum]').forEach(function (input) { sum += Number(input.value) || 0; });
        var el = document.querySelector(target);
        if (el) el.textContent = sum;
    }

    function wireRowEditors() {
        document.querySelectorAll('[data-rows]').forEach(function (container) {
            var template = container.querySelector('template[data-row-template]');
            var addButton = document.querySelector('[data-add-row="' + container.id + '"]');
            if (addButton && template) {
                addButton.addEventListener('click', function () {
                    var fragment = template.content.cloneNode(true);
                    container.insertBefore(fragment, template);
                    reindex(container);
                });
            }
            container.addEventListener('click', function (e) {
                var btn = e.target.closest('.remove-row');
                if (!btn) return;
                var rows = container.querySelectorAll('.row-editor');
                if (rows.length <= 1) { alert('At least one row is required.'); return; }
                btn.closest('.row-editor').remove();
                reindex(container);
            });
            container.addEventListener('input', function () { updateTotals(container); });
            updateTotals(container);
        });
    }

    // ------------------------------------------------------------------ notification badge polling
    function wireNotificationPolling() {
        var badge = document.querySelector('[data-unread-badge]');
        if (!badge) return;
        setInterval(function () {
            fetch('/Notifications/UnreadCount', { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (!data) return;
                    badge.textContent = data.count > 99 ? '99+' : data.count;
                    badge.hidden = data.count === 0;
                })
                .catch(function () { /* offline: ignore */ });
        }, 60000);
    }

    // ------------------------------------------------------------------ misc helpers
    function wireMisc() {
        // Client-side filter for long tables: <input data-table-filter="#tableId">
        document.querySelectorAll('[data-table-filter]').forEach(function (input) {
            var table = document.querySelector(input.getAttribute('data-table-filter'));
            if (!table) return;
            input.addEventListener('input', function () {
                var term = input.value.toLowerCase();
                table.querySelectorAll('tbody tr').forEach(function (tr) {
                    tr.style.display = tr.textContent.toLowerCase().indexOf(term) >= 0 ? '' : 'none';
                });
            });
        });

        // Show/hide fields based on a select: <select data-toggle-sections> + [data-section="Value"]
        document.querySelectorAll('[data-toggle-sections]').forEach(function (select) {
            function update() {
                document.querySelectorAll('[data-section]').forEach(function (s) {
                    s.hidden = s.getAttribute('data-section') !== select.value;
                });
            }
            select.addEventListener('change', update);
            update();
        });

        // Live calorie estimate on the workout form.
        var estimate = document.getElementById('calorieEstimate');
        if (estimate) {
            var weight = Number(estimate.getAttribute('data-weight')) || 70;
            var duration = document.getElementById('DurationMinutes');
            var intensity = document.getElementById('Intensity');
            var mets = { '1': 3.5, '2': 6, '3': 9, 'Low': 3.5, 'Moderate': 6, 'High': 9 };
            function calc() {
                var met = mets[intensity.value] || 6;
                estimate.textContent = Math.round(met * weight * (Number(duration.value) || 0) / 60);
            }
            if (duration && intensity) {
                duration.addEventListener('input', calc);
                intensity.addEventListener('change', calc);
                calc();
            }
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        renderCharts();
        wireConfirmations();
        wireSidebar();
        wireIdleTimeout();
        wireRowEditors();
        wireNotificationPolling();
        wireMisc();
    });
})();
