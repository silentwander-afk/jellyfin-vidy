# Test report - Vidy for Jellyfin 0.1.0

Rule used: a test is marked **PASS** only if it was actually executed. Nothing below has been executed except the
items marked "checked here". Everything else is **NOT RUN**.

## Checked in the authoring sandbox

| Check | Result |
|---|---|
| `vidy.js` parses (`node --check`) | PASS |
| Dashboard page inline script parses | PASS |
| Bracket balance of all 20 C# files (own scanner) | PASS - this is NOT a compile |
| C# compile | **NOT RUN** (no .NET SDK; NuGet unreachable) |
| xUnit tests | **NOT RUN** (same reason) |

## Automated tests (written, will run in GitHub Actions)

The numbers come from `dotnet test`'s result file in the *test-results* artifact; paste it here once you have it.

| Area | What is covered | Status |
|---|---|---|
| TMDB (`TmdbTests`) | ranking, de-dup, type toggles, limit, year handling and fallback, missing key, invalid key (key never in messages), 429 retry, network failure, caching, v3 vs v4 auth, TV/season parsing with specials and gaps | NOT RUN |
| Vidy (`VidyTests`) | documented routes and flags, URL allow-listing, exact `allow` string, origin validation, malformed / oversized / wrong-type messages, settings validation | NOT RUN |
| Search (`SearchTests`) | year splitting, accent/case-insensitive matching, scoring order, Levenshtein | NOT RUN |
| History (`HistoryTests`) | survives restart (new store instance), per-user separation, threshold, no watched-on-open, validation, path-traversal, corrupt file, cap, concurrency, clear | NOT RUN |
| Playback (`PlaybackTests`) | next episode with gaps, empty seasons, season transitions, specials, non-1/1 start; Continue Watching resume/next/dedupe/limit/failure warnings | NOT RUN |

## Manual tests required (none performed)

Every scenario in the project spec needs a real Jellyfin 12.4 server, a TMDB key and browsers. All are **NOT RUN**:
plugin loads on 12.4; Dashboard saves and persists across restart; TMDB connection test; movie + TV search on Jellyfin's
search page and via the Vidy button; details pages; Vidy playback (movie, episode); pause/resume; refresh and resume;
unavailable player; episode complete -> next episode; season transition; disabling auto-next; history after Jellyfin restart;
two users with separate history; Continue Watching; Chrome, Firefox, Edge, mobile Chrome; fullscreen; autoplay restrictions;
no network; invalid key.

## Unverified assumptions most likely to need fixing

1. `Jellyfin.Controller` / `Jellyfin.Model` **12.4.0** exist on NuGet and the used APIs (`BasePlugin`, `IHasWebPages`,
   `IPluginServiceRegistrator`, `IApplicationPaths.WebPath`) are unchanged in 12.x.
2. Authorization policy names `DefaultAuthorization` and `RequiresElevation`, and the `Jellyfin-UserId` claim, are taken from
   Jellyfin 10.x and not re-verified for 12.4.
3. Jellyfin Web 12.x selectors used for the search-page section (`.searchResults`, `.libraryPage`) and home screen
   (`.homeSectionsContainer`), and `window.ApiClient` (`accessToken()`, `getUrl()`).
4. Vidy's actual `PLAYER_EVENT` payload: only `event`, `currentTime`, `duration` are documented; the page's own example
   shows `payload.event`/`payload.currentTime` but the "..." means other fields may exist.
5. Whether Vidy's page loads inside Jellyfin's CSP (a `frame-src` restriction would block the iframe).
6. The TMDB rate limit; sources disagree and the official page could not be fetched. The client limits itself to 4 concurrent
   requests, retries a single 429 once, and caches.

## Known limitations (by design)

Jellyfin Web only (no native apps); Vidy's in-player episode selector is not tracked; no anime route (spec was movies/TV);
subtitles/DRM/streams are entirely Vidy's.
