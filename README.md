# Vidy for Jellyfin (target: Jellyfin Server 12.4)

Search TMDB from Jellyfin Web, browse movies / series / seasons / episodes, and play them through the
[Vidy embed player](https://www.vidy.st/), with per-user progress, resume, watched marks, Continue Watching
and automatic next-episode playback.

> **Status: first build, never compiled or run.** The source was written in a sandbox that had no .NET SDK and no
> access to NuGet, TMDB, Vidy or a Jellyfin server. See [TESTING.md](TESTING.md) for exactly what has and has not
> been verified. Expect that the first CI build may need a fix or two.

> **Content notice.** Vidy's documentation says nothing about content licensing. Make sure you are entitled to use the
> streams it serves. The plugin only builds the documented embed URLs; it does not host or bypass anything.

## What it does

| Feature | How |
|---|---|
| TMDB search | Server endpoint `GET /Vidy/Search`; ranked by title similarity + year, de-duplicated, movie/TV labelled |
| Details, seasons, episodes | TMDB `movie`, `tv`, `tv/{id}/season/{n}` (cached, loaded on demand) |
| Playback | Iframe to `https://www.vidy.st/movie/{id}` or `/tv/{id}/{season}/{episode}` with the documented `allow` policy |
| Progress / resume | Vidy `PLAYER_EVENT` messages -> `POST /Vidy/Progress` -> JSON file per Jellyfin user; resume via `progress=` |
| Watched | Configurable threshold (default 90 %) or an `ended` event; merely opening never marks watched |
| Next episode | Computed by the plugin from TMDB (skips gaps and empty seasons, crosses seasons); countdown + cancel |
| Continue Watching | Row on the Jellyfin home screen: unfinished movies, "resume" or "next episode" per series |
| Dashboard | Dashboard -> Plugins -> Vidy: API key + test, search, playback, history, cache settings |

## Design decisions you should know about

* **Vidy's own `autoplayNextEpisode`/`nextEpisode` flags are not used.** Vidy documents no event telling the page which
  episode it moved to, so progress would be saved against the wrong episode. The plugin does the advancing itself.
  The optional in-player *episode selector* has the same caveat (off by default).
* **`MEDIA_DATA` messages are ignored** - Vidy documents it only as an opaque serialized string.
* **Only `PLAYER_EVENT` fields documented by Vidy are used** (`event`, `currentTime`, `duration`). Messages are accepted only from
  `https://vidy.st` / `https://www.vidy.st`, from the exact iframe window, as JSON strings, with a known event name and finite numbers.
* **The TMDB key never reaches the browser.** The browser calls this plugin's `/Vidy/*` endpoints, which require a signed-in user.
* **Search integration is browser-side.** Jellyfin has no supported hook for a server plugin to add results to the stock search
  page, so `vidy.js` (a) adds a "Vidy (TMDB)" section under Jellyfin's search results and (b) provides a floating **Vidy** button
  with its own search box. (a) depends on Jellyfin Web's page structure, which is not a stable API; (b) always works.
* **The script is added to Jellyfin Web's `index.html` at server start.** If the web folder is read-only (Docker images, some
  packages), the plugin logs a warning; load `/Vidy/Client/vidy.js` with a JavaScript-injector plugin instead.
  Jellyfin updates replace `index.html`; the plugin re-adds the tag on the next start.
* **Native apps (Android, iOS, TV, Swiftfin...) do not run this script** and are not supported.

## Build (no local tools needed)

1. Create a free GitHub account and a new empty repository.
2. Upload the contents of this folder (keep the folder structure, including `.github/workflows/build.yml`).
3. Open the repository's **Actions** tab. The *Build and test Vidy plugin* workflow runs on every push.
4. When it finishes, open the run and download the **vidy-plugin** artifact (contains `vidy_0.1.0.0.zip`) and
   **test-results** (the real unit-test report).
5. If the run fails, copy the red error text from the log and send it to me.

Local build (needs the .NET 10 SDK): `dotnet test tests/Jellyfin.Plugin.Vidy.Tests` then `bash scripts/package.sh`.

## Install (manual)

1. Stop Jellyfin.
2. Create a folder `Vidy_0.1.0.0` in Jellyfin's `plugins` directory and unzip `vidy_0.1.0.0.zip` into it
   (Linux `/var/lib/jellyfin/plugins/`, Docker `/config/plugins/`, Windows `%ProgramData%\Jellyfin\Server\plugins\`).
3. Start Jellyfin. Dashboard -> Plugins should list **Vidy** as *Active*.
4. Restart Jellyfin once more if you want the script tag added (it is added on start, after the plugin first loads).
5. Hard-refresh the browser (Ctrl+F5).

**Repository install is not available yet.** `scripts/package.sh` writes a `manifest.json`, but its `sourceUrl` must point to a public
copy of the ZIP (for example a GitHub Release asset). Until you host the ZIP and set `SOURCE_URL`, use the manual install.

## Configure

Dashboard -> Plugins -> Vidy.

1. Paste your TMDB key (free at themoviedb.org -> Settings -> API; the v3 key or the v4 read token both work).
2. Click **Save and test connection**.
3. Adjust playback and history options; **Save**.

## Troubleshooting

| Symptom | Check |
|---|---|
| Plugin shows *NotSupported* | Package version must match the server: `Jellyfin.Controller` / `Jellyfin.Model` 12.4.0 and `targetAbi` 12.4.0.0. Server and plugin must be 12.4. |
| No **Vidy** button in Jellyfin Web | Look in the Jellyfin log for "Vidy: ..."; read-only web folder? Hard-refresh; check `/Vidy/Client/vidy.js` loads in the browser. |
| "TMDB rejected the API key" | Re-copy the key; use the test button. |
| Search works in the Vidy box but not on Jellyfin's search page | Jellyfin Web markup changed; use the floating button and report the page's HTML structure. |
| Player never starts with sound | Browsers block autoplay until you have clicked the page; click play. Allow-policy is Vidy's documented `encrypted-media; autoplay *; fullscreen *`. |
| Player blank / "taking a long time" | Vidy may be down or blocked on your network/browser. Use **Reload player**. |
| Progress not saved | A toast appears if the server rejects it; check the Jellyfin log. Tracking must be enabled in settings. |

## Layout

```
src/Jellyfin.Plugin.Vidy.Core/   plain .NET library: TMDB client, Vidy URLs, history, next-episode logic (unit-tested)
src/Jellyfin.Plugin.Vidy/        Jellyfin plugin: config, REST controller, script injection, Dashboard page, vidy.js/css
tests/                           xUnit tests (mocked HTTP; no network needed)
scripts/package.sh               builds the ZIP, md5 and manifest
```

Licence: GPL-3.0 (required because the plugin links against Jellyfin's GPL packages).
TMDB attribution: this product uses the TMDB API but is not endorsed or certified by TMDB.
