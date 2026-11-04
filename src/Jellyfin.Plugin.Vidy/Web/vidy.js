/* Vidy plugin - browser client for Jellyfin Web.
 *
 * What this does (and does not do):
 *  - Adds a "Vidy (TMDB)" section to Jellyfin's search page. Selectors for Jellyfin's search page are not a
 *    stable public API, so a floating "Vidy" button (own search box) is always available as a fallback.
 *  - Shows TMDB movie / series details, seasons and episodes in an overlay, and plays titles in a Vidy iframe.
 *  - Saves progress through the plugin's server endpoints (per Jellyfin user), resumes, and advances episodes.
 *  - Only works in Jellyfin Web (browsers). Native apps do not load this script.
 */
(function () {
    'use strict';
    if (window.__vidyPlugin) { return; }
    window.__vidyPlugin = true;

    var TRUSTED = ['https://vidy.st', 'https://www.vidy.st'];
    var IMG = 'https://image.tmdb.org/t/p/';
    var EVENTS = ['timeupdate', 'play', 'pause', 'ended'];
    var scriptSrc = (document.currentScript && document.currentScript.src) || '';

    var cfg = null, cfgPromise = null;
    var overlay = null, player = null;
    var searchTimer = null, searchCtl = null, searchState = null;
    var continueState = { at: 0, data: null, loading: false };

    // ------------------------------------------------------------------ helpers

    function el(tag, attrs, kids) {
        var n = document.createElement(tag);
        if (attrs) {
            Object.keys(attrs).forEach(function (k) {
                var v = attrs[k];
                if (v === null || v === undefined || v === false) { return; }
                if (k === 'class') { n.className = v; }
                else if (k === 'text') { n.textContent = v; }
                else if (k.indexOf('on') === 0 && typeof v === 'function') { n.addEventListener(k.slice(2), v); }
                else { n.setAttribute(k, v === true ? '' : String(v)); }
            });
        }
        (kids || []).forEach(function (c) {
            if (c === null || c === undefined) { return; }
            n.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
        });
        return n;
    }

    // Only TMDB-style relative paths are ever turned into image URLs.
    function img(path, size) {
        return (typeof path === 'string' && /^\/[\w.\-]+$/.test(path)) ? IMG + size + path : null;
    }

    function fmtTime(s) {
        s = Math.max(0, Math.floor(s || 0));
        var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
        var mm = (h ? ('0' + m).slice(-2) : String(m)) + ':' + ('0' + sec).slice(-2);
        return h ? h + ':' + mm : mm;
    }

    function errMsg(e) {
        if (e && e.name === 'TypeError') { return 'Could not reach the Jellyfin server. Check your connection and try again.'; }
        return (e && e.message) || 'Something went wrong.';
    }

    function toast(msg) {
        var t = el('div', { class: 'vidy-toast', role: 'status', text: msg });
        document.body.appendChild(t);
        setTimeout(function () { t.remove(); }, 6000);
    }

    function signedIn() {
        var c = window.ApiClient;
        return !!(c && c.accessToken && c.accessToken() && c.getUrl);
    }

    function call(path, params, opts) {
        var c = window.ApiClient;
        if (!signedIn()) { return Promise.reject(new Error('You are not signed in to Jellyfin.')); }
        opts = opts || {};
        var headers = { 'Authorization': 'MediaBrowser Token="' + c.accessToken() + '"', 'Accept': 'application/json' };
        var body;
        if (opts.body !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(opts.body); }
        return fetch(c.getUrl(path, params || undefined), {
            method: opts.method || 'GET', headers: headers, body: body, signal: opts.signal, keepalive: !!opts.keepalive
        }).then(function (res) {
            return res.text().then(function (t) {
                var d = null;
                try { d = t ? JSON.parse(t) : null; } catch (ignore) { /* non-JSON error page */ }
                if (!res.ok) {
                    var e = new Error((d && d.error) || ('The server returned an error (' + res.status + ').'));
                    e.status = res.status;
                    throw e;
                }
                return d;
            });
        });
    }

    function getCfg() {
        if (cfg) { return Promise.resolve(cfg); }
        if (!cfgPromise) {
            cfgPromise = call('Vidy/Config').then(function (c) {
                cfg = c;
                if (c && c.accentColor && /^[0-9A-Fa-f]{6}$/.test(c.accentColor)) {
                    document.documentElement.style.setProperty('--vidy-accent', '#' + c.accentColor);
                }
                return c;
            }).catch(function (e) { cfgPromise = null; throw e; });
        }
        return cfgPromise;
    }

    function loadCss() {
        if (document.getElementById('vidy-css')) { return; }
        var href = scriptSrc ? scriptSrc.replace(/vidy\.js(\?.*)?$/, 'vidy.css') : '../Vidy/Client/vidy.css';
        document.head.appendChild(el('link', { id: 'vidy-css', rel: 'stylesheet', href: href }));
    }

    function setLoading(node, text) { node.textContent = ''; node.appendChild(el('div', { class: 'vidy-status', role: 'status', text: text })); }

    function setError(node, message, retry) {
        node.textContent = '';
        node.appendChild(el('div', { class: 'vidy-error', role: 'alert' }, [
            el('div', { text: message }),
            retry ? el('button', { class: 'vidy-btn', type: 'button', text: 'Retry', onclick: retry }) : null
        ]));
    }

    // ------------------------------------------------------------------ cards

    function card(r) {
        var p = img(r.posterPath, 'w342');
        return el('button', { class: 'vidy-card', type: 'button', title: r.title, onclick: function () { openDetails(r.kind, r.tmdbId); } }, [
            p ? el('img', { src: p, loading: 'lazy', alt: '' }) : el('div', { class: 'vidy-noposter', text: 'No poster' }),
            el('div', { class: 'vidy-card-title', text: r.title }),
            el('div', { class: 'vidy-card-sub' }, [(r.year ? r.year + ' ' : ''), el('span', { class: 'vidy-badge', text: r.kind === 'tv' ? 'TV' : 'Movie' })])
        ]);
    }

    function resultsGrid(results) {
        if (!results.length) { return el('div', { class: 'vidy-status', text: 'No results on TMDB.' }); }
        return el('div', { class: 'vidy-grid' }, results.map(card));
    }

    // ------------------------------------------------------------------ search integration (Jellyfin's search page)

    function onSearchInput(e) {
        var t = e.target;
        if (!t || t.tagName !== 'INPUT' || (t.closest && t.closest('.vidy-overlay'))) { return; }
        if (location.hash.toLowerCase().indexOf('search') < 0) { return; }
        var id = (t.id || '').toLowerCase(), cls = String(t.className || '').toLowerCase();
        if (!(t.type === 'search' || id.indexOf('search') > -1 || cls.indexOf('search') > -1)) { return; }
        clearTimeout(searchTimer);
        var q = t.value.trim();
        searchTimer = setTimeout(function () { runPageSearch(q); }, 450);
    }

    function runPageSearch(q) {
        if (searchCtl) { searchCtl.abort(); }
        if (q.length < 2) { searchState = null; removeSearchSection(); return; }
        getCfg().then(function (c) {
            if (!c.tmdbEnabled) { return; }
            if (!c.tmdbConfigured) {
                searchState = { q: q, status: 'error', message: 'Vidy needs a TMDB API key. An administrator can add it in Dashboard → Plugins → Vidy.' };
                paintSearch(); return;
            }
            searchState = { q: q, status: 'loading' };
            paintSearch();
            searchCtl = new AbortController();
            var mine = searchState;
            return call('Vidy/Search', { q: q }, { signal: searchCtl.signal }).then(function (d) {
                if (searchState !== mine) { return; }
                searchState = { q: q, status: 'ok', results: d.results || [] };
                paintSearch();
            }).catch(function (e) {
                if (e && e.name === 'AbortError') { return; }
                searchState = { q: q, status: 'error', message: errMsg(e) };
                paintSearch();
            });
        }).catch(function () { /* config unavailable (e.g. not signed in yet); ignore */ });
    }

    function removeSearchSection() {
        var old = document.querySelector('.vidy-search');
        if (old) { old.remove(); }
    }

    function searchHost() {
        return document.querySelector('.searchResults') || document.querySelector('.libraryPage:not(.hide)') || document.querySelector('.page:not(.hide)');
    }

    function paintSearch() {
        removeSearchSection();
        if (!searchState) { return; }
        var host = searchHost();
        if (!host) { return; }
        var body = el('div');
        var sec = el('div', { class: 'vidy-search verticalSection' }, [el('h2', { class: 'sectionTitle sectionTitle-cards', text: 'Vidy (TMDB)' }), body]);
        if (searchState.status === 'loading') { setLoading(body, 'Searching TMDB…'); }
        else if (searchState.status === 'error') { setError(body, searchState.message, function () { runPageSearch(searchState.q); }); }
        else { body.appendChild(resultsGrid(searchState.results)); }
        host.appendChild(sec);
    }

    // ------------------------------------------------------------------ overlay

    function topbar(title, onBack) {
        return el('div', { class: 'vidy-top' }, [
            onBack ? el('button', { class: 'vidy-btn vidy-secondary', type: 'button', text: '← Back', onclick: onBack }) : null,
            el('h2', { text: title }),
            el('button', { class: 'vidy-btn vidy-secondary', type: 'button', 'aria-label': 'Close', text: '✕', onclick: closeOverlay })
        ]);
    }

    function openOverlay() {
        flushProgress(); stopPlayer();
        if (overlay) { overlay.remove(); }
        overlay = el('div', { class: 'vidy-overlay', role: 'dialog', 'aria-modal': 'true' });
        document.body.appendChild(overlay);
        document.body.classList.add('vidy-noscroll');
        return overlay;
    }

    function closeOverlay() {
        flushProgress(); stopPlayer();
        if (overlay) { overlay.remove(); overlay = null; }
        document.body.classList.remove('vidy-noscroll');
        continueState.at = 0; // refresh Continue Watching next time it is shown
    }

    function openSearchOverlay() {
        var o = openOverlay();
        var results = el('div');
        var ctl = null, timer = null;
        var input = el('input', { class: 'vidy-input', type: 'text', placeholder: 'Search movies and TV on TMDB…', 'aria-label': 'Search TMDB' });
        input.addEventListener('input', function () {
            clearTimeout(timer);
            timer = setTimeout(function () {
                var q = input.value.trim();
                if (ctl) { ctl.abort(); }
                if (q.length < 2) { results.textContent = ''; return; }
                setLoading(results, 'Searching TMDB…');
                ctl = new AbortController();
                call('Vidy/Search', { q: q }, { signal: ctl.signal }).then(function (d) {
                    results.textContent = ''; results.appendChild(resultsGrid(d.results || []));
                }).catch(function (e) {
                    if (e && e.name === 'AbortError') { return; }
                    setError(results, errMsg(e), function () { input.dispatchEvent(new Event('input')); });
                });
            }, 450);
        });
        o.appendChild(topbar('Search Vidy'));
        o.appendChild(el('div', { class: 'vidy-body' }, [input, results]));
        input.focus();
        getCfg().then(function (c) {
            if (!c.tmdbConfigured) {
                setError(results, 'TMDB is not configured. An administrator can add the API key in Dashboard → Plugins → Vidy.');
            }
        }).catch(function (e) { setError(results, errMsg(e)); });
    }

    // ------------------------------------------------------------------ details

    function openDetails(kind, id) {
        var o = openOverlay();
        var body = el('div', { class: 'vidy-body' });
        o.appendChild(topbar('Details'));
        o.appendChild(body);
        setLoading(body, 'Loading metadata…');
        var retry = function () { openDetails(kind, id); };
        if (kind === 'tv') {
            call('Vidy/Tv/' + id).then(function (d) { return renderTv(body, d); }).catch(function (e) { setError(body, errMsg(e), retry); });
        } else {
            call('Vidy/Movie/' + id).then(function (d) { return renderMovie(body, d); }).catch(function (e) { setError(body, errMsg(e), retry); });
        }
    }

    function hero(d, title, metaParts) {
        var p = img(d.posterPath, 'w342');
        return el('div', { class: 'vidy-hero' }, [
            p ? el('img', { src: p, alt: '' }) : null,
            el('div', { class: 'vidy-hero-info' }, [
                el('h1', { text: title }),
                (d.originalTitle || d.originalName) && (d.originalTitle || d.originalName) !== title ? el('div', { class: 'vidy-meta', text: 'Original title: ' + (d.originalTitle || d.originalName) }) : null,
                el('div', { class: 'vidy-meta', text: metaParts.filter(Boolean).join(' · ') }),
                el('p', { text: d.overview || 'No description available.' })
            ])
        ]);
    }

    function renderMovie(body, d) {
        return call('Vidy/History/Item', { kind: 'movie', id: d.tmdbId }).catch(function () { return { entry: null }; }).then(function (h) {
            var e = h && h.entry, resume = e && !e.watched && e.positionSeconds >= 5;
            var h1 = hero(d, d.title, [d.year, d.runtime ? d.runtime + ' min' : null, d.contentRating, (d.genres || []).join(', ')]);
            var play = el('button', {
                class: 'vidy-btn', type: 'button',
                text: resume ? 'Resume from ' + fmtTime(e.positionSeconds) : 'Play',
                onclick: function () { openPlayer({ kind: 'movie', id: d.tmdbId, title: d.title, posterPath: d.posterPath }); }
            });
            body.textContent = '';
            body.appendChild(h1);
            h1.querySelector('.vidy-hero-info').appendChild(el('div', { class: 'vidy-actions' }, [play, e && e.watched ? el('span', { class: 'vidy-meta', text: '✓ Watched' }) : null]));
        });
    }

    function renderTv(body, d) {
        var seasons = (d.seasons || []).filter(function (s) { return s.episodeCount > 0; });
        return Promise.all([
            call('Vidy/History/Tv/' + d.tmdbId).catch(function () { return { entries: [] }; }),
            call('Vidy/ContinueWatching').catch(function () { return { items: [] }; })
        ]).then(function (res) {
            var entries = res[0].entries || [], cont = (res[1].items || []).filter(function (i) { return i.kind === 'tv' && i.tmdbId === d.tmdbId; })[0];
            var byKey = {}, watchedBySeason = {};
            entries.forEach(function (e) {
                byKey[e.season + ':' + e.episode] = e;
                if (e.watched) { watchedBySeason[e.season] = (watchedBySeason[e.season] || 0) + 1; }
            });
            var totalWatched = entries.filter(function (e) { return e.watched; }).length;
            var h1 = hero(d, d.name, [d.year, d.numberOfSeasons + ' season' + (d.numberOfSeasons === 1 ? '' : 's'), d.numberOfEpisodes + ' episodes', d.contentRating, (d.genres || []).join(', ')]);
            body.textContent = '';
            body.appendChild(h1);
            var info = h1.querySelector('.vidy-hero-info');
            info.appendChild(el('div', { class: 'vidy-meta', text: totalWatched + ' of ' + d.numberOfEpisodes + ' episodes watched' }));
            if (cont) {
                info.appendChild(el('div', { class: 'vidy-actions' }, [el('button', {
                    class: 'vidy-btn', type: 'button',
                    text: (cont.action === 'resume' ? 'Resume ' : 'Continue with ') + cont.label,
                    onclick: function () { openPlayer({ kind: 'tv', id: d.tmdbId, season: cont.season, episode: cont.episode, title: d.name, posterPath: d.posterPath }); }
                })]));
            }
            if (!seasons.length) {
                body.appendChild(el('div', { class: 'vidy-status', text: 'TMDB lists no episodes for this series, so there is nothing to play.' }));
                return;
            }
            var first = seasons.filter(function (s) { return s.seasonNumber > 0; })[0] || seasons[0];
            var sel = el('select', { class: 'vidy-select', 'aria-label': 'Season' });
            seasons.forEach(function (s) {
                var w = watchedBySeason[s.seasonNumber] || 0;
                sel.appendChild(el('option', { value: String(s.seasonNumber), text: (s.name || ('Season ' + s.seasonNumber)) + ' · ' + s.episodeCount + ' episodes' + (w ? ' · ' + w + ' watched' : '') }));
            });
            sel.value = String(first.seasonNumber);
            var list = el('div', { class: 'vidy-episodes' });
            body.appendChild(el('div', { class: 'vidy-actions' }, [sel]));
            body.appendChild(list);
            var loadSeason = function () {
                var n = parseInt(sel.value, 10);
                setLoading(list, 'Loading episodes…');
                call('Vidy/Tv/' + d.tmdbId + '/Season/' + n).then(function (sd) {
                    list.textContent = '';
                    if (!sd.episodes || !sd.episodes.length) { list.appendChild(el('div', { class: 'vidy-status', text: 'No episodes are listed for this season.' })); return; }
                    sd.episodes.forEach(function (ep) { list.appendChild(episodeRow(d, ep, byKey[ep.seasonNumber + ':' + ep.episodeNumber])); });
                }).catch(function (e) { setError(list, errMsg(e), loadSeason); });
            };
            sel.addEventListener('change', loadSeason);
            loadSeason();
        });
    }

    function episodeRow(show, ep, entry) {
        var still = img(ep.stillPath, 'w300');
        var watched = entry && entry.watched;
        var resume = entry && !watched && entry.positionSeconds >= 5;
        var pct = entry && entry.durationSeconds > 0 ? Math.min(100, Math.round(entry.positionSeconds / entry.durationSeconds * 100)) : 0;
        var meta = [ep.airDate, ep.runtime ? ep.runtime + ' min' : null, watched ? '✓ Watched' : null].filter(Boolean).join(' · ');
        return el('div', { class: 'vidy-ep' }, [
            still ? el('img', { src: still, loading: 'lazy', alt: '' }) : el('div', { class: 'vidy-noposter vidy-ep-noimg', text: 'No image' }),
            el('div', { class: 'vidy-ep-info' }, [
                el('div', { class: 'vidy-ep-title', text: (ep.seasonNumber === 0 ? 'Special ' : 'E') + ep.episodeNumber + ' · ' + (ep.name || ('Episode ' + ep.episodeNumber)) }),
                el('div', { class: 'vidy-ep-meta', text: meta }),
                el('p', { class: 'vidy-ep-ov', text: ep.overview || 'No description available.' }),
                (pct > 0 && !watched) ? el('div', { class: 'vidy-bar', title: pct + '% watched' }, [el('div', { style: 'width:' + pct + '%' })]) : null
            ]),
            el('button', {
                class: 'vidy-btn', type: 'button', text: resume ? 'Resume' : 'Play',
                onclick: function () { openPlayer({ kind: 'tv', id: show.tmdbId, season: ep.seasonNumber, episode: ep.episodeNumber, title: show.name, posterPath: show.posterPath }); }
            })
        ]);
    }

    // ------------------------------------------------------------------ player

    function isTrustedUrl(u) {
        try { var x = new URL(u); return x.protocol === 'https:' && TRUSTED.indexOf(x.origin) > -1; } catch (e) { return false; }
    }

    function stopPlayer() {
        if (player && player.timer) { clearInterval(player.timer); }
        player = null;
    }

    function epLabel(ctx) { return ctx.kind === 'tv' ? ' · ' + (ctx.season === 0 ? 'Special ' : 'S' + ctx.season + 'E') + ctx.episode : ''; }

    function openPlayer(ctx, forceAutoplay) {
        var o = openOverlay();
        var body = el('div', { class: 'vidy-body' });
        o.appendChild(topbar((ctx.title || 'Player') + epLabel(ctx), function () { openDetails(ctx.kind, ctx.id); }));
        o.appendChild(body);
        setLoading(body, 'Loading player…');
        var params = { kind: ctx.kind, id: ctx.id };
        if (ctx.kind === 'tv') { params.season = ctx.season; params.episode = ctx.episode; }
        if (forceAutoplay) { params.forceAutoplay = 'true'; }
        call('Vidy/Player', params).then(function (data) {
            if (!isTrustedUrl(data.url)) { throw new Error('Blocked a player URL that is not on a trusted Vidy origin.'); }
            if (overlay !== o) { return; }
            body.textContent = '';
            var wrap = el('div', { class: 'vidy-player' });
            wrap.style.aspectRatio = String(data.aspectRatio || '16:9').replace(':', ' / ');
            var loading = el('div', { class: 'vidy-loading', role: 'status', text: 'Loading player…' });
            var frame = el('iframe', { src: data.url, allow: data.allow, allowfullscreen: true, frameborder: '0', title: 'Vidy player' });
            wrap.appendChild(frame); wrap.appendChild(loading);
            body.appendChild(wrap);
            player = { ctx: ctx, data: data, frame: frame, wrap: wrap, lastTime: 0, lastDur: 0, lastSent: 0, ended: false, warned: false, timer: null, msg: el('div', { class: 'vidy-note', role: 'status' }) };
            frame.addEventListener('load', function () { loading.remove(); if (player && player.loadTimer) { clearTimeout(player.loadTimer); } });
            player.loadTimer = setTimeout(function () {
                if (loading.parentNode) { loading.textContent = 'The player is taking a long time to load. Vidy may be unavailable.'; }
            }, 20000);
            if (data.startSeconds) { player.msg.textContent = 'Resuming from ' + fmtTime(data.startSeconds) + '.'; }
            body.appendChild(player.msg);
            var actions = el('div', { class: 'vidy-actions' });
            actions.appendChild(el('button', { class: 'vidy-btn vidy-secondary', type: 'button', text: 'Reload player', onclick: function () { openPlayer(ctx, forceAutoplay); } }));
            if (data.next && data.settings.allowManualNext) {
                actions.appendChild(el('button', { class: 'vidy-btn', type: 'button', text: 'Next: S' + data.next.season + 'E' + data.next.episode + (data.next.name ? ' · ' + data.next.name : ''), onclick: playNext }));
            }
            body.appendChild(actions);
        }).catch(function (e) { setError(body, errMsg(e), function () { openPlayer(ctx, forceAutoplay); }); });
    }

    function playNext() {
        var p = player;
        if (!p || !p.data.next) { return; }
        flushProgress();
        openPlayer({ kind: 'tv', id: p.ctx.id, season: p.data.next.season, episode: p.data.next.episode, title: p.ctx.title, posterPath: p.ctx.posterPath }, true);
    }

    function sendProgress(eventName) {
        var p = player;
        if (!p || !p.data.settings.tracking) { return Promise.resolve(); }
        if (p.lastTime <= 0 && eventName !== 'ended') { return Promise.resolve(); }
        p.lastSent = Date.now();
        var body = { kind: p.ctx.kind, id: p.ctx.id, season: p.ctx.season, episode: p.ctx.episode, position: p.lastTime, duration: p.lastDur, event: eventName, title: p.ctx.title, posterPath: p.ctx.posterPath };
        return call('Vidy/Progress', null, { method: 'POST', body: body, keepalive: true }).catch(function (e) {
            console.warn('[Vidy] progress was not saved:', e && e.message);
            if (!p.warned) { p.warned = true; toast('Vidy could not save your progress: ' + errMsg(e)); }
        });
    }

    function flushProgress() {
        if (player && !player.ended && player.lastTime > 0) { sendProgress('pause'); }
    }

    function handleEnded() {
        var p = player;
        if (!p) { return; }
        var s = p.data.settings;
        if (!p.data.next) { p.msg.textContent = p.ctx.kind === 'tv' ? 'You have reached the end of the series.' : 'Finished.'; return; }
        if (!s.autoNextEpisode) { p.msg.textContent = 'Episode finished.'; return; }
        if (!s.countdownEnabled) { playNext(); return; }
        var left = s.countdownSeconds || 8;
        var label = el('span', { text: '' });
        var box = el('div', { class: 'vidy-countdown', role: 'alert' }, [label]);
        var cancel = el('button', { class: 'vidy-btn vidy-secondary', type: 'button', text: 'Cancel', onclick: function () { clearInterval(p.timer); p.timer = null; box.remove(); } });
        var tick = function () { label.textContent = 'Next episode in ' + left + ' s'; };
        if (s.allowManualNext) { box.appendChild(el('button', { class: 'vidy-btn', type: 'button', text: 'Play now', onclick: function () { clearInterval(p.timer); playNext(); } })); }
        box.appendChild(cancel);
        p.wrap.appendChild(box);
        tick();
        p.timer = setInterval(function () {
            left -= 1;
            if (left <= 0) { clearInterval(p.timer); p.timer = null; playNext(); } else { tick(); }
        }, 1000);
    }

    // Strict handling of messages from the Vidy iframe: trusted origin, the exact iframe window, a JSON string,
    // a known event name, finite non-negative numbers. Everything else is ignored.
    function onMessage(ev) {
        if (!player || TRUSTED.indexOf(ev.origin) < 0) { return; }
        if (ev.source !== player.frame.contentWindow) { return; }
        if (typeof ev.data !== 'string' || ev.data.length === 0 || ev.data.length > 20000) { return; }
        var m;
        try { m = JSON.parse(ev.data); } catch (e) { return; }
        if (!m || typeof m !== 'object' || EVENTS.indexOf(m.event) < 0) { return; }
        var t = m.currentTime, d = m.duration;
        if (t !== undefined && t !== null && !(typeof t === 'number' && isFinite(t) && t >= 0)) { return; }
        if (d !== undefined && d !== null && !(typeof d === 'number' && isFinite(d) && d >= 0)) { return; }
        var p = player;
        if (typeof t === 'number') { p.lastTime = t; }
        if (typeof d === 'number' && d > 0) { p.lastDur = d; }
        if (m.event === 'timeupdate') {
            if (Date.now() - p.lastSent >= (p.data.settings.syncSeconds || 10) * 1000) { sendProgress('timeupdate'); }
        } else if (m.event === 'pause') {
            sendProgress('pause');
        } else if (m.event === 'ended') {
            p.ended = true;
            sendProgress('ended').then(handleEnded);
        }
    }

    // ------------------------------------------------------------------ Continue Watching on the home screen

    function onHome() { return /#\/home/i.test(location.hash); }

    function ensureContinue() {
        if (!onHome() || !cfg || !cfg.continueWatching || document.querySelector('.vidy-continue')) { return; }
        var host = document.querySelector('.homeSectionsContainer');
        if (!host) { return; }
        var fresh = Date.now() - continueState.at < 60000;
        if (!fresh && !continueState.loading) {
            continueState.loading = true;
            call('Vidy/ContinueWatching').then(function (d) {
                continueState.data = d; continueState.at = Date.now();
                (d.warnings || []).forEach(function (w) { console.warn('[Vidy]', w); });
            }).catch(function (e) { console.warn('[Vidy] Continue Watching unavailable:', e && e.message); continueState.at = Date.now(); })
                .then(function () { continueState.loading = false; });
        }
        var items = continueState.data && continueState.data.items;
        if (!items || !items.length) { return; }
        var row = el('div', { class: 'vidy-row' }, items.map(continueCard));
        var sec = el('div', { class: 'vidy-continue verticalSection' }, [el('h2', { class: 'sectionTitle sectionTitle-cards', text: 'Continue Watching (Vidy)' }), row]);
        host.insertBefore(sec, host.firstChild);
    }

    function continueCard(i) {
        var p = img(i.posterPath, 'w342');
        return el('button', {
            class: 'vidy-card', type: 'button', title: (i.title || '') + ' ' + i.label,
            onclick: function () { openPlayer({ kind: i.kind, id: i.tmdbId, season: i.season, episode: i.episode, title: i.title, posterPath: i.posterPath }); }
        }, [
            p ? el('img', { src: p, loading: 'lazy', alt: '' }) : el('div', { class: 'vidy-noposter', text: 'No poster' }),
            i.percent > 0 ? el('div', { class: 'vidy-bar' }, [el('div', { style: 'width:' + i.percent + '%' })]) : null,
            el('div', { class: 'vidy-card-title', text: i.title || 'Untitled' }),
            el('div', { class: 'vidy-card-sub', text: (i.action === 'resume' ? 'Resume ' : 'Next: ') + i.label })
        ]);
    }

    // ------------------------------------------------------------------ boot

    function ensureFab() {
        if (document.querySelector('.vidy-fab') || !signedIn()) { return; }
        document.body.appendChild(el('button', { class: 'vidy-fab', type: 'button', text: 'Vidy', title: 'Search TMDB and watch with Vidy', onclick: openSearchOverlay }));
    }

    function tick() {
        if (!signedIn()) {
            var f = document.querySelector('.vidy-fab'); if (f) { f.remove(); }
            return;
        }
        if (!cfg) { getCfg().catch(function () { /* retried on the next tick */ }); return; }
        if (!cfg.tmdbEnabled) { return; }
        ensureFab();
        if (searchState && location.hash.toLowerCase().indexOf('search') > -1) {
            if (!document.querySelector('.vidy-search')) { paintSearch(); }
        } else if (document.querySelector('.vidy-search')) {
            removeSearchSection();
        }
        ensureContinue();
    }

    loadCss();
    document.addEventListener('input', onSearchInput, true);
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && overlay && !document.fullscreenElement) { closeOverlay(); } });
    window.addEventListener('message', onMessage);
    window.addEventListener('pagehide', flushProgress);
    window.addEventListener('hashchange', function () { if (location.hash.toLowerCase().indexOf('search') < 0) { searchState = null; } tick(); });
    setInterval(tick, 1500);
    tick();
})();
