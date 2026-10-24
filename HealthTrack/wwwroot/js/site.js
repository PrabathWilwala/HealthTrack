// HealthTrack client-side behaviour.
// - Renders Chart.js charts declared with <canvas data-chart="...json...">
// - Confirms destructive actions (forms with data-confirm)
// - Signs users out automatically after inactivity
// - Adds/removes rows in the plan editors
// - Keeps the unread notification count up to date
(function () {
    'use strict';

    // ------------------------------------------------------------------ charts
    function renderCharts() {
        if (typeof Chart === 'undefined') {
            document.querySelectorAll('canvas[data-chart]').forEach(function (c) {
                var note = document.createElement('p');
                note.textContent = 'Charts need an internet connection (Chart.js CDN).';
                c.replaceWith(note);
            });
            return;
        }

        document.querySelectorAll('canvas[data-chart]').forEach(function (canvas) {
            var data;
            try { data = JSON.parse(canvas.getAttribute('data-chart')); } catch (e) { return; }
            var type = canvas.getAttribute('data-type') || 'line';
            var horizontal = canvas.hasAttribute('data-horizontal');
            var stacked = canvas.hasAttribute('data-stacked');
            var yLabel = canvas.getAttribute('data-y-label');
            var suggestedMax = canvas.getAttribute('data-max');
            var isPie = type === 'doughnut' || type === 'pie';

            (data.datasets || []).forEach(function (ds) {
                if (ds.type == null) delete ds.type;
            });

            new Chart(canvas, {
                type: type,
                data: data,
                options: {
                    responsive: false,
                    animation: false,
                    indexAxis: horizontal ? 'y' : 'x',
                    scales: isPie ? {} : {
                        x: { stacked: stacked },
                        y: {
                            stacked: stacked, beginAtZero: type === 'bar',
                            suggestedMax: suggestedMax ? Number(suggestedMax) : undefined,
                            title: { display: !!yLabel, text: yLabel || '' }
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

    // ------------------------------------------------------------------ inactivity sign-out
    function wireIdleTimeout() {
        var minutes = parseInt(document.body.getAttribute('data-idle-minutes') || '0', 10);
        var form = document.getElementById('idleLogoutForm');
        var warning = document.getElementById('idleWarning');
        var countdown = document.getElementById('idleCountdown');
        if (!minutes || !form) return;

        var limit = minutes * 60 * 1000;
        var warnAt = limit - 60 * 1000;
        var last = Date.now();

        function activity() {
            last = Date.now();
            if (warning) warning.hidden = true;
        }
        ['mousemove', 'keydown', 'click', 'scroll', 'touchstart'].forEach(function (evt) {
            window.addEventListener(evt, activity, { passive: true });
        });

        setInterval(function () {
            var idle = Date.now() - last;
            if (idle >= limit) {
                form.submit();
            } else if (idle >= warnAt) {
                if (warning) warning.hidden = false;
                if (countdown) countdown.textContent = Math.ceil((limit - idle) / 1000);
            }
        }, 1000);
    }

    // ------------------------------------------------------------------ dynamic rows (plan editors)
    // A container [data-rows] holds [data-row] children and a <template data-row-template>.
    // Inputs use the placeholder __i__ which is replaced by the row index.
    function reindex(container) {
        var prefix = container.getAttribute('data-rows');
        container.querySelectorAll('[data-row]').forEach(function (row, index) {
            row.querySelectorAll('[name]').forEach(function (input) {
                input.name = input.name.replace(new RegExp(prefix + '\\[(\\d+|__i__)\\]'), prefix + '[' + index + ']');
                if (input.id) input.id = input.id.replace(/_(\d+|__i__)__/, '_' + index + '__');
            });
            row.querySelectorAll('[data-valmsg-for]').forEach(function (span) {
                span.setAttribute('data-valmsg-for', span.getAttribute('data-valmsg-for').replace(/\[(\d+|__i__)\]/, '[' + index + ']'));
            });
            var label = row.querySelector('[data-row-number]');
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
                var btn = e.target.closest('[data-remove-row]');
                if (!btn) return;
                var rows = container.querySelectorAll('[data-row]');
                if (rows.length <= 1) { alert('At least one row is required.'); return; }
                btn.closest('[data-row]').remove();
                reindex(container);
            });
            container.addEventListener('input', function () { updateTotals(container); });
            updateTotals(container);
        });
    }

    // ------------------------------------------------------------------ unread notification count polling
    function wireNotificationPolling() {
        var counter = document.querySelector('[data-unread-count]');
        if (!counter) return;
        setInterval(function () {
            fetch('/Notifications/UnreadCount', { headers: { 'Accept': 'application/json' }, credentials: 'same-origin' })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (!data) return;
                    counter.textContent = data.count > 99 ? '99+' : data.count;
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
                    tr.hidden = tr.textContent.toLowerCase().indexOf(term) < 0;
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
        wireIdleTimeout();
        wireRowEditors();
        wireNotificationPolling();
        wireMisc();
    });
})();
