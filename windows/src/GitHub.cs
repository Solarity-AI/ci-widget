// CI Widget for Windows - reading GitHub: tokens from gh, conditional requests (ETag), the API budget, and the collector
// that turns repos, runs and jobs into a Snapshot. Only GET requests: the widget never changes anything on GitHub.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CiWidget
{
    public sealed class Fetch
    {
        public int Status;               // HTTP status; 0 = no answer (network)
        public object Body;              // parsed JSON (the cached copy on a 304)
        public bool FromCache;           // a 304: GitHub does not count it against the rate limit
        public int Remaining = -1, Limit = -1;
        public DateTime ResetUtc = DateTime.MinValue;
        public string Error = "";
        public bool Ok { get { return Status == 200 && Body != null; } }   // a 200 whose JSON can't be read is a failure
        public string Why { get { return Status == 0 ? (Error.Length > 0 ? Error : "no answer") : Status.ToString(CultureInfo.InvariantCulture) + (Error.Length > 0 ? " " + Error : ""); } }
    }

    public interface IFetcher
    {
        Fetch Get(string path);          // path after the API root, e.g. "/repos/o/r/actions/runs?status=queued"
        string Prepare();                // gets a token; returns "" or why it can't
    }

    // HTTPS client for 1 account. Keeps the last body and ETag per URL, so an unchanged answer costs no rate limit.
    public sealed class GitHubFetcher : IFetcher
    {
        sealed class Cached { public string ETag; public object Body; }
        readonly AccountConfig acct;
        readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>();
        string token;
        DateTime tokenUtc = DateTime.MinValue;

        public GitHubFetcher(AccountConfig a) { acct = a; }

        static GitHubFetcher()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
        }

        public string Prepare()
        {
            if (token != null && (DateTime.UtcNow - tokenUtc).TotalMinutes < 30) return "";
            string why;
            string t = Tokens.Read(acct, out why);
            if (t == null) { token = null; return why; }
            token = t; tokenUtc = DateTime.UtcNow;
            return "";
        }

        public Fetch Get(string path)
        {
            Fetch f = new Fetch();
            string url = acct.Api + path;
            Cached c;
            cache.TryGetValue(url, out c);
            HttpWebResponse resp = null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = 15000; req.ReadWriteTimeout = 15000;
                req.UserAgent = "ci-widget";
                req.Accept = "application/vnd.github+json";
                req.Headers["X-GitHub-Api-Version"] = "2022-11-28";
                if (token != null) req.Headers["Authorization"] = "Bearer " + token;
                if (c != null && c.ETag != null) req.Headers["If-None-Match"] = c.ETag;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex) { resp = wex.Response as HttpWebResponse; if (resp == null) { f.Error = wex.Status.ToString(); return f; } }
                f.Status = (int)resp.StatusCode;
                ReadRate(resp, f);
                if (f.Status == 304 && c != null) { f.Body = c.Body; f.FromCache = true; f.Status = 200; return f; }
                string text;
                using (Stream s = resp.GetResponseStream())
                using (StreamReader r = new StreamReader(s, Encoding.UTF8)) text = r.ReadToEnd();
                if (f.Status == 200)
                {
                    try { f.Body = Json.Parse(text); }
                    catch { f.Error = "unreadable answer"; return f; }   // Ok stays false: never read as "nothing running"
                    string etag = resp.Headers["ETag"];
                    if (etag != null) { Cached n = new Cached(); n.ETag = etag; n.Body = f.Body; cache[url] = n; }
                    if (cache.Count > 4000) cache.Clear();   // repos come and go: never grow without end
                }
                else
                {
                    try { Dictionary<string, object> d = Json.Parse(text) as Dictionary<string, object>; f.Error = Json.Str(d, "message"); } catch { }
                    if (f.Status == 401) tokenUtc = DateTime.MinValue;   // read it again from gh next round; keep it for this one
                }
            }
            catch (Exception ex) { f.Error = ex.Message; }
            finally { if (resp != null) resp.Close(); }
            return f;
        }

        static void ReadRate(HttpWebResponse resp, Fetch f)
        {
            int n; long l;
            if (int.TryParse(resp.Headers["X-RateLimit-Remaining"], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) f.Remaining = n;
            if (int.TryParse(resp.Headers["X-RateLimit-Limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) f.Limit = n;
            if (long.TryParse(resp.Headers["X-RateLimit-Reset"], NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                f.ResetUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(l);
        }
    }

    // Tokens come from the GitHub CLI (gh auth token) or an environment variable, at run time, into memory only.
    public static class Tokens
    {
        public static string Read(AccountConfig a, out string why)
        {
            why = "";
            if (a.TokenEnv.Length > 0)
            {
                string v = Environment.GetEnvironmentVariable(a.TokenEnv);
                if (!string.IsNullOrEmpty(v)) return v.Trim();
                why = "environment variable " + a.TokenEnv + " is empty";
                return null;
            }
            string gh = GhPath();
            if (gh == null) { why = "GitHub CLI (gh) not found: install it and run gh auth login"; return null; }
            string host = "github.com";
            try { Uri u = new Uri(a.Api); if (u.Host != "api.github.com") host = u.Host.StartsWith("api.") ? u.Host.Substring(4) : u.Host; } catch { }
            string args = "auth token -h " + host + (a.GhUser.Length > 0 ? " -u " + a.GhUser : "");
            string output;
            int code = Run(gh, args, 10000, out output);
            string t = (output ?? "").Trim();
            if (code == 0 && t.Length > 0 && t.IndexOf(' ') < 0) return t;
            why = a.GhUser.Length > 0 ? "gh is not signed in as " + a.GhUser : "gh is not signed in";
            return null;
        }

        public static string GhPath()
        {
            string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
            foreach (string d in dirs)
            {
                try { string p = Path.Combine(d.Trim().Trim('"'), "gh.exe"); if (d.Trim().Length > 0 && File.Exists(p)) return p; } catch { }
            }
            string[] known = {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI\\gh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\\GitHub CLI\\gh.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop\\shims\\gh.exe") };
            foreach (string p in known) if (File.Exists(p)) return p;
            return null;
        }

        internal static int Run(string exe, string args, int ms, out string stdout)
        {
            stdout = "";
            try
            {
                ProcessStartInfo si = new ProcessStartInfo(exe, args);
                si.UseShellExecute = false; si.CreateNoWindow = true;
                si.RedirectStandardOutput = true; si.RedirectStandardError = true;
                using (Process p = Process.Start(si))
                {
                    // Both pipes are read off this thread, so a helper that hangs is killed after ms, not waited for.
                    string err = null, outText = null;
                    Thread te = new Thread(delegate() { try { err = p.StandardError.ReadToEnd(); } catch { } });
                    Thread to = new Thread(delegate() { try { outText = p.StandardOutput.ReadToEnd(); } catch { } });
                    te.IsBackground = true; to.IsBackground = true; te.Start(); to.Start();
                    if (!p.WaitForExit(ms)) { try { p.Kill(); } catch { } return -1; }
                    to.Join(2000);
                    stdout = outText ?? "";
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }

    // Counts the calls that cost rate limit (not 304s) per account over the last hour, and decides how hard to poll.
    public sealed class Budget
    {
        readonly Queue<DateTime> calls = new Queue<DateTime>();
        public int Remaining = -1, Limit = -1;
        public DateTime ResetUtc = DateTime.MinValue;

        public void Record(Fetch f, DateTime now)
        {
            if (f.Status != 0 && !f.FromCache) calls.Enqueue(now);
            if (f.Remaining >= 0) { Remaining = f.Remaining; Limit = f.Limit; ResetUtc = f.ResetUtc; }
        }

        public int LastHour(DateTime now)
        {
            while (calls.Count > 0 && (now - calls.Peek()).TotalMinutes >= 60) calls.Dequeue();
            return calls.Count;
        }

        // 0 = normal, 1 = slowed (few calls left or over the hourly cap), 2 = paused until the reset.
        public int Level(DateTime now, int maxPerHour)
        {
            if (Remaining >= 0 && Remaining < 200 && ResetUtc > now) return 2;
            if (Remaining >= 0 && Remaining < 1000) return 1;
            if (LastHour(now) >= maxPerHour) return 1;
            return 0;
        }
    }

    sealed class RepoState
    {
        public string Name = "";
        public DateTime PushedUtc = DateTime.MinValue, CheckedUtc = DateTime.MinValue;
        public bool Explicit, Active;            // listed by name; had runs going at its last check
        public List<JobRow> Runs = new List<JobRow>();   // runs in progress (base rows)
        public QueueRow Queue = new QueueRow();
        public int QueueTotalSeen = -1;
        public string Error = "";
    }

    sealed class RunJobs
    {
        public string UpdatedAt = "";
        public DateTime FetchedUtc = DateTime.MinValue;
        public List<JobRow> Jobs = new List<JobRow>();
    }

    // 1 per account: which repos to watch, and their running and queued work.
    public sealed class AccountCollector
    {
        public readonly AccountConfig Cfg;
        readonly IFetcher http;
        public readonly Budget Budget = new Budget();
        readonly Dictionary<string, RepoState> repos = new Dictionary<string, RepoState>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<long, RunJobs> runJobs = new Dictionary<long, RunJobs>();
        readonly Dictionary<string, DateTime> runnerDenied = new Dictionary<string, DateTime>();
        List<RunnerInfo> runners = new List<RunnerInfo>();
        DateTime reposUtc = DateTime.MinValue, runnersUtc = DateTime.MinValue;
        public string Error = "";
        public string ListProblem = "";          // a repo list that could not be read while an older one is kept
        public int Unreadable;
        string roundError = "";                  // the round's account-wide failure (a refused token, no repo list)
        const int MaxRunPages = 3, MaxJobPages = 5, MaxRunnerPages = 3;   // past 100 per page, within the API budget

        public AccountCollector(AccountConfig cfg, IFetcher fetcher) { Cfg = cfg; http = fetcher; }

        public List<RunnerInfo> Runners { get { return runners; } }

        Fetch Get(string path, DateTime now)
        {
            Fetch f = http.Get(path);
            Budget.Record(f, now);
            if (f.Status == 401 && roundError.Length == 0) roundError = "GitHub refused the token (" + f.Why + ")";
            return f;
        }

        // 1 round. Returns false when it did not run (no token, paused by the budget).
        public bool Round(Config cfg, DateTime now)
        {
            string why = http.Prepare();
            if (why.Length > 0) { Error = why; return false; }
            int level = Budget.Level(now, cfg.MaxCallsPerHour);
            if (level == 2)
            {
                Error = "";
                return false;
            }
            Error = ""; roundError = "";
            if (repos.Count == 0 || (now - reposUtc).TotalMinutes >= 10) ListRepos(cfg, now);
            int jobRefreshSec = level == 0 ? 45 : 180;
            int coldSec = level == 0 ? 300 : 900;
            int unreadable = 0;
            HashSet<long> live = new HashSet<long>();
            foreach (RepoState r in repos.Values)
            {
                bool hot = r.Explicit || r.Active || (now - r.PushedUtc).TotalDays <= 7;
                double since = (now - r.CheckedUtc).TotalSeconds;
                bool due = hot ? (level == 0 || since >= 90) : since >= coldSec;
                if (due) CheckRepo(r, now);
                bool bad = r.Error.Length > 0;
                foreach (JobRow run in r.Runs)
                {
                    long id = RunId(run);
                    live.Add(id);
                    RunJobs rj;
                    runJobs.TryGetValue(id, out rj);
                    string upd = run.UpdatedAt;
                    if (rj == null || rj.UpdatedAt != upd || (now - rj.FetchedUtc).TotalSeconds >= jobRefreshSec)
                    {
                        List<JobRow> jobs = ReadJobs(r.Name, id, run, now);
                        if (jobs != null)
                        {
                            if (rj == null) { rj = new RunJobs(); runJobs[id] = rj; }
                            rj.Jobs = jobs;
                            rj.UpdatedAt = upd; rj.FetchedUtc = now;
                        }
                        else if (rj == null) bad = true;   // a run whose jobs were never read: say so, never show it as idle
                    }
                }
                if (bad) unreadable++;
            }
            Unreadable = unreadable;
            // Out of API calls: the card says it is paused until the reset; that is not a broken account.
            Error = Budget.Level(now, cfg.MaxCallsPerHour) == 2 ? "" : roundError;
            List<long> gone = new List<long>();
            foreach (long id in runJobs.Keys) if (!live.Contains(id)) gone.Add(id);
            foreach (long id in gone) runJobs.Remove(id);
            if ((now - runnersUtc).TotalSeconds >= (level == 0 ? 60 : 300)) ReadRunners(now);
            return true;
        }

        // Every page of a run's jobs (a big matrix passes 100), or null when the first page cannot be read.
        List<JobRow> ReadJobs(string repo, long id, JobRow run, DateTime now)
        {
            string path = "/repos/" + repo + "/actions/runs/" + id.ToString(CultureInfo.InvariantCulture) + "/jobs?filter=latest&per_page=100";
            Fetch f = Get(path, now);
            if (!f.Ok) return null;
            Dictionary<string, object> body = f.Body as Dictionary<string, object>;
            List<JobRow> jobs = Model.FromJobs(body, run);
            int total = Json.Int(body, "total_count");
            for (int page = 2; page <= MaxJobPages && (page - 1) * 100 < total; page++)
            {
                Fetch p = Get(path + "&page=" + page.ToString(CultureInfo.InvariantCulture), now);
                if (!p.Ok) break;
                jobs.AddRange(Model.FromJobs(p.Body as Dictionary<string, object>, run));
            }
            return jobs;
        }

        static long RunId(JobRow run)
        {
            // html_url ends with /actions/runs/<id>
            string u = run.Url;
            int i = u.LastIndexOf('/');
            long id;
            return i >= 0 && long.TryParse(u.Substring(i + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) ? id : 0;
        }

        void ListRepos(Config cfg, DateTime now)
        {
            Dictionary<string, DateTime> found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> explicitRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool anyOk = false;
            List<string> failed = new List<string>();
            foreach (string o in Cfg.Orgs)
            {
                if (Page("/orgs/" + o + "/repos?type=all&sort=pushed&per_page=100", found, now, DateTime.MinValue)) anyOk = true;
                else if (Page("/users/" + o + "/repos?type=owner&sort=pushed&per_page=100", found, now, DateTime.MinValue)) anyOk = true;
                else failed.Add(o + " (" + pageWhy + ")");
            }
            foreach (string u in Cfg.Users)
                if (Page("/users/" + u + "/repos?type=owner&sort=pushed&per_page=100", found, now, DateTime.MinValue)) anyOk = true;
                else failed.Add(u + " (" + pageWhy + ")");
            if (Cfg.Auto)
            {
                if (Page("/user/repos?sort=pushed&per_page=100", found, now, now.AddDays(-30))) anyOk = true;
                else failed.Add("your account (" + pageWhy + ")");
            }
            foreach (string r in Cfg.Repos) { explicitRepos.Add(r); if (!found.ContainsKey(r)) found[r] = now; anyOk = true; }
            string why = failed.Count == 0 ? "" : "could not list the repos of " + string.Join(", ", failed.ToArray());
            ListProblem = "";
            if (!anyOk && repos.Count > 0)
            {
                ListProblem = why;                       // keep the old list through a failed refresh; try again in 1 min
                reposUtc = now.AddMinutes(-9);
                return;
            }
            if (!anyOk && why.Length > 0) { if (roundError.Length == 0) roundError = why; }   // nothing to watch: an error, not "nothing running"
            else if (why.Length > 0) ListProblem = why;
            List<string> drop = new List<string>();
            foreach (string k in repos.Keys) if (!found.ContainsKey(k)) drop.Add(k);
            foreach (string k in drop) repos.Remove(k);
            foreach (KeyValuePair<string, DateTime> kv in found)
            {
                if (cfg.Excluded(kv.Key)) { repos.Remove(kv.Key); continue; }
                RepoState st;
                if (!repos.TryGetValue(kv.Key, out st)) { st = new RepoState(); st.Name = kv.Key; repos[kv.Key] = st; }
                st.PushedUtc = kv.Value;
                st.Explicit = explicitRepos.Contains(kv.Key);
            }
            reposUtc = now;
        }

        // Reads up to 10 pages of a repo list; stops early once repos are older than 'since' (sorted by push).
        string pageWhy = "";
        bool Page(string path, Dictionary<string, DateTime> found, DateTime now, DateTime since)
        {
            for (int page = 1; page <= 10; page++)
            {
                Fetch f = Get(path + "&page=" + page.ToString(CultureInfo.InvariantCulture), now);
                object[] arr = f.Ok ? f.Body as object[] : null;
                if (arr == null) { if (page == 1) pageWhy = f.Ok ? "unexpected answer" : f.Why; return page > 1; }
                bool older = false;
                foreach (object o in arr)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null || Json.Bool(d, "archived", false) || Json.Bool(d, "disabled", false)) continue;
                    DateTime pushed = Model.Time(Json.Get(d, "pushed_at"));
                    if (since != DateTime.MinValue && pushed < since) { older = true; continue; }
                    string name = Json.Str(d, "full_name");
                    if (name.Length > 0) found[name] = pushed;
                }
                if (arr.Length < 100 || older) break;
            }
            return true;
        }

        void CheckRepo(RepoState r, DateTime now)
        {
            r.CheckedUtc = now;
            string runsPath = "/repos/" + r.Name + "/actions/runs?status=in_progress&per_page=100";
            Fetch f = Get(runsPath, now);
            if (!f.Ok) { r.Error = f.Why; return; }
            r.Error = "";
            List<JobRow> runs = new List<JobRow>();
            AddRuns(f, runs, r.Name);
            int totalRuns = Json.Int(f.Body as Dictionary<string, object>, "total_count");
            for (int page = 2; page <= MaxRunPages && (page - 1) * 100 < totalRuns; page++)
            {
                Fetch p = Get(runsPath + "&page=" + page.ToString(CultureInfo.InvariantCulture), now);
                if (!p.Ok) { r.Error = p.Why; break; }
                AddRuns(p, runs, r.Name);
            }
            r.Runs = runs;
            Fetch q = Get("/repos/" + r.Name + "/actions/runs?status=queued&per_page=100", now);
            if (!q.Ok && r.Error.Length == 0) r.Error = q.Why;   // the old queue stays, marked unreadable
            if (q.Ok)
            {
                Dictionary<string, object> qd = q.Body as Dictionary<string, object>;
                int total = Json.Int(qd, "total_count");
                object[] qa = Json.Get(qd, "workflow_runs") as object[];
                DateTime oldest = DateTime.MinValue;
                if (qa != null && qa.Length > 0)
                {
                    Dictionary<string, object> last = qa[qa.Length - 1] as Dictionary<string, object>;
                    oldest = Model.Time(Json.Get(last, "created_at"));
                }
                if (total > 100)
                {
                    // The list is newest first: the oldest queued run is on the last page (GitHub serves 1,000 at most).
                    if (total != r.QueueTotalSeen || r.Queue.OldestUtc == DateTime.MinValue)
                    {
                        int lastPage = Math.Min((total + 99) / 100, 10);
                        Fetch lp = Get("/repos/" + r.Name + "/actions/runs?status=queued&per_page=100&page=" + lastPage.ToString(CultureInfo.InvariantCulture), now);
                        object[] la = lp.Ok ? Json.Get(lp.Body as Dictionary<string, object>, "workflow_runs") as object[] : null;
                        if (la != null && la.Length > 0) oldest = Model.Time(Json.Get(la[la.Length - 1] as Dictionary<string, object>, "created_at"));
                    }
                    else oldest = r.Queue.OldestUtc;
                }
                r.Queue = new QueueRow();
                r.Queue.Repo = r.Name; r.Queue.Count = total; r.Queue.OldestUtc = total > 0 ? oldest : DateTime.MinValue;
                r.Queue.Url = "https://github.com/" + r.Name + "/actions?query=is%3Aqueued";
                r.QueueTotalSeen = total;
            }
            r.Active = r.Runs.Count > 0 || r.Queue.Count > 0;
        }

        void AddRuns(Fetch f, List<JobRow> runs, string repo)
        {
            object[] arr = Json.Get(f.Body as Dictionary<string, object>, "workflow_runs") as object[];
            if (arr == null) return;
            foreach (object o in arr)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null) runs.Add(Model.FromRun(d, Cfg.Name, repo));
            }
        }

        void ReadRunners(DateTime now)
        {
            runnersUtc = now;
            List<RunnerInfo> list = new List<RunnerInfo>();
            List<string> scopes = new List<string>();
            foreach (string o in Cfg.RunnerOrgs) scopes.Add("/orgs/" + o + "/actions/runners?per_page=100");
            foreach (string r in Cfg.RunnerRepos) scopes.Add("/repos/" + r + "/actions/runners?per_page=100");
            bool failed = false;
            foreach (string s in scopes)
            {
                DateTime until;
                if (runnerDenied.TryGetValue(s, out until) && until > now) continue;
                // Runner lists need admin rights: a 403 or 404 is normal, so it stays quiet and is not asked again for 1 h.
                for (int page = 1; page <= MaxRunnerPages; page++)
                {
                    Fetch f = Get(page == 1 ? s : s + "&page=" + page.ToString(CultureInfo.InvariantCulture), now);
                    if (f.Status == 403 || f.Status == 404) { runnerDenied[s] = now.AddHours(1); break; }
                    if (!f.Ok) { failed = true; break; }
                    Dictionary<string, object> body = f.Body as Dictionary<string, object>;
                    object[] arr = Json.Get(body, "runners") as object[];
                    if (arr == null) break;
                    foreach (object o in arr)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        RunnerInfo ri = new RunnerInfo();
                        ri.Name = Json.Str(d, "name"); ri.Status = Json.Str(d, "status"); ri.Busy = Json.Bool(d, "busy", false);
                        list.Add(ri);
                    }
                    if (page * 100 >= Json.Int(body, "total_count")) break;
                }
            }
            if (scopes.Count > 0 && !failed) runners = list;   // a list read only in part keeps the last whole one
        }

        // The rows of this account's last round.
        public void Fill(List<JobRow> jobs, List<QueueRow> queues)
        {
            foreach (RepoState r in repos.Values)
            {
                foreach (JobRow run in r.Runs)
                {
                    RunJobs rj;
                    if (runJobs.TryGetValue(RunId(run), out rj) && rj.Jobs.Count > 0) jobs.AddRange(rj.Jobs);
                }
                if (r.Queue.Count > 0) queues.Add(r.Queue);
            }
        }

        public int RepoCount { get { return repos.Count; } }
        public IEnumerable<string> RepoNames { get { return repos.Keys; } }
    }

    // All accounts together: 1 round each, then 1 Snapshot.
    public sealed class Collector
    {
        public Config Cfg;
        readonly List<AccountCollector> accounts = new List<AccountCollector>();

        public Collector(Config cfg, Func<AccountConfig, IFetcher> make)
        {
            Cfg = cfg;
            foreach (AccountConfig a in cfg.Accounts) accounts.Add(new AccountCollector(a, make(a)));
        }

        public Snapshot Round(DateTime now)
        {
            foreach (AccountCollector a in accounts) a.Round(Cfg, now);
            return Build(now);
        }

        public Snapshot Build(DateTime now)
        {
            Snapshot s = new Snapshot();
            s.TakenUtc = now;
            List<RunnerInfo> runners = new List<RunnerInfo>();
            bool slowed = false, paused = false;
            // 2 accounts can see the same repo or runner: each job, queue, runner and repo counts once.
            HashSet<string> seenJobs = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> seenQueues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenRunners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AccountCollector a in accounts)
            {
                List<JobRow> jobs = new List<JobRow>(); List<QueueRow> queues = new List<QueueRow>();
                a.Fill(jobs, queues);
                foreach (JobRow j in jobs) if (seenJobs.Add(j.Url + "|" + j.Job)) s.Jobs.Add(j);
                foreach (QueueRow q in queues) if (seenQueues.Add(q.Repo)) s.Queues.Add(q);
                foreach (RunnerInfo ri in a.Runners) if (seenRunners.Add(ri.Name)) runners.Add(ri);
                foreach (string rn in a.RepoNames) seenRepos.Add(rn);
                AccountHealth h = new AccountHealth();
                h.Name = a.Cfg.Name; h.Error = a.Error;
                h.Remaining = a.Budget.Remaining; h.Limit = a.Budget.Limit; h.ResetUtc = a.Budget.ResetUtc;
                h.CallsLastHour = a.Budget.LastHour(now);
                s.Accounts.Add(h);
                int lv = a.Budget.Level(now, Cfg.MaxCallsPerHour);
                if (lv == 1) slowed = true;
                if (lv == 2) paused = true;
                if (a.Unreadable > 0 && s.Note.Length == 0) s.Note = Model.Count(a.Unreadable, "repo", "repos") + " could not be read (" + a.Cfg.Name + ")";
                if (a.ListProblem.Length > 0 && s.Note.Length == 0) s.Note = a.Cfg.Name + ": " + a.ListProblem;
            }
            s.ReposWatched = seenRepos.Count;
            if (paused) s.Note = "Paused until the API limit resets";
            else if (slowed) s.Note = "Slowed down to save the API limit";
            s.Queues = Model.SortQueues(s.Queues);
            s.Machines = Model.BuildMachines(s.Jobs, Cfg.Machines, runners);
            return s;
        }

        // Seconds until the next round: the configured interval, longer while any account is slowed.
        public int NextDelaySec(DateTime now)
        {
            int sec = Cfg.PollSeconds;
            foreach (AccountCollector a in accounts)
            {
                int lv = a.Budget.Level(now, Cfg.MaxCallsPerHour);
                if (lv == 1) sec = Math.Max(sec, 120);
                if (lv == 2) sec = Math.Max(sec, 300);
            }
            return sec;
        }
    }
}
