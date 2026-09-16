# Jellyfin Local Recommendations Plug-in

[![License: GPLv3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Tests](https://github.com/rdpharr/jellyfin-plugin-localrecs/actions/workflows/tests.yml/badge.svg)](https://github.com/rdpharr/jellyfin-plugin-localrecs/actions/workflows/tests.yml)

Privacy-first personalized recommendations for Jellyfin based entirely on local watch history and metadata similarity. No cloud services, no tracking. Works on all Jellyfin clients (even TVs).

> [!NOTE]
> This is the Jellyfin 12 fork of [rdpharr/jellyfin-plugin-localrecs](https://github.com/rdpharr/jellyfin-plugin-localrecs), which is not being updated for Jellyfin 12.

Please report any issues or feedback on [GitHub Issues](https://github.com/rdpharr/jellyfin-plugin-localrecs/issues).

> ⚠️ **Windows hosts:** As of v0.6.0, this plugin creates filesystem symlinks to expose recommendations. On Windows, symlink creation requires either **running Jellyfin as Administrator** or **enabling Windows Developer Mode** (Settings → Privacy & security → For developers → Developer Mode). Without one of these, recommendation refreshes will log "Access denied creating symlink" and the virtual libraries will be empty. Docker-on-Linux and native Linux deployments are unaffected. See [Troubleshooting](#troubleshooting) below.

## Features

- **Per-user personalization** - Tailored recommendations for each user's viewing history
- **Content-based filtering** - TF-IDF embeddings with cosine similarity matching
- **Temporal similarity** - Decade-based grouping finds content from similar time periods
- **Virtual library integration** - Works on all Jellyfin clients (web, mobile, Roku, etc.)
- **Play status sync** - Watch state syncs from recommendations back to your real library
- **Privacy-first** - All processing happens locally on your server
- **Performance optimized** - Handles 2,000+ item libraries efficiently

## Requirements

- **Jellyfin Server:** 12.0+ (tested against 12.0 and 12.1; use plugin 0.6.1 for 10.11.9+)
- **.NET Runtime:** 10.0
- **Target ABI:** 12.0.0.0

## Installation

1. **Add plugin repository:**  
   Dashboard → Plugins → Repositories → Add  
   `https://raw.githubusercontent.com/crystal-coding-time/jellyfin-plugin-localrecs/main/manifest.json`

2. **Install plugin:**  
   Dashboard → Plugins → Catalog → Install "Local Recommendations"

3. **Restart Jellyfin server**

That's it: there is no setup (see below).

## Setup

None. When Jellyfin starts, and whenever a user is added, the plugin:

- **Creates two libraries per user**, **Recommended Movies (name)** and **Recommended Shows (name)**, which appear on that user's home screen as tiles and "Latest" rows.
- **Sets each user's Library Access** so they see their own recommendation libraries and nobody else's. Users who had "access all libraries" are switched to an explicit list of every library. Libraries you add later are added to their list automatically, and a library you remove from a user stays removed. To give someone everything again, tick "access all libraries": the plugin turns it straight back into a full list without other users' recommendations.
- **Fills the libraries** on the next **Refresh Local Recommendations** run (at startup and daily at 4 AM, or Dashboard → Scheduled Tasks → Run).

Recommendation libraries use only the metadata and artwork the plugin copies from your real libraries: no online lookups, nothing written into your media folders, and no real-time folder monitoring. Deleting a user removes their recommendation libraries.

> **Upgrading from 0.7 or earlier:** libraries you created by hand for each user are adopted, not recreated.
>
> **Upgrading from the 0.8.0 pre-release:** its shared "Recommended Movies" / "Recommended Shows" libraries and the `localrecs-…` blocked tags it added to users are removed automatically.
>
> **Uninstalling:** users stay on explicit library lists. Tick "access all libraries" for anyone who should have everything again, and delete the recommendation libraries.

## Configuration

Access via: **Dashboard → Plugins → Local Recommendations → Settings**

### Key Settings

**Recommendation Counts**
- Movies and TV shows to recommend per user (default: 25 each)
- Minimum watched items for personalization (default: 3)

**Filtering**
- Exclude abandoned TV series (default: enabled, 90 days threshold)

**Weighting Factors**
- Favorite boost: 2.0x (configurable)
- Recent watch emphasis: 1.0 (configurable) — amplifies recently watched items using decay²
- Recency decay half-life: 365 days (configurable)

**Optional Features**
- Rating proximity scoring (boost items with similar ratings)
- Decade-based temporal similarity (finds content from similar eras)

**Performance**
- Vocabulary limits for actors/tags (default: 500 each) — see note below
- Parallel processing options

**Vocabulary size** controls how many distinct actors, directors, and tags are included in the TF-IDF model. A higher value (e.g. 1000) captures more niche contributors and gives richer signals for large, varied libraries. A lower value (e.g. 200) is faster and uses less memory but may miss less-common cast members. The default of 500 is a good starting point for most libraries; raise it if recommendations feel too genre-driven and ignore specific actors you watch often.

**Recency decay** controls how much your recent watch history influences recommendations relative to older watches. It is expressed as a half-life in days: a value of 365 means a film watched a year ago contributes half as much to your taste profile as one watched today. Lower values (e.g. 90) make recommendations react quickly to recent binges; higher values (e.g. 730) give a more stable, long-term taste profile.

**Update Schedule**
- Default: Daily at 4:00 AM
- Customize in Dashboard → Scheduled Tasks
- Manual refresh available anytime

## How It Works

### Algorithm

**Content-based filtering** using TF-IDF embeddings and cosine similarity:

1. **Feature extraction** - Genres, actors, directors, tags, decades, ratings
2. **TF-IDF embeddings** - Numerical vectors for each item (~1200-1500 dimensions)
3. **User profiles** - Aggregated taste vector from weighted watch history
4. **Similarity scoring** - Cosine similarity between user profile and unwatched items
5. **Ranking** - Top N items sorted by similarity score

**Weighting factors:**
- Favorites (2x boost)
- Recent watch emphasis (decay² amplification, default 1.0)
- Recency decay (365-day half-life)
- Decade similarity (items from similar time periods)
- Optional rating proximity (items with similar ratings)

### Virtual Libraries

Recommendations appear as separate libraries for each user:

- Plugin creates filesystem symlinks pointing to original media files (with matching artwork symlinks)
- Admin creates Jellyfin libraries pointing to plugin directories (one-time setup)
- Each user gets Movies and TV libraries with personalized recommendations
- Play status sync: Watch state on recommendation items is synced back to the source library
- Watched items are cleaned up at the next scheduled recommendation refresh

### Privacy

**100% local processing:**
- No external services or cloud dependencies
- No tracking or telemetry
- Only uses data already in your Jellyfin database
- All computation happens on your server

## Known Limitations

- **Duplicate "Continue Watching" / "Next Up":** Partially watched recommendations appear twice — once for the virtual (symlinked) item and once for the source media file. This resolves on the next recommendation refresh, or you can manually trigger a refresh from Scheduled Tasks.
- **Metadata display:** Virtual library items may not show full text metadata (runtime, ratings, cast) in the UI because Jellyfin treats them as items in a separate library. Posters, backdrops, and playback work normally.
- **Manual setup required:** Admin must manually create libraries and set permissions (Jellyfin API limitation)
- **Library scanning:** Manually scan recommendation libraries after refresh to see updates
- **Windows symlink permissions:** Requires Administrator or Developer Mode — see Troubleshooting.

## Troubleshooting

### Recommendation libraries are empty after refresh (Windows)

If the refresh task completes without errors but the recommendation libraries are empty, check
the Jellyfin log for `Access denied creating symlink`. This means the Jellyfin process lacks
permission to create symbolic links, which is required on Windows.

**Fix (pick one):**

1. **Enable Windows Developer Mode** (recommended, no elevation needed):
   Settings → Privacy & security → For developers → turn on **Developer Mode**, then restart
   the Jellyfin service.
2. **Run Jellyfin as Administrator.** Right-click the service or launcher → Run as administrator.
   If Jellyfin runs as a Windows service, configure the service's logon account to a user with
   `SeCreateSymbolicLinkPrivilege` granted, or to a local administrator.

After either change, run **Dashboard → Scheduled Tasks → Refresh Local Recommendations**.

### New files in your real libraries stop being detected (Linux)

Jellyfin restarts its folder watchers every time a library is created, so setting up many users at once
can exhaust the kernel's inotify limits. The log shows `Error watching path` or `The configured user
limit on the number of inotify watches has been reached`, and new files added to your **real** libraries
are no longer picked up automatically (manual scans still work). The plugin's own libraries never use
real-time monitoring, so this is a one-off cost of creating the libraries.

**Fix:** restart Jellyfin once after the libraries are created, and raise the limits:

```bash
echo fs.inotify.max_user_watches=524288 | sudo tee /etc/sysctl.d/40-max-user-watches.conf
echo fs.inotify.max_user_instances=512 | sudo tee /etc/sysctl.d/50-jellyfin-server.conf
sudo sysctl --system
```

On Docker, set these on the host, not inside the container.

### Transcoded playback fails on Jellyfin 10.11.7+ (plugin versions ≤0.5.3)

Upgrade to **v0.6.0 or later**. Jellyfin 10.11.7 shipped a security fix
([GHSA-j2hf-x4q5-47j3](https://github.com/jellyfin/jellyfin/security/advisories/GHSA-j2hf-x4q5-47j3))
that broke the old `.strm`-based approach. v0.6.0 switches to symlinks, which bypass the
restricted `.strm` parser entirely.

## Building from Source

**Prerequisites:** .NET 10.0 SDK, Git

```bash
git clone https://github.com/rdpharr/jellyfin-plugin-localrecs.git
cd jellyfin-plugin-localrecs

# Build (uses dotnet-helper.sh wrapper)
bash dotnet-helper.sh build

# Run tests
bash dotnet-helper.sh test

# Output: Jellyfin.Plugin.LocalRecs/bin/Debug/net10.0/
```

**Windows:** Use Git Bash or WSL to run the helper script.

## Contributing

Contributions to the current Jellyfin version are welcome. See [DESIGN.md](DESIGN.md) for technical details and architecture. For Jellyfin 12 support, please create a fork; maintained forks can be submitted for inclusion in the notice above.

## Support

- **Issues:** [GitHub Issues](https://github.com/rdpharr/jellyfin-plugin-localrecs/issues)
- **Discussions:** [GitHub Discussions](https://github.com/rdpharr/jellyfin-plugin-localrecs/discussions)
- **Documentation:** [DESIGN.md](DESIGN.md)

## License

GNU General Public License v3.0 - see [LICENSE.txt](LICENSE.txt)
