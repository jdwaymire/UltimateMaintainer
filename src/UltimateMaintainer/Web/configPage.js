/*
 * configPage.js
 *
 * Controller for the plugin's settings page (configPage.html). Loads and saves PluginConfiguration and drives one
 * section per maintenance job (`jobs` below): user and library checklists, Preview (a table of what would change),
 * Run now (confirm box, saves first), Undo and the last-run line. The sections work the same way; what differs is in
 * their entry in `jobs`.
 *
 * NOTE: pluginId must match the GUID in Plugin.cs, and the config key names in `jobs` must match the
 * PluginConfiguration property names exactly. A misspelt key fails quietly: that setting just never loads or saves.
 */
define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-radio'], function (BaseView, loading) {
    'use strict';

    var pluginId = '11b49b1a-0ead-49f0-9a27-f20f9733bcdc';

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
    }

    function plural(n, word) {
        return n + ' ' + word + (n === 1 ? '' : 's');
    }

    // ---- the jobs ----
    //
    // prefix: start of the section's element ids. config: PluginConfiguration keys. api: REST route prefix.
    // columns: preview table [heading, cell(row), right-aligned?]. noun: what a preview row is called.
    // libraryTypes: only offer libraries of these Emby types ('mixed' or '' = mixed content); leave out for every video library.
    // checks: extra checkboxes [element id suffix, config key, preview field]. recentText: what Preview's "left alone"
    // count means. A job without config.Libraries has no library picker; one without undoText has no Undo button.

    var jobs = [
        {
            prefix: 'umPw',
            config: { Enabled: 'PartialWatchEnabled', Days: 'PartialWatchDays', Users: 'PartialWatchUsers', Libraries: 'PartialWatchLibraries', Action: 'PartialWatchAction' },
            checks: [['IncludeUndated', 'PartialWatchIncludeUndated', 'IncludeUndated']],
            api: 'UltimateMaintainer/PartialWatch',
            noun: 'item',
            verb: 'would change',
            doneText: function (r) { return r.Action === 'unwatched' ? 'resume points cleared' : 'marked watched'; },
            undoText: 'Puts back the resume points (and watched marks) the last cleanup changed. Items played since then are left as they are.',
            columns: [
                ['User', function (r) { return r.User; }],
                ['Title', function (r) { return r.Title; }],
                ['Library', function (r) { return r.Library; }],
                ['Watched', function (r) { return r.Percent == null ? '?' : r.Percent + '%'; }, true],
                ['Last played', function (r) { return r.DaysIdle < 0 ? 'no date' : r.DaysIdle + ' days ago'; }, true],
                ['Result', function (r) { return r.Result + (r.WasPlayed ? ' (already watched before)' : ''); }]
            ],
            userOf: function (r) { return r.User; }
        },
        {
            prefix: 'umNu',
            config: { Enabled: 'NextUpEnabled', Days: 'NextUpDays', Users: 'NextUpUsers', Libraries: 'NextUpLibraries' },
            api: 'UltimateMaintainer/NextUp',
            noun: 'next-episode entry',
            libraryTypes: ['tvshows', 'mixed', ''],
            nouns: 'next-episode entries',
            verb: 'would be hidden',
            doneText: function () { return 'hidden from Continue Watching'; },
            undoText: 'Brings back every next-episode entry the last cleanup hid.',
            columns: [
                ['User', function (r) { return r.UserName; }],
                ['Series', function (r) { return r.Series; }],
                ['Next episode', function (r) { return r.Episode; }],
                ['Library', function (r) { return r.Library; }],
                ['Series last played', function (r) { return r.DaysIdle + ' days ago'; }, true]
            ],
            userOf: function (r) { return r.UserName; }
        },
        {
            prefix: 'umDv',
            config: { Enabled: 'DevicesEnabled', Days: 'DevicesDays', Users: 'DevicesUsers' },
            api: 'UltimateMaintainer/Devices',
            noun: 'device',
            verb: 'would be removed',
            recentText: 'seen recently or in use are kept',
            minDays: 7,
            doneText: function () { return 'removed'; },
            columns: [
                ['Device', function (r) { return r.Name; }],
                ['App', function (r) { return r.App; }],
                ['Last user', function (r) { return r.UserName || '(none)'; }],
                ['Last seen', function (r) { return r.DaysIdle + ' days ago'; }, true]
            ],
            userOf: function (r) { return r.UserName || '(no user)'; }
        }
    ];

    function count(job, n) {
        return n + ' ' + (n === 1 ? job.noun : (job.nouns || job.noun + 's'));
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

    function q(view, job, rest) {
        return view.querySelector(rest.charAt(0) === '.' ? '.' + job.prefix + rest.slice(1) : '#' + job.prefix + rest);
    }

    // ---- "all, or only these" pickers (users and libraries) ----
    //
    // The saved value is "all" or comma-separated ids. The checklist only shows with "Only these" picked.

    function showPickers(view, job) {
        q(view, job, '.UserPick').style.display = q(view, job, 'UsersChosen').checked ? '' : 'none';
        if (job.config.Libraries) {
            q(view, job, '.LibPick').style.display = q(view, job, 'LibsChosen').checked ? '' : 'none';
        }
    }

    function fillPicker(view, job, kind, saved, items) {
        var list = q(view, job, '.' + kind + 'List');
        var ids = (saved || 'all').split(',').map(function (x) { return x.trim(); }).filter(function (x) { return x; });
        var all = !ids.length || ids.some(function (x) { return x.toLowerCase() === 'all'; });
        q(view, job, kind + 'sAll').checked = all;
        q(view, job, kind + 'sChosen').checked = !all;
        list.innerHTML = '';
        items.forEach(function (x) {
            var label = document.createElement('label');
            label.style.display = 'block';
            label.innerHTML = '<input is="emby-checkbox" type="checkbox" class="' + job.prefix + kind + 'Box"><span></span>';
            label.querySelector('input').value = x.id;
            label.querySelector('input').checked = !all && ids.indexOf(x.id) >= 0;
            label.querySelector('span').textContent = x.name;
            list.appendChild(label);
        });
        showPickers(view, job);
    }

    function readPicker(view, job, kind) {
        if (q(view, job, kind + 'sAll').checked) {
            return 'all';
        }
        return Array.prototype.filter.call(view.querySelectorAll('.' + job.prefix + kind + 'Box'), function (b) { return b.checked; })
            .map(function (b) { return b.value; }).join(',');
    }

    // Users and libraries are read once per page view and fill every section.
    function loadPickers(view, config) {
        ApiClient.getUsers().then(function (list) {
            var users = list.map(function (u) { return { id: u.Id, name: u.Name }; });
            jobs.forEach(function (job) { fillPicker(view, job, 'User', config[job.config.Users], users); });
        }, function () {
            jobs.forEach(function (job) { q(view, job, '.UserList').textContent = 'Could not load the users.'; });
        });
        ApiClient.getJSON(ApiClient.getUrl('UltimateMaintainer/Libraries')).then(function (list) {
            jobs.filter(function (job) { return job.config.Libraries; }).forEach(function (job) {
                var libs = list.filter(function (l) { return !job.libraryTypes || job.libraryTypes.indexOf((l.CollectionType || '').toLowerCase()) >= 0; })
                    .map(function (l) { return { id: l.Id, name: l.Name }; });
                fillPicker(view, job, 'Lib', config[job.config.Libraries], libs);
            });
        }, function () {
            jobs.filter(function (job) { return job.config.Libraries; }).forEach(function (job) {
                q(view, job, '.LibList').textContent = 'Could not load the libraries.';
            });
        });
    }

    // A section's current values, the same shape for Save and for Preview.
    function readSettings(view, job) {
        var days = parseInt(q(view, job, 'Days').value, 10);
        var s = {
            Days: Math.max(days > 0 ? days : 14, job.minDays || 1),
            Users: readPicker(view, job, 'User')
        };
        if (job.config.Libraries) {
            s.Libraries = readPicker(view, job, 'Lib');
        }
        (job.checks || []).forEach(function (c) {
            s[c[2]] = q(view, job, c[0]).checked;
        });
        if (job.config.Action) {
            s.Action = q(view, job, 'ActionUnwatched').checked ? 'unwatched' : 'watched';
        }
        return s;
    }

    // Saves every section (a Run now saves the whole page, like Save).
    function save(view) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            jobs.forEach(function (job) {
                var s = readSettings(view, job);
                config[job.config.Enabled] = q(view, job, 'Enabled').checked;
                config[job.config.Days] = s.Days;
                config[job.config.Users] = s.Users;
                if (job.config.Libraries) {
                    config[job.config.Libraries] = s.Libraries;
                }
                (job.checks || []).forEach(function (c) {
                    config[c[1]] = s[c[2]];
                });
                if (job.config.Action) {
                    config[job.config.Action] = s.Action;
                }
            });
            return ApiClient.updatePluginConfiguration(pluginId, config);
        });
    }

    // ---- last run, preview, run, undo ----

    function loadLastRun(view, job) {
        var el = q(view, job, '.LastRun');
        var undo = q(view, job, 'Undo');
        ApiClient.getJSON(ApiClient.getUrl(job.api + '/LastRun')).then(function (r) {
            if (undo) {
                undo.disabled = !r.Exists || r.Undone;
            }
            el.textContent = !r.Exists ? 'No cleanup has changed anything yet.'
                : 'Last run that changed something: ' + r.Date + ', ' + count(job, r.Changed) + ' ' + job.doneText(r) + (r.Undone ? ', undone.' : '.');
        }, function () {
            if (undo) {
                undo.disabled = true;
            }
            el.textContent = '';
        });
    }

    function countByUser(job, rows) {
        var o = {};
        rows.forEach(function (r) { var u = job.userOf(r); o[u] = (o[u] || 0) + 1; });
        return Object.keys(o).sort(function (a, b) { return o[b] - o[a]; }).map(function (k) { return escapeHtml(k) + ' ' + o[k]; }).join(', ');
    }

    function showPreview(view, job, p) {
        var rows = p.Rows || [];
        var cell = 'padding:.3em .6em;';
        var html = '<h3 style="margin: .5em 0; font-weight: 500;">Preview: ' + count(job, rows.length) + ' ' + job.verb + '</h3>';
        html += '<p class="fieldDescription">' + (rows.length ? 'By user: ' + countByUser(job, rows) + '. ' : '') +
            p.Recent + ' ' + (job.recentText || 'played recently are left alone') + '. ' +
            (p.UsersChecked != null ? plural(p.UsersChecked, 'user') + ' checked.' : plural(p.Total, 'device') + ' in all.') + '</p>';
        (p.Errors || []).forEach(function (e) {
            html += '<p style="color:#e09a2b;">' + escapeHtml(e) + '</p>';
        });
        if (rows.length) {
            html += '<div style="max-height: 28em; overflow: auto; border: 1px solid rgba(128,128,128,.3); border-radius: 4px;">' +
                '<table style="width:100%; border-collapse:collapse; font-size:.9em;"><thead><tr style="text-align:left; position:sticky; top:0; background:rgba(40,40,40,.95);">';
            job.columns.forEach(function (c) {
                html += '<th style="' + cell + (c[2] ? 'text-align:right;' : '') + '">' + c[0] + '</th>';
            });
            html += '</tr></thead><tbody>';
            rows.forEach(function (r) {
                html += '<tr style="border-top:1px solid rgba(128,128,128,.2);">';
                job.columns.forEach(function (c) {
                    html += '<td style="' + cell + (c[2] ? 'text-align:right;' : '') + '">' + escapeHtml(c[1](r)) + '</td>';
                });
                html += '</tr>';
            });
            html += '</tbody></table></div>';
        }
        if ((p.Skipped || []).length) {
            html += '<p class="fieldDescription" style="margin-top:.8em;">Skipped (' + p.Skipped.length + '): ' +
                p.Skipped.map(function (s) { return escapeHtml(s.UserName + ': ' + s.Title + ' (' + s.Reason + ')'); }).join('; ') + '</p>';
        }
        q(view, job, '.PreviewOut').innerHTML = html;
    }

    function preview(view, job) {
        loading.show();
        return postJson(job.api + '/Preview', readSettings(view, job)).then(function (p) {
            loading.hide();
            showPreview(view, job, p);
            return p;
        }, function (err) {
            loading.hide();
            errorText(err).then(toast);
            throw err;
        });
    }

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

    // Preview first, so the box can say how many; nothing happens on Cancel.
    function runNow(view, job) {
        preview(view, job).then(function (p) {
            var n = (p.Rows || []).length;
            if (!n) {
                toast('Nothing to clean up with these settings.');
                return;
            }
            return ask('Run the cleanup now?', 'This saves the settings and changes ' + count(job, n) +
                ' (listed in the preview). ' + (job.undoText ? 'Undo can put them back.' : 'This cannot be undone.'), 'Run now').then(function (yes) {
                if (!yes) {
                    return;
                }
                loading.show();
                return save(view).then(function () {
                    return postJson(job.api + '/Run');
                }).then(function (r) {
                    loading.hide();
                    toast('Cleanup done: ' + count(job, r.Exists ? r.Changed : 0) + ' ' + job.doneText(r) + '.');
                    loadLastRun(view, job);
                    preview(view, job).catch(function () { });
                }, function (err) {
                    loading.hide();
                    errorText(err).then(toast);
                });
            });
        }, function () { /* preview already said why */ });
    }

    function undo(view, job) {
        ask('Undo the last cleanup?', job.undoText, 'Undo').then(function (yes) {
            if (!yes) {
                return;
            }
            loading.show();
            postJson(job.api + '/Undo').then(function (r) {
                loading.hide();
                toast('Undo done: ' + r.Restored + ' put back' + (r.Skipped ? ', ' + r.Skipped + ' left alone' : '') + '.');
                loadLastRun(view, job);
            }, function (err) {
                loading.hide();
                errorText(err).then(toast);
            });
        });
    }

    // ---- the page ----

    function View(view, params) {
        BaseView.apply(this, arguments);

        jobs.forEach(function (job) {
            ['UsersAll', 'UsersChosen', 'LibsAll', 'LibsChosen'].forEach(function (id) {
                var el = q(view, job, id);
                if (el) {
                    el.addEventListener('change', function () { showPickers(view, job); });
                }
            });
            q(view, job, 'Preview').addEventListener('click', function () { preview(view, job).catch(function () { }); });
            q(view, job, 'RunNow').addEventListener('click', function () { runNow(view, job); });
            if (job.undoText) {
                q(view, job, 'Undo').addEventListener('click', function () { undo(view, job); });
            }
        });

        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            save(view).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            }, function () {
                loading.hide();
                toast('Saving failed.');
            });
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    // Runs every time the page is shown: load the settings, the pickers and the last-run lines.
    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            jobs.forEach(function (job) {
                q(view, job, '.PreviewOut').innerHTML = '';
                q(view, job, 'Enabled').checked = !!config[job.config.Enabled];
                q(view, job, 'Days').value = config[job.config.Days] || 14;
                (job.checks || []).forEach(function (c) {
                    q(view, job, c[0]).checked = !!config[c[1]];
                });
                if (job.config.Action) {
                    var unwatched = config[job.config.Action] === 'unwatched';
                    q(view, job, 'ActionUnwatched').checked = unwatched;
                    q(view, job, 'ActionWatched').checked = !unwatched;
                }
                loadLastRun(view, job);
            });
            loadPickers(view, config);
            loading.hide();
        });
    };

    return View;
});
