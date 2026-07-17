using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Web.Responses;

// PICS can't tell us whether a playtest's signup is currently open, so more than half
// of the playtest requests the plugin used to issue came back as 401 ("no playtest for
// appID") and got re-issued on every PICS change. To stop that storm without a TTL
// (which would risk missing a playtest that re-opens several times over a weekend),
// we periodically fetch Steam's own "Playtests" store search (category1=989). That
// category only ever lists apps whose playtest signup is currently visible, so list
// membership is the re-trigger: a playtest that leaves the list (dev hid the signup)
// and later returns (re-opened) is requested again, every time.
//
// The live set is the source of truth for "joinable right now". PICS is kept only to
// (a) resolve playtest_type for the limited/unlimited filter and (b) wake us early via
// RequestRefresh when a Beta app changes. See the plan in
// ~/.claude/plans/snappy-weaving-aurora.md for the full rationale.

namespace FreePackages {
	internal static class PlaytestCatalog {
		private const int PageSize = 50;
		private const int MaxPages = 200; // 200 * 50 = 10,000 — a safety bound well above the ~2,800 observed
		private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(15);
		private static readonly TimeSpan RefreshFrequency = TimeSpan.FromMinutes(15);
		private static readonly TimeSpan EarlyWakeDebounce = TimeSpan.FromSeconds(60);

		// data-ds-appid on each search result is the BASE game appID — the same value the
		// plugin posts to /ajaxrequestplaytestaccess/<id> and stores as app.Parent.ID.
		private static readonly Regex AppIDRegex = new("data-ds-appid=\"(\\d+)\"", RegexOptions.CultureInvariant | RegexOptions.Compiled);

		// The URL form verified on 2026-07-17: anonymous, paginated via start=, count=,
		// category1=989 (Playtests). `infinite=1&json=1` returns {success,total_count,results_html}.
		private const string SearchUrl = "https://store.steampowered.com/search/results/?query&start={0}&count={1}&dynamic_data=&sort_by=_ASC&snr=1_7_7_230_150&category1=989&infinite=1&json=1";

		private static HashSet<uint> LivePlaytests = new();
		private static readonly object LiveLock = new();
		internal static bool HasFetched; // True once at least one fetch has replaced the live set
		private static long LastRefreshRequestTicks; // For the early-wake debounce (DateTime.UtcNow.Ticks)

		private static readonly SemaphoreSlim RefreshSemaphore = new(1, 1);
		private static readonly Timer UpdateTimer = new(async _ => await DoUpdate().ConfigureAwait(false), null, Timeout.Infinite, Timeout.Infinite);

		internal static void Update() {
			UpdateTimer.Change(InitialDelay, RefreshFrequency);
		}

		// Wake the catalog immediately (debounced to ~60s) so a Beta-app PICS change is
		// reflected within a minute instead of waiting up to 15 minutes for the next cycle.
		internal static void RequestRefresh() {
			long now = DateTime.UtcNow.Ticks;
			long last = Interlocked.Read(ref LastRefreshRequestTicks);

			if (now - last < EarlyWakeDebounce.Ticks) {
				return;
			}

			Interlocked.Exchange(ref LastRefreshRequestTicks, now);
			UpdateTimer.Change(TimeSpan.Zero, RefreshFrequency);
		}

		internal static bool IsLive(uint baseAppID) {
			lock (LiveLock) {
				return LivePlaytests.Contains(baseAppID);
			}
		}

		internal static HashSet<uint> GetLiveSet() {
			lock (LiveLock) {
				return new HashSet<uint>(LivePlaytests);
			}
		}

		// Parse the BASE appIDs out of one page of results_html. Exposed for unit tests
		// (the fetch itself hits the network and is not covered there). Returns null
		// when the page is unusable so the caller can abort the whole fetch cleanly.
		internal static HashSet<uint>? ParsePage(SearchResults? page) {
			if (page == null || page.Success != 1) {
				return null;
			}

			return ParseAppIDs(page.ResultsHtml);
		}

		internal static HashSet<uint> ParseAppIDs(string resultsHtml) {
			HashSet<uint> appIDs = new();

			foreach (Match match in AppIDRegex.Matches(resultsHtml)) {
				if (uint.TryParse(match.Groups[1].Value, out uint appID) && appID > 0) {
					appIDs.Add(appID);
				}
			}

			return appIDs;
		}

		// A fetch is only "complete" (and therefore safe to prune from) when we actually
		// parsed a set close to what Steam reports. Parsing far fewer entries than
		// total_count usually means the store markup changed and we scraped nothing — in
		// that case we must NOT replace the live set or prune, otherwise a markup change
		// would wipe RequestedPlaytests and re-request the entire catalog.
		internal static bool IsFetchComplete(HashSet<uint> fetched, int totalCount) {
			if (fetched.Count == 0 || totalCount <= 0) {
				return false;
			}

			if (fetched.Count < totalCount / 2) {
				return false;
			}

			return true;
		}

		private static async Task DoUpdate() {
			ArgumentNullException.ThrowIfNull(ASF.WebBrowser);

			// Skip overlapping invocations (scheduled tick + early wake): the run already
			// in progress covers the latest state, so a queued second run adds nothing.
			if (!await RefreshSemaphore.WaitAsync(0).ConfigureAwait(false)) {
				return;
			}

			HashSet<uint> fetched = new();
			int totalCount = 0;
			bool failed = false;

			try {
				for (int page = 0; page < MaxPages; page++) {
					int start = page * PageSize;

					if (page > 0 && start >= totalCount) {
						break;
					}

					Uri request = new(string.Format(SearchUrl, start, PageSize));
					ObjectResponse<SearchResults>? response = await ASF.WebBrowser.UrlGetToJsonObject<SearchResults>(request).ConfigureAwait(false);

					HashSet<uint>? pageAppIDs = ParsePage(response?.Content);
					if (pageAppIDs == null) {
						int statusCode = response == null ? -1 : (int) response.StatusCode;
						ASF.ArchiLogger.LogGenericError($"PlaytestCatalog fetch failed at page {page} (start={start}, HTTP {statusCode})");

						failed = true;
						break;
					}

					if (page == 0) {
						totalCount = response!.Content!.TotalCount;

						if (totalCount <= 0) {
							ASF.ArchiLogger.LogGenericError("PlaytestCatalog fetch failed: total_count was 0");

							failed = true;
							break;
						}
					}

					int before = fetched.Count;
					fetched.UnionWith(pageAppIDs);

					// No new entries on a page means we've walked past the end of the list.
					if (fetched.Count == before) {
						break;
					}

					if (start + PageSize >= totalCount) {
						break;
					}
				}
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);

				failed = true;
			} finally {
				RefreshSemaphore.Release();
			}

			// A failed or incomplete fetch keeps the previous live set and MUST NOT prune.
			// See IsFetchComplete for the markup-change safety net.
			if (failed || !IsFetchComplete(fetched, totalCount)) {
				int previousCount;
				lock (LiveLock) {
					previousCount = LivePlaytests.Count;
				}

				ASF.ArchiLogger.LogGenericError($"PlaytestCatalog refresh did not complete cleanly (failed={failed}, fetched={fetched.Count}, total_count={totalCount}); keeping previous live set of {previousCount} playtest(s)");

				return;
			}

			HashSet<uint> snapshot;
			lock (LiveLock) {
				LivePlaytests = fetched;
				HasFetched = true;
				snapshot = new HashSet<uint>(fetched);
			}

			ASF.ArchiLogger.LogGenericInfo($"PlaytestCatalog fetched {snapshot.Count} live playtest(s) (total_count={totalCount})");

			// Fan out the prune + proactive enqueue off the timer thread.
			Utilities.InBackground(() => PackageHandler.OnPlaytestCatalogUpdated(snapshot));
		}

		// Mirrors the Json.cs pattern: private init + JsonConstructor so ASF's source-gen
		// serializer populates these from the anonymous search response.
		internal sealed class SearchResults {
			[JsonInclude]
			[JsonPropertyName("success")]
			[JsonRequired]
			internal int Success { get; private init; } = 0;

			[JsonInclude]
			[JsonPropertyName("total_count")]
			[JsonRequired]
			internal int TotalCount { get; private init; } = 0;

			[JsonInclude]
			[JsonPropertyName("results_html")]
			[JsonRequired]
			internal string ResultsHtml { get; private init; } = "";

			[JsonConstructor]
			internal SearchResults() { }

			// For unit tests: the private init setters are only reachable from within the
			// type, so tests build instances through this constructor instead of an
			// object initializer. The serializer uses the parameterless one above.
			internal SearchResults(int success, int totalCount, string resultsHtml) {
				Success = success;
				TotalCount = totalCount;
				ResultsHtml = resultsHtml;
			}
		}
	}
}