using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CityDwellers.Shared
{
    public sealed class RepositoryUpdateSnapshot
    {
        public string State = "checking";
        public string LatestRevision;
        public string Detail = "Checking GitHub";
        public DateTime? CheckedUtc;
    }

    public static class RepositoryUpdates
    {
        private const string SnapshotKey = "CITYDWELLERS_REPOSITORY_UPDATE";
        private const string ApiRoot = "https://api.github.com/repos/axlslak/citydwellers/";
        private static Timer _timer;
        private static int _checking;
        private static string _lastNotice;
        private static Action<string> _log;

        public static RepositoryUpdateSnapshot Read()
        {
            try
            {
                string json = Environment.GetEnvironmentVariable(SnapshotKey, EnvironmentVariableTarget.Process);
                return string.IsNullOrEmpty(json) ? new RepositoryUpdateSnapshot() :
                    JsonConvert.DeserializeObject<RepositoryUpdateSnapshot>(json) ?? new RepositoryUpdateSnapshot();
            }
            catch { return new RepositoryUpdateSnapshot { State = "unavailable", Detail = "Update status unavailable" }; }
        }

        // Start only in the host; thread-pool timer uses elapsed intervals, not wall-clock deadlines.
        public static void Start(Action<string> log)
        {
            if (_timer != null) return;
            _log = log;
            Publish(new RepositoryUpdateSnapshot());
            _timer = new Timer(CheckTimer, null, TimeSpan.Zero, TimeSpan.FromHours(1));
        }

        private static async void CheckTimer(object ignored)
        {
            if (Interlocked.Exchange(ref _checking, 1) != 0) return;
            var result = new RepositoryUpdateSnapshot();
            try
            {
                using (var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }))
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.MaxResponseContentBufferSize = 512 * 1024;
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("CityDwellers-VersionCheck/1.0");
                    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
                    JObject head = await GetJson(client, "git/ref/heads/master").ConfigureAwait(false);
                    string latest = (string)head["object"]?["sha"];
                    if (BuildIdentity.GitRevision(latest) != latest || string.IsNullOrEmpty(latest))
                        throw new InvalidOperationException("GitHub returned an invalid revision");
                    result.LatestRevision = latest;
                    string local = BuildIdentity.GitRevision(BuildIdentity.Revision);
                    if (local == null)
                    {
                        result.State = "uncomparable";
                        result.Detail = "Latest revision known; local build has no Git revision to compare";
                    }
                    else if (local == latest)
                    {
                        result.State = BuildIdentity.Revision.EndsWith("-modified", StringComparison.Ordinal) ? "modified" : "current";
                        result.Detail = result.State == "current" ? "At latest repository revision" : "Based on latest revision; local modifications";
                    }
                    else
                    {
                        JObject comparison = await GetJson(client, "compare/" + local + "..." + latest + "?per_page=1").ConfigureAwait(false);
                        string relationship = (string)comparison["status"];
                        switch (relationship)
                        {
                            case "ahead":
                                result.State = "outdated";
                                result.Detail = "Newer repository revision available";
                                break;
                            case "behind":
                                result.State = "ahead";
                                result.Detail = "Local revision is ahead of published master";
                                break;
                            case "diverged":
                                result.State = "diverged";
                                result.Detail = "Local and published history have diverged";
                                break;
                            default:
                                throw new InvalidOperationException("GitHub returned an unexpected comparison");
                        }
                        if (BuildIdentity.Revision.EndsWith("-modified", StringComparison.Ordinal))
                            result.Detail += "; local modifications";
                    }
                }
            }
            catch (Exception ex)
            {
                result.State = "unavailable";
                result.Detail = "Update check unavailable (" + ex.GetType().Name + "); retry in one hour";
                if (ex is HttpRequestException) result.Detail += ": " + ex.Message;
            }
            finally
            {
                result.CheckedUtc = DateTime.UtcNow;
                try { Publish(result); }
                catch { /* Diagnostics must not terminate or delay the runtime. */ }
                Interlocked.Exchange(ref _checking, 0);
            }
        }

        private static async Task<JObject> GetJson(HttpClient client, string path)
        {
            using (HttpResponseMessage response = await client.GetAsync(ApiRoot + path).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                return JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }

        private static void Publish(RepositoryUpdateSnapshot result)
        {
            Environment.SetEnvironmentVariable(SnapshotKey, JsonConvert.SerializeObject(result), EnvironmentVariableTarget.Process);
            string notice = result.State + "|" + result.LatestRevision + "|" + result.Detail;
            if (notice == _lastNotice) return;
            _lastNotice = notice;
            _log?.Invoke("VERSION " + result.Detail + "; running=" + BuildIdentity.Revision +
                (result.LatestRevision == null ? "" : "; latest=" + result.LatestRevision));
        }
    }
}
