# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- **TV episode recommendations now have artwork.** Jellyfin gives episodes their own image provider, `EpisodeLocalImageProvider`, and it reads only files named after the episode file itself: `<episode filename>.<ext>` or `<episode filename>-thumb.<ext>`. The plugin wrote a bare video symlink into each season folder and nothing beside it, so an episode tile had no image it could possibly find — a series' or season's poster is never consulted for an episode. Frame extraction could not cover the gap either: it comes from `VideoImageProvider`, which is an `IDynamicImageProvider` rather than a local one, so the empty `ImageFetchers` list that keeps recommendation libraries offline switches it off. Each episode's own image is now linked beside its symlink.

  This was never a Jellyfin 12 regression. The same gap is present in the upstream plugin and predates the 12 port, which changed no C# at all; what changed is that per-user TV libraries now populate and get scanned, so the missing artwork became visible.

  Measured on the test server (300 shows of 12 episodes, 1,200 recommendation episodes), comparing the published v0.9.3 against this build: **0 of 1,200 episodes had an image before, 1,200 of 1,200 after.**

- **Links to deleted files are now actually removed, and the removal is reported.** The dangling-link sweep asked whether the *link* existed rather than whether its target did, and `File.Exists` follows a symlink and still reports a dangling one as present on .NET 10, so the check never fired once and a link to a deleted source file was never cleaned up. The result was discarded as well, and since only users whose folders changed are rescanned, a folder that had lost a file was left behind with Jellyfin still serving a row that pointed at it. The sweep now tests the link's target and reports what it removed, so that user's library is rescanned.

  A repeat refresh still takes 6 seconds and still skips the libraries that did not change, so this costs nothing.

- **Movie recommendations no longer keep the raw folder name and no poster.** Only users whose folders changed are rescanned, but unchanged files are not the same as files Jellyfin has read. An item created by a library validation pass is never metadata-refreshed — `Folder.ValidateSubFolders` validates children with `refreshChildMetadata: false` — so it keeps the name `MovieResolver` gives a movie in its own folder, the literal folder name `The Dark Knight (2008) [tmdbid-155]`, and has no image, because the NFO reader and the local image provider never ran for it. Nothing rewrites those files afterwards, so that user never looked changed again and was never scanned again: the entry stayed wrong permanently while its files on disk were perfectly correct. A library is now also scanned when it still holds items that have never been refreshed, so the next refresh repairs them and no user can get stuck this way again.

  Diagnosed on the 49-user test server. Of 1,225 recommendation movies, the 625 with `DateLastRefreshed` unset were exactly the 625 showing a folder-name title and exactly the 625 with no poster; the 600 that had been refreshed had neither symptom, and had been scanned that same day. The split is per user and total — 25 users at 25 of 25 broken, 24 users at 0 of 25 — because a scan covers a whole library at once, which is why no individual film was ever cursed. It was never about `IsInMixedFolder`, which is 0 for every recommendation movie, broken and healthy alike.

## [0.9.3] - Unreleased

### Fixed

- **A second refresh no longer costs more than the first.** The per-user loop asked the database one question per candidate series per user — "has this user watched any episode of this show?" — about 15,000 queries per refresh on a 49-user server, and every one of them grew slower as the plugin's own recommendation copies accumulated in the same database. It now asks once per user and answers the rest in memory, scoped to the real libraries. The same per-series pattern in the profile builder, which looked up each series' most recently watched episode, was replaced the same way.

  Measured on a 49-user test server (1,200 movies, 300 shows of 12 episodes, 18,850 recommendation items after the first run), running two refreshes with no activity between them:

  | | 0.9.2 | 0.9.3 |
  |---|---|---|
  | Generation phase, refresh #1 | 247s | 33s |
  | Generation phase, refresh #2 | 1,126s | 49s |
  | Refresh #2, total | 1,183s | 102s |

### Known issues

- A *first* refresh is still dominated by Jellyfin's library scan of the newly written recommendations — about 15 minutes for 100 libraries on the test server. This release does not change that. What it changes is the cost of a repeat refresh.
- Posters are missing for some users' movie recommendations and for all TV episode recommendations, and the affected entries show the raw folder name instead of the clean title.

## [0.9.2] - 2026-09-16

Supersedes the 0.9.1 pre-release, which shipped everything below but left refreshes so expensive that a second run on a 49-user server made no visible progress for half an hour. 0.9.1 in turn superseded 0.9.0, which left watch status stuck and recommendation libraries empty until a manual scan.

### Added

- **Automatic setup.** Each user gets their own "Recommended Movies (name)" and "Recommended Shows (name)" libraries, created at startup and when users are added, and removed when users are deleted. Libraries created by hand for 0.7 and earlier are adopted.
- **Automatic library access.** Each user's Library Access is set so they see only their own recommendation libraries. Users on "access all libraries" are switched to an explicit list of every library; libraries added later are added for them, and turning "access all libraries" back on is converted again straight away. If anything fails, recommendation libraries stay hidden rather than becoming visible to other users.

### Changed

- **Recommendation libraries use only local metadata:** no online lookups, no metadata saving, no chapter or trickplay image extraction, and no real-time monitoring. The plugin writes title, plot, genres, ratings, studios and dates into each recommendation's NFO.
- **Refreshes are incremental.** Unchanged recommendations are left in place and NFOs are only rewritten when their content changes, so Jellyfin no longer re-reads every recommendation on every refresh. Links whose source file was deleted are removed.
- The refresh task also runs at startup, and scans the recommendation libraries itself when it finishes; no manual scan is needed.

### Fixed

- **A long refresh no longer looks like a hung one.** The task reported 5% for the entire per-user loop, so a run still working and a run that had died were indistinguishable; 0.9.1 sat at a frozen percentage for 20–31 minutes. It now reports progress as each user finishes.
- **Library queries no longer count the plugin's own output.** Recommendations are real items in the same database as the originals, so the engine's queries had begun returning them alongside the originals. Queries are now scoped to the folders the real media lives in, the per-user access check asks for item ids instead of whole items, and each user's watch data is read in one batch instead of a lookup per item. This did **not** make a refresh cheaper overall: on a 49-user server the second refresh still took longer than the first. See 0.9.3.
- **Watch status reaches the real item immediately again.** The plugin looked the real item up by path, which builds a fresh copy, while Jellyfin serves requests from its cached copy and reads played state from the copy's own user data. Marking a recommendation played updated the database but the server kept reporting the real item as unwatched until it restarted.
- **Recommendation libraries no longer stay empty on a fresh install.** Jellyfin registers a library's folders when the library is created, when they are still empty, so nothing was indexed until someone ran a full library scan by hand. The plugin now re-registers them before scanning.
- **Refreshes no longer get slower as users are added.** Every refresh scanned every user's libraries; with 49 users a second refresh took over half an hour and stalled. Only users whose recommendations changed are scanned, and nothing is scanned when nothing changed.
- **Startup no longer blocks the server.** Library setup and the play-status sync run in the background after Jellyfin has started; the sync held up startup for ~2.5 minutes on a 50-user server.
- "Generating recommendations" was logged twice per refresh.

### Upgrade Notes

- Supersedes the withdrawn 0.8.0 pre-release. Its shared libraries and the `localrecs-…` blocked tags it added to users are removed automatically.

## [0.7.0] - 2026-09-15

### Changed

- **Jellyfin 12 support**: retargeted to .NET 10 and built against Jellyfin 12.0.0 (ABI `12.0.0.0`). No code changes were needed; the build also runs against the 12.1 assemblies with the full test suite passing.

### Upgrade Notes

- **Requires Jellyfin 12.0 or newer.** Plugins built for 10.11 do not load on 12, and this build does not load on 10.11. Servers on 10.11.9+ should stay on 0.6.1.
- After upgrading Jellyfin to 12, run a full library scan (Jellyfin requires one) before refreshing recommendations.

## [0.6.1] - 2026-05-30

### Fixed

- **Jellyfin 10.11.9+ compatibility (#17, fixes #21)**: the recommendation refresh, benchmark, setup, and play-status sync tasks no longer crash with `MissingMethodException: IUserManager.get_Users()`. Jellyfin 10.11.9 removed the `IUserManager.Users` property as part of its EFCore refactor ([jellyfin#15368](https://github.com/jellyfin/jellyfin/pull/15368)); all six call sites now use `GetUsers()`. **This release requires Jellyfin 10.11.9 or newer** (see Upgrade Notes).
- **TV series recency (#19)**: a series is now weighted by the date of its most recently watched episode. Previously every watched series was treated as watched *today* — Jellyfin never sets `LastPlayedDate` on the series item itself — which inflated TV influence on the taste profile regardless of when it was actually watched.
- **Config page numeric fields (#22)**: numeric settings saved as `0` no longer silently revert to their hardcoded defaults on the next page load (JavaScript `||` treated `0` as falsy).

### Changed

- **Recent-watch emphasis replaces rewatch boost (#20)**: the play-count–based rewatch boost is replaced by a recency-driven `decay²` amplification (`weight = decay × (1 + RecentWatchBoost × decay)`). Jellyfin's `PlayCount` increments on every stop/start of a stream and is near-useless at the series level, so recency is now used as the preference-strength proxy (a genuine re-watch resets recency anyway). The `RewatchBoost` setting (default 1.5) is replaced by **`RecentWatchBoost`** (default 1.0); the old value is dropped on first save. Set `RecentWatchBoost = 0` to disable the emphasis entirely.

### Internal

- Removed the now-unused `WatchRecord.PlayCount` field (#25) and added regression tests covering the TV-series recency path (#24).

### Upgrade Notes

- **Requires Jellyfin 10.11.9.0 or newer.** This release targets ABI `10.11.9.0` and is **not** compatible with 10.11.0–10.11.8 (the `GetUsers()` API does not exist there). Jellyfin's plugin catalogue will only offer this update to servers running 10.11.9+; servers on older 10.11.x builds should remain on 0.6.0 until they update Jellyfin.
- Recommendation ranking will shift on upgrade: the move to recency-based emphasis (and the series-recency fix) changes how watch history is weighted. Recommendations regenerate on the next scheduled refresh.

### Credits

- Huge thanks to [@PascalGodin](https://github.com/PascalGodin), who contributed the bulk of this release — the 10.11.9 compatibility fix (#17), the TV-series recency fix (#19), the recent-watch emphasis redesign (#20), and the config-page zero-value fix (#22).

## [0.6.0] - 2026-04-15

### Changed

- **Virtual libraries now use filesystem symlinks instead of `.strm` files (#13)**. Fixes transcoded playback on Jellyfin 10.11.7+, which silently rejects local paths in `.strm` files as part of security advisory [GHSA-j2hf-x4q5-47j3](https://github.com/jellyfin/jellyfin/security/advisories/GHSA-j2hf-x4q5-47j3). Symlinks have the source file's real extension, so Jellyfin's media pipeline treats them as regular media — transcoding, probing, and artwork discovery all work natively.
- **Artwork is now symlinked from the source folder** (`poster.jpg`, `fanart.jpg`, etc.) instead of being copied. Custom artwork on source items propagates automatically.
- **Trailer discovery delegated to Jellyfin.** Plugin symlinks trailer files by name; Jellyfin's scanner handles the rest.

### Removed

- **`ImageSyncService`** and its associated configuration (`EnableImageSync`, `SyncBackdrops`). Symlinked artwork supersedes the copy-based approach.
- **Custom trailer scanning logic** (~65 lines) and the video-extension heuristic.

### Fixed

- **`tvshow.nfo` written for series folders** so Jellyfin's scanner reliably identifies them as Series instead of rendering individual episodes as standalone items.
- **Series poster rendering**: artwork now resolved via `BaseItem.GetImagePath` (which works for metadata-cache storage) rather than scanning the source folder.
- **Additional artwork aliases** (`folder.jpg`, `backdrop.jpg`) symlinked alongside `poster.jpg`/`fanart.jpg` to suppress Jellyfin core warnings for conventional filenames it probes.
- **Reduced log noise**: per-user/per-item progress messages demoted from INFO to DEBUG. Refresh-level summary lines remain at INFO.

### Upgrade Notes

- **Linux / Docker-on-Linux:** No action required. Virtual libraries regenerate on the next scheduled refresh.
- **Windows hosts:** Jellyfin must run as Administrator **or** Windows Developer Mode must be enabled (Settings → Privacy & security → For developers). Without one of these, the plugin logs `Access denied creating symlink` and the virtual libraries remain empty. See README Troubleshooting section.
- Existing `.strm`-based virtual libraries are cleared and rebuilt on the next recommendation refresh — no manual migration needed.

## [0.5.3] - 2026-03-23

### Fixed

- **User Library Access Filtering (#10)**: Recommendations now respect per-user library access. Items from libraries a user cannot access (including disabled libraries) are excluded from both personalized and cold-start recommendation paths.

## [0.5.2] - 2026-02-08

### Fixed

- **Playback Freeze from Recommendations (#8)**: Playing items from recommendation libraries no longer freezes playback. The plugin was causing a storm of database writes every ~10 seconds during active playback by syncing every position update to the source library.

### Changed

- **Deferred Removal**: Virtual library items are never removed from event handlers. Watched items remain in recommendation libraries until the next scheduled refresh cleans them up naturally.
- **SaveReason Filtering**: Only meaningful events (PlaybackFinished, TogglePlayed, UpdateUserRating) trigger play status sync. PlaybackStart and PlaybackProgress events are ignored entirely.
- **Code Cleanup**: Removed dead removal code (RemoveVirtualLibraryItem, FindSeriesFolderForItem, TriggerLibraryScan, active session tracking).

### Known Issues

- Partially watched recommendations may appear twice in "Continue Watching" / "Next Up" (once for the .strm item, once for the source). Resolves on next recommendation refresh.

## [0.4.0] - 2025-12-28

### Added

- **Decade-Based Temporal Similarity**: Recommendations now consider content from similar time periods using categorical decade grouping (1980s, 1990s, etc.) instead of continuous year normalization
  - Improves temporal relevance alongside existing genre/actor/director similarity features
  - Tested with production data: ~12 decades extracted from 970 items
  - Observable impact: 24% of movie recommendations changed, 8% of TV recommendations changed

### Fixed

- **In-Progress Series Filtering**: TV series with unwatched episodes no longer appear in recommendations (prevents recommending shows you're currently watching)

## [0.3.0] - 2025-12-27

### Fixed

- **Series Filtering**: Fully watched series no longer appear in recommendations. Previously relied on unreliable `userData.Played` flag; now queries for unwatched episodes directly
- **Play Status Sync**: Virtual library items now correctly reflect source library watch status when scanned by Jellyfin

### Added

- **Play Status Sync on Item Add**: When Jellyfin scans new virtual library items, their play status is automatically synced from the source library via `ItemAdded` event
- **Play Status Sync on Startup**: Existing virtual library items sync play status from source library when plugin initializes
- **Rating Proximity Scoring**: Optional feature to boost recommendations with similar community/critic ratings to user's watched content

### Changed

- Refactored `PlayStatusSyncService` to reduce code duplication with extracted helper methods
- Reduced debug logging noise in production for cleaner logs
- Removed ineffective sync call from recommendation refresh task (items aren't indexed yet when it runs)

### Removed

- **NFO File Generation**: Removed NFO metadata files as Jellyfin doesn't read NFO files for .strm content (metadata comes from the source library item)

## [0.2.1] - 2025-12-26

### Fixed

- **NFO Encoding**: Fixed XML encoding from UTF-16 to UTF-8 so Jellyfin properly reads metadata (runtime, etc.)
- **Cast & Crew**: NFO files now include actors, directors, and writers from source media
- **Stream Details**: NFO files now include video/audio/subtitle stream information for proper stream selector display

### Added

- **FileInfo Section**: NFO files now contain `<fileinfo><streamdetails>` with codec, bitrate, resolution, framerate, language, and channel information

## [0.2.0] - 2025-12-26

### Added

- **NFO Metadata Support**: Virtual library items now include NFO files with full metadata (runtime, ratings, genres, studios, tags, provider IDs)
- **Local Trailer Support**: Trailers from source media are now linked in virtual libraries using `-trailer.strm` files
- **Movie Folder Structure**: Movies now use proper folder structure with NFO files for better metadata support

### Fixed

- Copy buttons on setup page now work with fallback clipboard support for broader browser compatibility
- Manifest now correctly references ZIP file instead of raw DLL

### Changed

- Improved README with detailed installation instructions and algorithm documentation
- Simplified bug report template

## [0.1.0] - 2025-12-26

### Initial Beta Release

Privacy-first personalized recommendations for Jellyfin based on local watch history.

#### Features

- **Per-User Personalization**: Each user receives recommendations tailored to their viewing history
- **Content-Based Filtering**: Uses TF-IDF embeddings and cosine similarity to find similar content
- **Virtual Library Integration**: Recommendations appear as dedicated libraries accessible from all Jellyfin clients (web, mobile, Roku, etc.)
- **Privacy-First Design**: All processing happens locally on your server with zero external dependencies or tracking
- **Configurable Weighting**:
  - Favorite boost (default 2.0x)
  - Rewatch boost (default 1.5x)
  - Recency decay with configurable half-life (default 365 days)
- **Smart Filtering**:
  - Abandoned series exclusion (configurable threshold, default 90 days)
  - Minimum watch history requirement (default 3 items)
  - Excludes already-watched content
- **Flexible Updates**:
  - Daily scheduled task (configurable time)
  - Manual refresh available anytime
- **Performance Optimized**: Handles libraries of 2,000+ items efficiently with vocabulary limiting and parallel processing

#### Technical Details

- **Target**: Jellyfin Server 10.11.5+
- **Runtime**: .NET 9.0
- **Target ABI**: 10.11.0.0
- **Architecture**: Content-based filtering with TF-IDF, cosine similarity, and weighted user profiles
- **Storage**: Per-user .strm files in plugin data directory

#### Supported Metadata

- Genres
- Actors (top 500 by frequency)
- Directors
- Tags (top 500 by frequency)
- Content Ratings
- Release Years

#### Known Limitations

- Requires manual one-time library setup per user (5-10 minutes)
- Cold start: Users with fewer than 3 watched items receive popular content recommendations
- No collaborative filtering (recommendations based solely on individual user's history)
- Series recommendations based on series-level metadata only (not individual episodes)

[0.4.0]: https://github.com/rdpharr/jellyfin-plugin-localrecs/releases/tag/v0.4.0
[0.3.0]: https://github.com/rdpharr/jellyfin-plugin-localrecs/releases/tag/v0.3.0
[0.2.1]: https://github.com/rdpharr/jellyfin-plugin-localrecs/releases/tag/v0.2.1
[0.2.0]: https://github.com/rdpharr/jellyfin-plugin-localrecs/releases/tag/v0.2.0
[0.1.0]: https://github.com/rdpharr/jellyfin-plugin-localrecs/releases/tag/v0.1.0
