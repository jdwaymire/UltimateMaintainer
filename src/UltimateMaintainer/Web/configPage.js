/*
 * configPage.js
 *
 * Controller for the plugin's settings page (configPage.html). Loads and saves PluginConfiguration and drives the
 * page: a status card per job, a tab per job (settings blocks + a live Preview grouped by user, Run now, Undo, the
 * last-run lines) and a History tab. The job panels work the same way; what differs is in their entry in `jobs`.
 *
 * NOTE: pluginId must match the GUID in Plugin.cs, and the config key names in `jobs` must match the
 * PluginConfiguration property names exactly. A misspelt key fails quietly: that setting just never loads or saves.
 * NOTE: everything shown from the server (titles, user names) goes in with textContent, never innerHTML.
 */
define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox'], function (BaseView, loading) {
    'use strict';

    var pluginId = '11b49b1a-0ead-49f0-9a27-f20f9733bcdc';

    function plural(n, word) {
        return n + ' ' + word + (n === 1 ? '' : 's');
    }

    // ---- the jobs ----
    //
    // key: the panel's data-job. config: PluginConfiguration keys (no Libraries = no Where block; Action/IncludeUndated
    // only for partial watch). api: REST route prefix. task: the scheduled task's Key. noun/nouns: what a preview row
    // is. verb: Preview's headline. recentText: what the "left alone" count means. libraryTypes: only offer libraries
    // of these Emby types ('' = mixed); leave out for every video library. row(r): a preview row as user, title,
    // detail, image id and watched %. undoText: none = no Undo button. shortText: doneText for the narrow cards.

    var jobs = [
        {
            key: 'pw',
            name: 'Partial watch cleanup',
            config: { Enabled: 'PartialWatchEnabled', Days: 'PartialWatchDays', Users: 'PartialWatchUsers', Libraries: 'PartialWatchLibraries', Action: 'PartialWatchAction', IncludeUndated: 'PartialWatchIncludeUndated' },
            api: 'UltimateMaintainer/PartialWatch',
            task: 'UltimateMaintainerPartialWatch',
            noun: 'item',
            verb: function (s) { return s.Action === 'unwatched' ? 'would have their resume point cleared' : 'would be marked watched'; },
            recentText: 'played recently',
            allUsersText: 'All users, including users added later.',
            allLibsText: 'All video libraries, including libraries added later.',
            doneText: function (r) { return r.Action === 'unwatched' ? 'resume points cleared' : 'marked watched'; },
            shortText: function (r) { return r.Action === 'unwatched' ? 'cleared' : 'marked watched'; },
            undoText: 'Puts back the resume points (and watched marks) the last change took away. Items played since then are left as they are.',
            icon: '<circle cx="12" cy="12" r="9"></circle><path d="M12 3a9 9 0 0 1 0 18z" fill="currentColor"></path>',
            row: function (r) {
                return {
                    user: r.User, title: r.Title, image: r.ItemId, pct: r.Percent,
                    detail: [r.Library, r.Percent == null ? null : r.Percent + '% watched',
                        r.DaysIdle < 0 ? 'no play date' : 'last played ' + plural(r.DaysIdle, 'day') + ' ago',
                        r.WasPlayed ? 'already watched before' : null].filter(Boolean).join(' · ')
                };
            }
        },
        {
            key: 'nu',
            name: 'Next episode cleanup',
            config: { Enabled: 'NextUpEnabled', Days: 'NextUpDays', Users: 'NextUpUsers', Libraries: 'NextUpLibraries' },
            api: 'UltimateMaintainer/NextUp',
            task: 'UltimateMaintainerNextUp',
            noun: 'next-episode entry',
            nouns: 'next-episode entries',
            verb: function () { return 'would be hidden from Continue Watching'; },
            recentText: 'played recently',
            allUsersText: 'All users, including users added later.',
            allLibsText: 'All TV libraries, including libraries added later.',
            libraryTypes: ['tvshows', 'mixed', ''],
            doneText: function () { return 'hidden from Continue Watching'; },
            shortText: function () { return 'hidden'; },
            undoText: 'Brings back every next-episode entry the last change hid.',
            icon: '<rect x="3" y="5" width="18" height="12" rx="2"></rect><path d="M10 9l4 2-4 2z"></path><path d="M8 21h8"></path>',
            row: function (r) {
                return {
                    user: r.UserName, title: r.Series, image: r.EpisodeId,
                    detail: 'Next: ' + r.Episode + ' · series last played ' + plural(r.DaysIdle, 'day') + ' ago'
                };
            }
        },
        {
            key: 'dv',
            name: 'Old devices cleanup',
            config: { Enabled: 'DevicesEnabled', Days: 'DevicesDays', Users: 'DevicesUsers' },
            api: 'UltimateMaintainer/Devices',
            task: 'UltimateMaintainerDevices',
            noun: 'device',
            verb: function () { return 'would be removed'; },
            recentText: 'seen recently or in use',
            allUsersText: 'All devices, including ones with no known user.',
            minDays: 7,
            doneText: function () { return 'removed'; },
            shortText: function () { return 'removed'; },
            icon: '<rect x="7" y="2" width="10" height="20" rx="2"></rect><path d="M11 18h2"></path>',
            row: function (r) {
                return { user: r.UserName || '(no user)', title: r.Name, detail: r.App + ' · last seen ' + plural(r.DaysIdle, 'day') + ' ago' };
            }
        }
    ];

    var jobNames = { 'partial-watch': 'Partial watch cleanup', 'next-up': 'Next episode cleanup', 'devices': 'Old devices cleanup' };
    var jobByHistory = { 'partial-watch': 0, 'next-up': 1, 'devices': 2 };

    function count(job, n) {
        return n + ' ' + (n === 1 ? job.noun : (job.nouns || job.noun + 's'));
    }

    // ---- small helpers ----

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) {
            e.className = cls;
        }
        if (text != null) {
            e.textContent = text;
        }
        return e;
    }

    // Buttons made here need Emby's is="emby-button" for its look (a customized built-in element).
    function embyButton(cls, text) {
        var b = document.createElement('button', { is: 'emby-button' });
        b.setAttribute('is', 'emby-button');
        b.type = 'button';
        b.className = cls;
        b.appendChild(el('span', null, text));
        return b;
    }

    // Icons are fixed markup from `jobs` (never server data), so innerHTML is fine here.
    function icon(paths) {
        var span = el('span', 'umIcon');
        span.innerHTML = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" ' +
            'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">' + paths + '</svg>';
        return span;
    }

    // Emby's ApiClient rejects with the fetch Response; the server's message is its body.
    function errorText(err) {
        if (err && typeof err.text === 'function') {
            return err.text().then(function (t) { return t || 'The request failed.'; }, function () { return 'The request failed.'; });
        }
        return Promise.resolve('The request failed.');
    }

    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function toast(text) {
        require(['toast'], function (t) { (t.default || t)(text); });
    }

    function panel(view, job) {
        return view.querySelector('.umPanel[data-job="' + job.key + '"]');
    }

    function q(view, job, cls) {
        return panel(view, job).querySelector('.' + cls);
    }

    // "2026-10-14 04:10" (the server's local time) -> "today 04:10" / "yesterday 04:10" / "Oct 12, 04:10".
    function when(text) {
        var m = /^(\d{4})-(\d{2})-(\d{2}) (\d{2}:\d{2})$/.exec(text || '');
        if (!m) {
            return text || '';
        }
        var d = new Date(+m[1], +m[2] - 1, +m[3]);
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        var days = Math.round((today - d) / 86400000);
        if (days === 0) {
            return 'today ' + m[4];
        }
        if (days === 1) {
            return 'yesterday ' + m[4];
        }
        return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + ', ' + m[4];
    }

    // ---- page state (per page view) ----
    //
    // st.users / st.libs: what the chips choose from. st.sel[job.key]: { users: 'all' | [ids], libs: ... }.
    // st.preview[key], st.lastRun[key], st.task[key]: the latest answers from the server, for the cards.

    function newState() {
        return { users: [], libs: [], sel: {}, preview: {}, lastRun: {}, task: {}, tab: 'pw', dirty: false, timers: {} };
    }

    function parseSel(saved) {
        var ids = (saved || 'all').split(',').map(function (x) { return x.trim(); }).filter(Boolean);
        return !ids.length || ids.some(function (x) { return x.toLowerCase() === 'all'; }) ? 'all' : ids;
    }

    // Comparing ids without dashes and case, like Core's Selection (user ids come in two spellings).
    function sameId(a, b) {
        return String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
    }

    function has(sel, id) {
        return sel === 'all' || sel.some(function (x) { return sameId(x, id); });
    }

    // ---- "all, or only these" chips ----
    //
    // The first chip is "All"; while it is on, the others show as included but dimmed. Clicking one of them then
    // switches to "only these" with everyone except that one, so one click never widens or empties the choice.

    function renderChips(view, st, job, kind) {
        var box = q(view, job, kind === 'users' ? 'umUsers' : 'umLibs');
        var note = q(view, job, kind === 'users' ? 'umUsersNote' : 'umLibsNote');
        var items = kind === 'users' ? st.users : st.libs.filter(function (l) {
            return !job.libraryTypes || job.libraryTypes.indexOf((l.type || '').toLowerCase()) >= 0;
        });
        var sel = st.sel[job.key][kind];
        box.textContent = '';

        var all = el('button', 'umChip umAll', 'All');
        all.type = 'button';
        all.setAttribute('aria-pressed', sel === 'all' ? 'true' : 'false');
        all.addEventListener('click', function () {
            st.sel[job.key][kind] = 'all';
            changed(view, st, job);
            renderChips(view, st, job, kind);
        });
        box.appendChild(all);

        items.forEach(function (x, i) {
            var on = has(sel, x.id);
            var chip = el('button', 'umChip' + (sel === 'all' ? ' umImplied' : ''));
            chip.type = 'button';
            chip.setAttribute('aria-pressed', sel !== 'all' && on ? 'true' : 'false');
            if (kind === 'users') {
                var av = el('span', 'umAvatar', x.name.charAt(0).toUpperCase());
                av.style.background = 'hsl(' + ((i * 67 + 200) % 360) + ', 45%, 45%)';
                if (x.image) {
                    var img = el('img');
                    img.alt = '';
                    img.src = ApiClient.getUserImageUrl(x.id, { type: 'Primary', tag: x.image, height: 48 });
                    av.textContent = '';
                    av.appendChild(img);
                }
                chip.appendChild(av);
            }
            chip.appendChild(document.createTextNode(x.name));
            chip.addEventListener('click', function () {
                var cur = st.sel[job.key][kind];
                var ids = cur === 'all' ? items.map(function (y) { return y.id; }) : cur.slice();
                var at = -1;
                ids.forEach(function (y, k) { if (sameId(y, x.id)) { at = k; } });
                if (at >= 0) {
                    // An empty list is saved as "" and the server reads that as all: never let it get empty.
                    if (ids.length === 1) {
                        toast('Keep at least one, or choose All.');
                        return;
                    }
                    ids.splice(at, 1);
                } else {
                    ids.push(x.id);
                }
                st.sel[job.key][kind] = ids;
                changed(view, st, job);
                renderChips(view, st, job, kind);
            });
            box.appendChild(chip);
        });

        if (sel === 'all') {
            note.textContent = kind === 'users' ? job.allUsersText : job.allLibsText;
        } else {
            var n = items.filter(function (x) { return has(sel, x.id); }).length;
            note.textContent = 'Only the ' + (n === 1 ? 'highlighted one' : n + ' highlighted') + '.';
        }
    }

    function selText(sel) {
        return sel === 'all' ? 'all' : sel.join(',');
    }

    // A panel's current values, the same shape for Save and for Preview.
    function readSettings(view, st, job) {
        var days = parseInt(q(view, job, 'umDays').value, 10);
        var s = {
            Days: Math.max(days > 0 ? days : 14, job.minDays || 1),
            Users: selText(st.sel[job.key].users)
        };
        if (job.config.Libraries) {
            s.Libraries = selText(st.sel[job.key].libs);
        }
        if (job.config.Action) {
            s.Action = q(view, job, 'umActionUnwatched').checked ? 'unwatched' : 'watched';
        }
        if (job.config.IncludeUndated) {
            s.IncludeUndated = q(view, job, 'umIncludeUndated').checked;
        }
        return s;
    }

    // Saves every job (Run now saves the whole page, like Save).
    function save(view, st) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            jobs.forEach(function (job) {
                var s = readSettings(view, st, job);
                config[job.config.Enabled] = q(view, job, 'umEnabled').checked;
                config[job.config.Days] = s.Days;
                config[job.config.Users] = s.Users;
                if (job.config.Libraries) {
                    config[job.config.Libraries] = s.Libraries;
                }
                if (job.config.Action) {
                    config[job.config.Action] = s.Action;
                }
                if (job.config.IncludeUndated) {
                    config[job.config.IncludeUndated] = s.IncludeUndated;
                }
            });
            return ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                setDirty(view, st, false);
                return result;
            });
        });
    }

    function setDirty(view, st, dirty) {
        st.dirty = dirty;
        view.querySelector('.umDirty').textContent = dirty ? 'You have changes that are not saved yet.' : '';
    }

    // Any settings change: mark the page unsaved, refresh the cards, and re-run that job's Preview after a short pause
    // (typing "30" must not preview "3" first).
    function changed(view, st, job) {
        setDirty(view, st, true);
        renderCards(view, st);
        clearTimeout(st.timers[job.key]);
        st.timers[job.key] = setTimeout(function () { preview(view, st, job, true); }, 700);
    }

    // ---- status cards and the health line ----

    function scheduleText(st, job) {
        var t = st.task[job.key];
        if (!t) {
            return '';
        }
        var daily = (t.Triggers || []).filter(function (x) { return (x.Type === 'DailyTrigger' || x.TriggerType === 'DailyTrigger') && x.TimeOfDayTicks != null; });
        if (!daily.length) {
            return (t.Triggers || []).length ? 'custom schedule' : 'no schedule set';
        }
        var mins = Math.round(daily[0].TimeOfDayTicks / 600000000);
        var hhmm = ('0' + Math.floor(mins / 60)).slice(-2) + ':' + ('0' + mins % 60).slice(-2);
        var now = new Date();
        return (now.getHours() * 60 + now.getMinutes() < mins ? 'today ' : 'tomorrow ') + hhmm;
    }

    function renderCards(view, st) {
        var box = view.querySelector('.umCards');
        box.textContent = '';
        var on = 0;
        jobs.forEach(function (job) {
            var enabled = q(view, job, 'umEnabled').checked;
            on += enabled ? 1 : 0;
            var card = el('button', 'umCard' + (st.tab === job.key ? ' umSelected' : ''));
            card.type = 'button';
            card.addEventListener('click', function () { selectTab(view, st, job.key); });

            var top = el('div', 'umCardTop');
            var name = el('span', 'umCardName');
            name.appendChild(icon(job.icon));
            name.appendChild(el('span', null, job.name));
            top.appendChild(name);
            top.appendChild(el('span', 'umPill' + (enabled ? '' : ' umOff'), enabled ? 'On' : 'Off'));
            card.appendChild(top);

            var p = st.preview[job.key];
            var big = el('div', 'umBig');
            big.appendChild(el('b', null, p ? String((p.Rows || []).length) : '…'));
            big.appendChild(el('span', 'umMuted', p ? ((p.Rows || []).length ? 'due now' : 'nothing due now') : 'checking'));
            card.appendChild(big);

            var foot = el('div', 'umCardFoot');
            var last = lastRunShort(job, st.lastRun[job.key]);
            footLine(foot, 'Last run', last.when);
            footLine(foot, 'Result', last.result);
            footLine(foot, 'Next run', enabled ? (scheduleText(st, job) || '…') : 'off');
            card.appendChild(foot);
            box.appendChild(card);
        });
        view.querySelector('.umHealthText').textContent = on + ' of ' + jobs.length + ' jobs on';
        view.querySelector('.umHealthDot').classList.toggle('umOff', on === 0);
    }

    // One short label/value line of a card; the value never wraps (it is cut with ... if a card gets very narrow).
    function footLine(foot, label, value) {
        var line = el('span', 'umFootLine');
        line.appendChild(el('span', null, label));
        var v = el('span', 'umFootValue', value);
        v.title = value;
        line.appendChild(v);
        foot.appendChild(line);
    }

    // The card's last run as { when, result }, the result as short as possible ("1 marked watched", "nothing to do").
    function lastRunShort(job, r) {
        if (!r) {
            return { when: '…', result: '…' };
        }
        if (r.LatestDate) {
            return { when: when(r.LatestDate), result: r.LatestChanged ? r.LatestChanged + ' ' + job.shortText({ Action: r.LatestAction }) : 'nothing to do' };
        }
        return r.Exists ? { when: when(r.Date), result: r.Changed + ' ' + job.shortText(r) } : { when: 'not yet', result: '–' };
    }

    // Two lines: the latest run (quiet nights too) and the last run that changed something (what Undo works on);
    // one line when they are the same run.
    function lastRunLines(job, r) {
        var changedLine = !r.Exists ? 'No run has changed anything yet.'
            : 'Last change: ' + when(r.Date) + ', ' + count(job, r.Changed) + ' ' + job.doneText(r) + (r.Undone ? ', undone.' : '.');
        if (!r.LatestDate) {
            return [changedLine];
        }
        var extra = [r.LatestRecent + ' ' + job.recentText];
        if (r.LatestSkipped) {
            extra.push(r.LatestSkipped + ' without a date');
        }
        if (r.LatestErrors) {
            extra.push(plural(r.LatestErrors, 'error'));
        }
        var latest = 'Last run: ' + when(r.LatestDate) + ', ' +
            (r.LatestChanged ? count(job, r.LatestChanged) + ' ' + job.doneText({ Action: r.LatestAction }) : 'nothing to do') +
            ' (' + extra.join(', ') + ')';
        if (r.LatestChanged && r.Exists && r.Date === r.LatestDate) {
            return [latest + (r.Undone ? ', undone.' : '.')];
        }
        return [latest + '.', changedLine];
    }

    function loadLastRun(view, st, job) {
        return ApiClient.getJSON(ApiClient.getUrl(job.api + '/LastRun')).then(function (r) {
            st.lastRun[job.key] = r;
            renderSide(view, st, job);
            renderCards(view, st);
        }, function () { });
    }

    // ---- tabs ----

    function selectTab(view, st, key) {
        st.tab = key;
        Array.prototype.forEach.call(view.querySelectorAll('.umTab'), function (t) {
            t.setAttribute('aria-selected', t.getAttribute('data-tab') === key ? 'true' : 'false');
        });
        Array.prototype.forEach.call(view.querySelectorAll('.umPanel'), function (p) {
            p.hidden = p.getAttribute('data-job') !== key;
        });
        if (key === 'history') {
            loadHistory(view);
        }
        renderCards(view, st);
    }

    // ---- the preview side of a job panel ----

    function renderSide(view, st, job) {
        var side = q(view, job, 'umPreview');
        var p = st.preview[job.key];
        var r = st.lastRun[job.key];
        side.textContent = '';

        var box = el('div', 'umBlock');
        var head = el('div', 'umSideHead');
        head.appendChild(el('span', 'umLabel', 'Preview'));
        var refresh = el('button', 'umIconBtn', 'Refresh');
        refresh.type = 'button';
        refresh.title = 'Check again (changes nothing)';
        refresh.addEventListener('click', function () { preview(view, st, job, false); });
        head.appendChild(refresh);
        box.appendChild(head);

        if (!p) {
            box.appendChild(el('span', 'umMuted', 'Checking what this job would change...'));
        } else {
            var rows = (p.Rows || []).map(job.row);
            var s = readSettings(view, st, job);
            var byUser = {};
            rows.forEach(function (x) { (byUser[x.user] = byUser[x.user] || []).push(x); });
            var users = Object.keys(byUser).sort(function (a, b) { return byUser[b].length - byUser[a].length; });

            var cnt = el('div', 'umCount');
            cnt.appendChild(el('b', null, String(rows.length)));
            cnt.appendChild(el('span', null, (rows.length === 1 ? job.noun : (job.nouns || job.noun + 's')) +
                (users.length > 1 ? ' for ' + users.length + ' users' : '') + ' ' + job.verb(s)));
            box.appendChild(cnt);
            box.appendChild(el('span', 'umMuted umSmall', 'Left alone: ' + p.Recent + ' ' + job.recentText + '. ' +
                (p.UsersChecked != null ? plural(p.UsersChecked, 'user') + ' checked.' : plural(p.Total, 'device') + ' in all.')));
            (p.Errors || []).forEach(function (e) { box.appendChild(el('span', 'umWarn umSmall', e)); });

            if (rows.length) {
                var groups = el('div', 'umGroups');
                users.forEach(function (u, i) {
                    var d = el('details', 'umGroup');
                    d.open = i === 0;
                    var sum = el('summary');
                    var who = el('span', 'umWho');
                    var av = el('span', 'umAvatar', u.charAt(0).toUpperCase());
                    av.style.background = 'hsl(' + ((i * 67 + 200) % 360) + ', 45%, 45%)';
                    who.appendChild(av);
                    who.appendChild(document.createTextNode(u));
                    sum.appendChild(who);
                    sum.appendChild(el('span', 'umMuted umSmall', count(job, byUser[u].length)));
                    d.appendChild(sum);
                    byUser[u].forEach(function (x) { d.appendChild(previewRow(job, x)); });
                    groups.appendChild(d);
                });
                box.appendChild(groups);
            } else {
                var tidy = el('div', 'umAllTidy');
                tidy.appendChild(el('b', null, 'All tidy'));
                tidy.appendChild(el('span', 'umMuted umSmall', 'Nothing to do with these settings.'));
                box.appendChild(tidy);
            }
            if ((p.Skipped || []).length) {
                var sk = el('details', 'umGroup');
                var ss = el('summary');
                ss.appendChild(el('span', 'umWho', 'Skipped'));
                ss.appendChild(el('span', 'umMuted umSmall', String(p.Skipped.length)));
                sk.appendChild(ss);
                p.Skipped.forEach(function (x) {
                    var row = el('div', 'umRow umSmall');
                    row.appendChild(el('span', null, x.UserName + ': ' + x.Title + ' (' + x.Reason + ')'));
                    sk.appendChild(row);
                });
                box.appendChild(sk);
            }
        }

        var buttons = el('div', 'umButtons');
        var run = embyButton('raised button-submit', 'Run now');
        run.disabled = !p || !(p.Rows || []).length;
        run.addEventListener('click', function () { runNow(view, st, job); });
        buttons.appendChild(run);
        if (job.undoText) {
            var undoBtn = embyButton('raised', 'Undo last change');
            undoBtn.disabled = !r || !r.Exists || r.Undone;
            undoBtn.addEventListener('click', function () { undo(view, st, job); });
            buttons.appendChild(undoBtn);
        }
        box.appendChild(buttons);

        if (r) {
            var lines = el('div', 'umMuted umSmall umLastRun');
            lastRunLines(job, r).forEach(function (line) { lines.appendChild(el('div', null, line)); });
            box.appendChild(lines);
        }
        side.appendChild(box);
    }

    function previewRow(job, x) {
        var row = el('div', 'umRow');
        if (x.image) {
            var img = el('img', 'umThumb');
            img.alt = '';
            img.loading = 'lazy';
            img.src = ApiClient.getImageUrl(x.image, { type: 'Primary', maxHeight: 96 });
            img.addEventListener('error', function () { img.style.visibility = 'hidden'; });
            row.appendChild(img);
        } else {
            var ph = icon(job.icon);
            ph.className = 'umThumb';
            row.appendChild(ph);
        }
        var text = el('div', 'umRowText');
        text.appendChild(el('span', 'umRowTitle', x.title));
        text.appendChild(el('span', 'umMuted umSmall', x.detail));
        if (x.pct != null) {
            var bar = el('span', 'umBar');
            var fill = el('span');
            fill.style.width = Math.max(0, Math.min(100, x.pct)) + '%';
            bar.appendChild(fill);
            text.appendChild(bar);
        }
        row.appendChild(text);
        return row;
    }

    // quiet: a background refresh (page open, settings change), so no spinner over the page.
    function preview(view, st, job, quiet) {
        if (!quiet) {
            loading.show();
        }
        return postJson(job.api + '/Preview', readSettings(view, st, job)).then(function (p) {
            if (!quiet) {
                loading.hide();
            }
            st.preview[job.key] = p;
            renderSide(view, st, job);
            renderCards(view, st);
            return p;
        }, function (err) {
            if (!quiet) {
                loading.hide();
            }
            errorText(err).then(toast);
            throw err;
        });
    }

    // ---- run now, undo ----

    // A small yes/no box on Emby's dialogHelper.
    function ask(title, text, yes) {
        return new Promise(function (resolve) {
            require(['dialogHelper'], function (dialogHelper) {
                dialogHelper = dialogHelper.default || dialogHelper;
                var dlg = dialogHelper.createDialog({ size: 'small', removeOnClose: true, scrollY: false });
                dlg.classList.add('formDialog');
                dlg.innerHTML =
                    '<div class="formDialogHeader">' +
                    '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
                    '  <h3 class="formDialogHeaderTitle"></h3>' +
                    '</div>' +
                    '<div class="formDialogContent" style="padding:1em 1.5em"><p class="umAskText"></p></div>' +
                    '<div class="formDialogFooter" style="display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em">' +
                    '  <button is="emby-button" type="button" class="raised button-cancel umNo"><span>Cancel</span></button>' +
                    '  <button is="emby-button" type="button" class="raised button-submit umYes"><span></span></button>' +
                    '</div>';
                dlg.querySelector('.formDialogHeaderTitle').textContent = title;
                dlg.querySelector('.umAskText').textContent = text;
                dlg.querySelector('.umYes span').textContent = yes;
                var answer = false;
                dlg.querySelector('.btnCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
                dlg.querySelector('.umNo').addEventListener('click', function () { dialogHelper.close(dlg); });
                dlg.querySelector('.umYes').addEventListener('click', function () { answer = true; dialogHelper.close(dlg); });
                dlg.addEventListener('close', function () { resolve(answer); });
                dialogHelper.open(dlg);
            });
        });
    }

    // Preview again first, so the box says how many with the settings as they are now; nothing happens on Cancel.
    function runNow(view, st, job) {
        preview(view, st, job, false).then(function (p) {
            var n = (p.Rows || []).length;
            if (!n) {
                toast('Nothing to clean up with these settings.');
                return;
            }
            return ask('Run ' + job.name.toLowerCase() + ' now?', 'This saves the settings and changes ' + count(job, n) +
                ' (listed in the preview). ' + (job.undoText ? 'Undo can put them back.' : 'This cannot be undone.'), 'Run now').then(function (yes) {
                if (!yes) {
                    return;
                }
                loading.show();
                return save(view, st).then(function () {
                    return postJson(job.api + '/Run');
                }).then(function (r) {
                    loading.hide();
                    // The latest run is this one; Changed/Date may be an older run's when this one changed nothing.
                    toast('Done: ' + count(job, r.LatestDate ? r.LatestChanged : 0) + ' ' + job.doneText({ Action: r.LatestAction }) + '.');
                    st.lastRun[job.key] = r;
                    preview(view, st, job, true).catch(function () { });
                }, function (err) {
                    loading.hide();
                    errorText(err).then(toast);
                });
            });
        }, function () { /* preview already said why */ });
    }

    function undo(view, st, job) {
        ask('Undo the last change?', job.undoText, 'Undo').then(function (yes) {
            if (!yes) {
                return;
            }
            loading.show();
            postJson(job.api + '/Undo').then(function (r) {
                loading.hide();
                toast('Undo done: ' + r.Restored + ' put back' + (r.Skipped ? ', ' + r.Skipped + ' left alone' : '') + '.');
                loadLastRun(view, st, job);
                preview(view, st, job, true).catch(function () { });
            }, function (err) {
                loading.hide();
                errorText(err).then(toast);
            });
        });
    }

    // ---- history ----

    function loadHistory(view) {
        var box = view.querySelector('.umHistory');
        box.textContent = '';
        box.appendChild(el('span', 'umMuted', 'Loading...'));
        ApiClient.getJSON(ApiClient.getUrl('UltimateMaintainer/History')).then(function (list) {
            box.textContent = '';
            if (!list.length) {
                box.appendChild(el('span', 'umMuted', 'No runs recorded yet. The history starts with this version; the first entries appear after tonight\'s runs.'));
                return;
            }
            list.forEach(function (h) {
                var job = jobs[jobByHistory[h.Job]];
                var row = el('div', 'umHistRow');
                row.appendChild(el('span', 'umHistWhen', when(h.Date)));
                row.appendChild(el('span', 'umHistJob', jobNames[h.Job] || h.Job));
                var what, tag, changedSomething = h.Changed > 0;
                if (h.Kind === 'undo') {
                    what = 'Undo: ' + h.Changed + ' put back' + (h.Skipped ? ', ' + h.Skipped + ' left alone' : '');
                    tag = 'undo';
                } else {
                    what = (h.Changed && job ? count(job, h.Changed) + ' ' + job.doneText({ Action: h.Action }) : 'Nothing to do') +
                        (job ? ' · ' + h.Recent + ' ' + job.recentText : '') +
                        (h.Skipped ? ' · ' + h.Skipped + ' without a date' : '') + (h.Errors ? ' · ' + plural(h.Errors, 'error') : '');
                    tag = h.Kind === 'manual' ? 'Run now' : (changedSomething ? 'changed' : 'quiet');
                }
                row.appendChild(el('span', 'umHistWhat', what));
                row.appendChild(el('span', 'umTag' + (changedSomething ? ' umChanged' : ''), tag));
                box.appendChild(row);
            });
        }, function () {
            box.textContent = '';
            box.appendChild(el('span', 'umWarn', 'Could not load the history.'));
        });
    }

    // ---- the page ----

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;

        Array.prototype.forEach.call(view.querySelectorAll('.umTab'), function (t) {
            t.addEventListener('click', function () { selectTab(view, self.st, t.getAttribute('data-tab')); });
        });

        jobs.forEach(function (job) {
            var p = panel(view, job);
            p.addEventListener('input', function (e) {
                if (e.target.classList.contains('umDays')) {
                    changed(view, self.st, job);
                }
            });
            p.addEventListener('change', function (e) {
                if (e.target.matches('input[type=checkbox], input[type=radio]')) {
                    changed(view, self.st, job);
                }
            });
        });

        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            save(view, self.st).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            }, function () {
                loading.hide();
                toast('Saving failed.');
            });
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    // Runs every time the page is shown: settings, users, libraries, task schedules, last runs, then a quiet Preview
    // of every job (they only read) so the cards can show what is due.
    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        var st = this.st = newState();
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            jobs.forEach(function (job) {
                q(view, job, 'umEnabled').checked = !!config[job.config.Enabled];
                q(view, job, 'umDays').value = config[job.config.Days] || 14;
                st.sel[job.key] = { users: parseSel(config[job.config.Users]), libs: job.config.Libraries ? parseSel(config[job.config.Libraries]) : 'all' };
                if (job.config.Action) {
                    var unwatched = config[job.config.Action] === 'unwatched';
                    q(view, job, 'umActionUnwatched').checked = unwatched;
                    q(view, job, 'umActionWatched').checked = !unwatched;
                }
                if (job.config.IncludeUndated) {
                    q(view, job, 'umIncludeUndated').checked = !!config[job.config.IncludeUndated];
                }
                renderSide(view, st, job);
            });
            selectTab(view, st, 'pw');
            setDirty(view, st, false);
            loading.hide();

            var users = ApiClient.getUsers().then(function (list) {
                st.users = list.map(function (u) { return { id: u.Id, name: u.Name, image: u.PrimaryImageTag }; });
                jobs.forEach(function (job) { renderChips(view, st, job, 'users'); });
            }, function () {
                jobs.forEach(function (job) { q(view, job, 'umUsers').textContent = 'Could not load the users.'; });
            });
            var libs = ApiClient.getJSON(ApiClient.getUrl('UltimateMaintainer/Libraries')).then(function (list) {
                st.libs = list.map(function (l) { return { id: l.Id, name: l.Name, type: l.CollectionType }; });
                jobs.filter(function (job) { return job.config.Libraries; }).forEach(function (job) { renderChips(view, st, job, 'libs'); });
            }, function () {
                jobs.filter(function (job) { return job.config.Libraries; }).forEach(function (job) {
                    q(view, job, 'umLibs').textContent = 'Could not load the libraries.';
                });
            });
            ApiClient.getScheduledTasks().then(function (tasks) {
                jobs.forEach(function (job) {
                    var t = tasks.filter(function (x) { return x.Key === job.task; })[0];
                    st.task[job.key] = t;
                    var span = q(view, job, 'umSchedule');
                    span.textContent = '';
                    if (t) {
                        span.appendChild(document.createTextNode('Next run: ' + (scheduleText(st, job) || 'not scheduled') + ' ('));
                        var a = el('a', 'umLink', 'change the time');
                        a.href = '#!/scheduledtask?id=' + encodeURIComponent(t.Id);
                        span.appendChild(a);
                        span.appendChild(document.createTextNode(').'));
                    }
                });
                renderCards(view, st);
            }, function () { });
            jobs.forEach(function (job) { loadLastRun(view, st, job); });
            // The previews need the users and libraries only for display, but run after them so the first Preview
            // does not compete with the page's own loading.
            Promise.all([users, libs]).then(function () {
                jobs.forEach(function (job) { preview(view, st, job, true).catch(function () { }); });
            });
        }, function () {
            loading.hide();
            toast('Could not load the settings.');
        });
    };

    return View;
});
