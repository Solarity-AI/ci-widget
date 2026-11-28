// CI Widget for Windows - command-line test modes: self-test, renders and a live probe. No console: each writes a file.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
// Sample data is invented (acme/*, "Build box", "Mac mini"): screenshots and tests never show real repos or machines.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;

namespace CiWidget
{
    // Answers API paths from a table, so the collector runs offline. Counts every call.
    public sealed class FakeFetcher : IFetcher
    {
        public readonly Dictionary<string, object> Answers = new Dictionary<string, object>();
        public readonly List<string> Calls = new List<string>();
        public string PrepareError = "";
        public int Remaining = 4800, Limit = 5000;
        public int FailAll;                      // every call answers this HTTP status
        public bool Garbage;                     // every call answers 200 with JSON that can't be read
        public readonly Dictionary<string, int> FailPath = new Dictionary<string, int>();

        public string Prepare() { return PrepareError; }

        public Fetch Get(string path)
        {
            Calls.Add(path);
            Fetch f = new Fetch();
            f.Remaining = Remaining; f.Limit = Limit; f.ResetUtc = TestModes.Now.AddMinutes(40);
            Remaining--;
            int st = 0;
            if (FailAll != 0 || FailPath.TryGetValue(path, out st))
            {
                f.Status = FailAll != 0 ? FailAll : st;
                f.Error = f.Status == 401 ? "Bad credentials" : "Server Error";
                return f;
            }
            if (Garbage) { f.Status = 200; f.Error = "unreadable answer"; return f; }
            object body;
            if (!Answers.TryGetValue(path, out body))
            {
                // A repo list asked for page 2+ that the table doesn't have: an empty page.
                if (path.IndexOf("/repos?", StringComparison.Ordinal) >= 0 && path.IndexOf("&page=1", StringComparison.Ordinal) < 0) { f.Status = 200; f.Body = new object[0]; return f; }
                f.Status = 404; f.Error = "Not Found"; return f;
            }
            f.Status = 200;
            f.Body = Json.Parse(Json.Write(body));   // through real JSON text, the way GitHub's answers arrive
            return f;
        }
    }

    internal static class TestModes
    {
        public static readonly DateTime Now = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);

        // ------------------------------------------------------------------ sample data
        static Dictionary<string, object> D(params object[] kv)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        static string T(DateTime t) { return t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture); }

        static object Repo(string name, int pushedDaysAgo, bool archived)
        {
            return D("full_name", name, "archived", archived, "disabled", false, "pushed_at", T(Now.AddDays(-pushedDaysAgo)));
        }

        static object Run(string repo, long id, string name, string branch, int pr, int startedMinAgo, string updated)
        {
            object[] prs = pr > 0 ? new object[] { D("number", pr) } : new object[0];
            return D("id", id, "name", name, "head_branch", branch, "event", pr > 0 ? "pull_request" : "push",
                     "html_url", "https://github.com/" + repo + "/actions/runs/" + id.ToString(CultureInfo.InvariantCulture),
                     "status", "in_progress", "created_at", T(Now.AddMinutes(-startedMinAgo - 1)),
                     "run_started_at", T(Now.AddMinutes(-startedMinAgo)), "updated_at", updated, "pull_requests", prs);
        }

        static object Job(string repo, long id, string name, string status, string runner, int startedMinAgo, string[] labels, string[] steps, int runningStep)
        {
            List<object> st = new List<object>();
            for (int i = 0; i < steps.Length; i++)
                st.Add(D("name", steps[i], "status", i + 1 < runningStep ? "completed" : i + 1 == runningStep ? "in_progress" : "queued"));
            return D("name", name, "status", status, "runner_name", runner ?? "",
                     "html_url", "https://github.com/" + repo + "/actions/runs/1/job/" + id.ToString(CultureInfo.InvariantCulture),
                     "started_at", status == "in_progress" ? T(Now.AddMinutes(-startedMinAgo)) : null,
                     "created_at", T(Now.AddMinutes(-startedMinAgo - 1)), "labels", labels, "steps", st.ToArray());
        }

        static object Runs(int total, params object[] runs) { return D("total_count", total, "workflow_runs", runs); }

        static object QueuedRun(int createdMinAgo)
        {
            return D("id", 9000 + createdMinAgo, "name", "CI", "status", "queued", "created_at", T(Now.AddMinutes(-createdMinAgo)));
        }

        public const string SampleConfig =
            "{ \"accounts\": [ { \"name\": \"acme\", \"orgs\": [\"acme\"], \"runner_orgs\": [\"acme\"] } ]," +
            "  \"machines\": [ { \"name\": \"Build box\", \"code\": \"BB\", \"runners\": [\"build-box-*\"] }," +
            "                  { \"name\": \"Mac mini\", \"code\": \"MM\", \"runners\": [\"mac-mini-*\"] }," +
            "                  { \"name\": \"Lab PC\", \"code\": \"LP\", \"runners\": [\"lab-pc\"] } ]," +
            "  \"exclude_repos\": [\"acme/sandbox-*\"], \"poll_seconds\": 30 }";

        static readonly string[] Steps = { "Set up job", "Checkout", "Install", "Build", "Test", "Upload", "Post Checkout", "Complete job" };

        // An org with 5 repos (1 archived, 1 excluded): 6 running jobs on 3 machines and GitHub's cloud, 2 queues.
        public static FakeFetcher SampleFetcher()
        {
            FakeFetcher f = new FakeFetcher();
            f.Answers["/orgs/acme/repos?type=all&sort=pushed&per_page=100&page=1"] = new object[] {
                Repo("acme/web-app", 0, false), Repo("acme/api", 1, false), Repo("acme/mobile", 3, false),
                Repo("acme/old-site", 400, true), Repo("acme/sandbox-1", 2, false), Repo("acme/docs", 60, false) };
            string web = "acme/web-app", api = "acme/api", mob = "acme/mobile", docs = "acme/docs";
            f.Answers["/repos/" + web + "/actions/runs?status=in_progress&per_page=100"] = Runs(2,
                Run(web, 101, "CI", "main", 0, 12, "u1"), Run(web, 102, "E2E", "feature/login", 4348, 41, "u1"));
            f.Answers["/repos/" + web + "/actions/runs/101/jobs?filter=latest&per_page=100"] = D("jobs", new object[] {
                Job(web, 1, "build", "in_progress", "build-box-1", 12, new string[] { "self-hosted", "build-box" }, Steps, 5),
                Job(web, 2, "lint", "in_progress", "build-box-2", 11, new string[] { "self-hosted", "build-box" }, Steps, 3),
                Job(web, 3, "deploy", "queued", null, 0, new string[] { "self-hosted", "gpu" }, new string[0], 0) });
            f.Answers["/repos/" + web + "/actions/runs/102/jobs?filter=latest&per_page=100"] = D("jobs", new object[] {
                Job(web, 4, "e2e (chrome)", "in_progress", "mac-mini-1", 41, new string[] { "self-hosted", "macos" }, Steps, 6),
                Job(web, 5, "unit", "completed", "build-box-1", 50, new string[] { "self-hosted" }, Steps, 8) });
            object[] page1 = new object[100], page3 = new object[40];
            for (int i = 0; i < 100; i++) page1[i] = QueuedRun(1 + i / 10);
            for (int i = 0; i < 40; i++) page3[i] = QueuedRun(30 + i);
            f.Answers["/repos/" + web + "/actions/runs?status=queued&per_page=100"] = Runs(240, page1);
            f.Answers["/repos/" + web + "/actions/runs?status=queued&per_page=100&page=3"] = Runs(240, page3);
            f.Answers["/repos/" + api + "/actions/runs?status=in_progress&per_page=100"] = Runs(1, Run(api, 201, "Release", "main", 0, 95, "u1"));
            f.Answers["/repos/" + api + "/actions/runs/201/jobs?filter=latest&per_page=100"] = D("jobs", new object[] {
                Job(api, 6, "publish", "in_progress", "build-box-3", 95, new string[] { "self-hosted" }, Steps, 4),
                Job(api, 7, "docker", "in_progress", "GitHub Actions 12", 20, new string[] { "ubuntu-latest" }, Steps, 4) });
            f.Answers["/repos/" + api + "/actions/runs?status=queued&per_page=100"] = Runs(3, QueuedRun(4), QueuedRun(6), QueuedRun(7));
            f.Answers["/repos/" + mob + "/actions/runs?status=in_progress&per_page=100"] = Runs(1, Run(mob, 301, "iOS", "main", 0, 3, "u1"));
            f.Answers["/repos/" + mob + "/actions/runs/301/jobs?filter=latest&per_page=100"] = D("jobs", new object[] {
                Job(mob, 8, "build-ios", "in_progress", "mac-mini-2", 3, new string[] { "self-hosted", "macos" }, Steps, 2) });
            f.Answers["/repos/" + mob + "/actions/runs?status=queued&per_page=100"] = Runs(0);
            f.Answers["/repos/" + docs + "/actions/runs?status=in_progress&per_page=100"] = Runs(0);
            f.Answers["/repos/" + docs + "/actions/runs?status=queued&per_page=100"] = Runs(0);
            f.Answers["/orgs/acme/actions/runners?per_page=100"] = D("total_count", 6, "runners", new object[] {
                D("name", "build-box-1", "status", "online", "busy", true), D("name", "build-box-2", "status", "online", "busy", true),
                D("name", "build-box-3", "status", "online", "busy", true), D("name", "mac-mini-1", "status", "online", "busy", true),
                D("name", "mac-mini-2", "status", "online", "busy", true), D("name", "lab-pc", "status", "offline", "busy", false) });
            return f;
        }

        public static Snapshot Sample(out Config cfg)
        {
            cfg = Config.Parse(SampleConfig);
            FakeFetcher f = SampleFetcher();
            Collector c = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f; });
            return c.Round(Now);
        }

        // A heavy load: 6 machines, 30 jobs, 12 queues, to check the card's height cap and "+N more".
        static Snapshot Heavy(Config cfg)
        {
            Snapshot s = new Snapshot(); s.TakenUtc = Now; s.ReposWatched = 31;
            string[] machines = { "build-box", "mac-mini", "lab-pc", "gpu-rig", "spare" };
            for (int i = 0; i < 30; i++)
            {
                JobRow j = new JobRow();
                j.Repo = "acme/service-" + (i % 7).ToString(CultureInfo.InvariantCulture); j.Workflow = "CI"; j.Job = "test (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                j.Branch = "main"; j.Url = "https://github.com/acme/x/actions/runs/" + i.ToString(CultureInfo.InvariantCulture);
                j.Runner = i % 6 == 5 ? "GitHub Actions " + i.ToString(CultureInfo.InvariantCulture) : machines[i % 5] + "-" + (i % 3).ToString(CultureInfo.InvariantCulture);
                j.StartedUtc = Now.AddMinutes(-(i * 13 % 400)); j.StepCount = 8; j.StepIndex = 1 + i % 8; j.Step = Steps[i % 8];
                s.Jobs.Add(j);
            }
            for (int i = 0; i < 12; i++)
            {
                QueueRow q = new QueueRow(); q.Repo = "acme/queue-" + i.ToString(CultureInfo.InvariantCulture); q.Count = 50 * (i + 1); q.OldestUtc = Now.AddMinutes(-(5 + i * 7));
                q.Url = "https://github.com/" + q.Repo + "/actions"; s.Queues.Add(q);
            }
            s.Queues = Model.SortQueues(s.Queues);
            s.Machines = Model.BuildMachines(s.Jobs, cfg.Machines, null);
            AccountHealth h = new AccountHealth(); h.Name = "acme"; h.Remaining = 3120; h.Limit = 5000; s.Accounts.Add(h);
            return s;
        }

        static Snapshot Failed()
        {
            Snapshot s = new Snapshot(); s.TakenUtc = Now;
            AccountHealth h = new AccountHealth(); h.Name = "acme"; h.Error = "gh is not signed in as octo-dev"; s.Accounts.Add(h);
            return s;
        }

        // ------------------------------------------------------------------ self-test
        sealed class Checks
        {
            public int Pass, Fail;
            public readonly StringBuilder Log = new StringBuilder();
            public void That(bool ok, string what)
            {
                if (ok) Pass++; else Fail++;
                Log.Append(ok ? "PASS " : "FAIL ").Append(what).Append("\r\n");
            }
            public void Eq(object want, object got, string what)
            {
                bool ok = object.Equals(want, got);
                That(ok, what + (ok ? "" : " (want " + want + ", got " + got + ")"));
            }
        }

        public static int SelfTest(string report)
        {
            Checks c = new Checks();
            try { Run(c); }
            catch (Exception ex) { c.That(false, "unexpected exception: " + ex); }
            c.Log.Append(c.Pass.ToString(CultureInfo.InvariantCulture)).Append(" passed, ").Append(c.Fail.ToString(CultureInfo.InvariantCulture)).Append(" failed\r\n");
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(report));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(report, c.Log.ToString(), new UTF8Encoding(false));
            }
            catch { return 2; }
            return c.Fail == 0 ? 0 : 3;
        }

        static void Run(Checks c)
        {
            // Model: names, machines, text.
            c.That(Model.Glob("build-box-*", "Build-Box-12"), "glob matches case-insensitively");
            c.That(!Model.Glob("build-box-*", "build-boxer"), "glob needs the whole name");
            c.That(Model.Glob("lab-pc", "lab-pc") && !Model.Glob("lab-pc", "lab-pc-2"), "glob without * is exact");
            c.Eq("build-box", Model.DefaultMachine("build-box-3"), "default machine drops a trailing number");
            c.Eq("spare", Model.DefaultMachine("spare"), "default machine keeps a name without a number");
            c.Eq(Model.CloudName, Model.MachineFor("GitHub Actions 7", new List<MachineRule>()), "GitHub's runners go to GitHub cloud");
            c.Eq("GH", Model.CodeFor(Model.CloudName, new List<MachineRule>()), "GitHub cloud code");
            c.Eq("MM", Model.CodeFor("mac-mini", new List<MachineRule>()), "code from 2 words");
            c.Eq("SP", Model.CodeFor("spare", new List<MachineRule>()), "code from 1 word");
            c.Eq("45 s", Model.Elapsed(TimeSpan.FromSeconds(45)), "elapsed seconds");
            c.Eq("12 min", Model.Elapsed(TimeSpan.FromMinutes(12.7)), "elapsed minutes");
            c.Eq("1 h 35 min", Model.Elapsed(TimeSpan.FromMinutes(95)), "elapsed hours");
            c.Eq("2 d 3 h", Model.Elapsed(TimeSpan.FromHours(51)), "elapsed days");
            c.Eq("0 s", Model.Elapsed(TimeSpan.FromSeconds(-5)), "elapsed never negative");
            c.Eq("1 job", Model.Count(1, "job", "jobs"), "count singular");
            c.Eq("1,234 jobs", Model.Count(1234, "job", "jobs"), "count plural with separator");
            c.Eq("1.2k", Painter.Short(1234), "short count");
            c.Eq("579", Painter.Short(579), "short count under 1,000");
            c.Eq("12k", Painter.Short(12345), "short count over 10,000");
            c.Eq(0, Model.QueueLevel(Now.AddMinutes(-5), Now, 10, 30), "queue 5 min is fine");
            c.Eq(1, Model.QueueLevel(Now.AddMinutes(-15), Now, 10, 30), "queue 15 min warns");
            c.Eq(2, Model.QueueLevel(Now.AddMinutes(-45), Now, 10, 30), "queue 45 min is critical");
            c.Eq(0, Model.QueueLevel(DateTime.MinValue, Now, 10, 30), "no queue time is fine");
            c.Eq(2, Painter.RunLevel(TimeSpan.FromHours(7)), "a 7 h job looks stuck");

            // Config: the example parses, values are clamped, a broken file is reported, not thrown.
            Config ex = Config.Parse(Config.Example);
            c.Eq(1, ex.Accounts.Count, "example has 1 account");
            c.That(ex.Accounts[0].Auto, "example account with no repos watches its own recent repos");
            c.Eq(1, ex.Machines.Count, "example has 1 machine rule");
            Config clamp = Config.Parse("{\"poll_seconds\": 1, \"max_calls_per_hour\": 99999, \"queue_warn_minutes\": 20, \"queue_crit_minutes\": 5, \"machines\": [{\"name\": \"x\", \"code\": \"ABCD\"}]}");
            c.Eq(10, clamp.PollSeconds, "poll interval at least 10 s");
            c.Eq(4500, clamp.MaxCallsPerHour, "hourly cap at most 4,500");
            c.Eq(20, clamp.QueueCritMinutes, "critical wait never under the warning");
            c.Eq("ABC", clamp.Machines[0].Code, "machine code cut to 3 letters");
            c.Eq(1, clamp.Accounts.Count, "no accounts means the default account");
            Config sc = Config.Parse(SampleConfig);
            c.That(sc.Excluded("acme/sandbox-1") && !sc.Excluded("acme/web-app"), "exclude patterns");
            bool threw = false;
            try { Config.Parse("{ not json"); } catch { threw = true; }
            c.That(threw, "a broken config file throws for Load to report");
            Settings st = Settings.Parse(new string[] { "offset=120", "view.group=repo", "view.group=bogus", "view.search=api" });
            c.Eq(120, st.OffsetFromRight, "settings offset");
            c.Eq("repo", st.ViewGroup, "settings ignore an unknown group");
            c.Eq("api", st.ViewSearch, "settings search");

            // Collector on the invented org.
            Config cfg = Config.Parse(SampleConfig);
            FakeFetcher f = SampleFetcher();
            Collector col = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f; });
            Snapshot s = col.Round(Now);
            int calls1 = f.Calls.Count;
            c.Eq(4, s.ReposWatched, "archived and excluded repos are skipped");
            c.Eq(6, s.RunningCount, "6 jobs running");
            c.Eq(7, s.Jobs.Count, "plus 1 job waiting for a runner; completed jobs dropped");
            c.Eq(243, s.QueuedCount, "queued runs summed over repos");
            c.Eq("web-app", s.Queues.Count > 0 ? s.Queues[0].RepoShort : "", "longest wait first");
            c.Eq(Now.AddMinutes(-69), s.OldestQueuedUtc, "oldest queued run read from the last page");
            c.That(f.Calls.Contains("/repos/acme/web-app/actions/runs?status=queued&per_page=100&page=3"), "the last queue page is fetched");
            c.That(!f.Calls.Exists(delegate(string p) { return p.IndexOf("sandbox", StringComparison.Ordinal) >= 0; }), "excluded repo never called");
            MachineView bb = Find(s, "Build box"), mm = Find(s, "Mac mini"), lp = Find(s, "Lab PC"), gh = Find(s, Model.CloudName);
            c.Eq(3, bb == null ? -1 : bb.Jobs.Count, "Build box runs 3 jobs");
            c.Eq(2, mm == null ? -1 : mm.Jobs.Count, "Mac mini runs 2 jobs");
            c.Eq(MachineState.Offline, lp == null ? MachineState.Unknown : lp.State, "Lab PC offline from the runner list");
            c.Eq(1, gh == null ? -1 : gh.Jobs.Count, "1 job on GitHub's cloud");
            c.Eq(Model.CloudName, s.Machines[s.Machines.Count - 1].Name, "GitHub cloud listed last");
            c.Eq("api", bb != null && bb.Jobs.Count > 0 ? bb.Jobs[0].RepoShort : "", "longest-running job first");
            JobRow e2e = s.Jobs.Find(delegate(JobRow j) { return j.Job == "e2e (chrome)"; });
            c.Eq(4348, e2e == null ? 0 : e2e.Pr, "pull request number kept");
            c.Eq("step 6/8 Upload", e2e == null ? "" : Model.StepText(e2e), "running step and count");
            c.Eq("PR 4348", e2e == null ? "" : Model.Where(e2e), "where says the PR");
            c.Eq(4800 - (calls1 - 1), s.Accounts[0].Remaining, "API calls left from the last answer");

            // The next round 10 s later: runs unchanged, so no job lists are read again, and no repo list.
            Snapshot s2 = col.Round(Now.AddSeconds(10));
            List<string> second = f.Calls.GetRange(calls1, f.Calls.Count - calls1);
            c.That(!second.Exists(delegate(string p) { return p.IndexOf("/jobs?", StringComparison.Ordinal) >= 0; }), "unchanged runs: job lists not read again");
            c.That(!second.Exists(delegate(string p) { return p.StartsWith("/orgs/acme/repos", StringComparison.Ordinal); }), "repo list kept for 10 min");
            c.That(!second.Exists(delegate(string p) { return p.IndexOf("acme/docs", StringComparison.Ordinal) >= 0; }), "a cold repo is not checked every round");
            c.Eq(6, s2.RunningCount, "same jobs on the second round");

            // A changed run is read again.
            f.Answers["/repos/acme/mobile/actions/runs?status=in_progress&per_page=100"] = Runs(1, Run("acme/mobile", 301, "iOS", "main", 0, 3, "u2"));
            int before = f.Calls.Count;
            col.Round(Now.AddSeconds(20));
            c.That(f.Calls.GetRange(before, f.Calls.Count - before).Contains("/repos/acme/mobile/actions/runs/301/jobs?filter=latest&per_page=100"), "a changed run's jobs are read again");

            // Budget: slowed under 1,000 left, paused under 200 until the reset.
            Budget b = new Budget();
            Fetch low = new Fetch(); low.Status = 200; low.Remaining = 900; low.Limit = 5000; low.ResetUtc = Now.AddMinutes(30);
            b.Record(low, Now); c.Eq(1, b.Level(Now, 900), "under 1,000 calls left slows down");
            low.Remaining = 150; b.Record(low, Now); c.Eq(2, b.Level(Now, 900), "under 200 calls left pauses");
            c.Eq(1, b.Level(Now.AddMinutes(31), 900), "the pause ends at the reset");
            Budget b2 = new Budget();
            Fetch ok = new Fetch(); ok.Status = 200; ok.Remaining = 4000;
            for (int i = 0; i < 5; i++) b2.Record(ok, Now);
            Fetch cached = new Fetch(); cached.Status = 200; cached.FromCache = true;
            b2.Record(cached, Now);
            c.Eq(5, b2.LastHour(Now), "a 304 costs no call");
            c.Eq(1, b2.Level(Now, 5), "the hourly cap slows down");
            c.Eq(0, b2.LastHour(Now.AddMinutes(61)), "calls older than 1 h drop out");

            // A paused account makes no calls; a missing token is reported.
            FakeFetcher dead = SampleFetcher(); dead.PrepareError = "gh is not signed in as octo-dev";
            Collector dc = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)dead; });
            Snapshot ds = dc.Round(Now);
            c.Eq(0, dead.Calls.Count, "no token: no calls");
            c.Eq("acme: gh is not signed in as octo-dev", ds.FirstError, "no token: the reason is shown");
            FakeFetcher poor = SampleFetcher(); poor.Remaining = 150;
            Collector pc = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)poor; });
            pc.Round(Now);
            int pcalls = poor.Calls.Count;
            Snapshot ps = pc.Round(Now.AddSeconds(30));
            c.Eq(pcalls, poor.Calls.Count, "paused: no calls until the reset");
            c.Eq("Paused until the API limit resets", ps.Note, "paused: the card says why");
            c.Eq(300, pc.NextDelaySec(Now.AddSeconds(30)), "paused: 5 min between rounds");
            c.Eq(30, col.NextDelaySec(Now), "normal: the configured interval");

            // Rendering: every state draws, the card never passes its height cap, rows link to GitHub.
            Snapshot heavy = Heavy(cfg);
            foreach (bool dark in new bool[] { true, false })
            {
                Palette p = Palette.For(dark);
                foreach (Snapshot snap in new Snapshot[] { s, heavy, Failed(), new Snapshot() })
                    foreach (float scale in new float[] { 1f, 1.5f, 2f })
                    {
                        List<PillCell> cells = Painter.Cells(snap, cfg, Now);
                        using (Bitmap pill = Painter.Pill(cells, p, scale, false))
                            { c.That(pill.Height == (int)Math.Ceiling(Painter.PillH * scale), "pill height at " + scale.ToString(CultureInfo.InvariantCulture) + "x");
                              c.That(pill.HorizontalResolution == 96f, "pill drawn at 96 DPI, so text does not grow with the screen's DPI"); }
                        List<CardLink> links = new List<CardLink>();
                        int cap = (int)(700 * scale);
                        using (Bitmap card = Painter.Card(snap, cfg, p, scale, false, Now, cap, links))
                            { c.That(card.HorizontalResolution == 96f, "card drawn at 96 DPI");
                              c.That(card.Height <= cap + 40 * scale, "card fits its height cap (" + card.Height.ToString(CultureInfo.InvariantCulture) + " <= " + cap.ToString(CultureInfo.InvariantCulture) + ")"); }
                        c.That(links.Exists(delegate(CardLink l) { return l.Target == "action:showall"; }), "card has a Show all link");
                    }
                c.That(MinContrast(new Color[] { p.Text, p.SubText, p.Run, p.Queue, p.Crit, p.Link }, new Color[] { p.Bg1, p.Bg2 }) >= 4.5,
                       (dark ? "dark" : "light") + " text contrast at least 4.5:1 (" + MinContrast(new Color[] { p.Text, p.SubText, p.Run, p.Queue, p.Crit, p.Link }, new Color[] { p.Bg1, p.Bg2 }).ToString(CultureInfo.InvariantCulture) + ")");
            }
            List<PillCell> sc2 = Painter.Cells(s, cfg, Now);
            c.Eq("6", sc2[0].Value, "pill title shows the running total");
            c.That(sc2.Exists(delegate(PillCell x) { return x.Code == "LP" && x.State == MachineState.Offline && x.Value == "–"; }), "pill shows Lab PC offline");
            PillCell qc = sc2[sc2.Count - 1];
            c.That(qc.Kind == CellKind.Queue && qc.Value == "243" && qc.Level == 2, "queue cell: count and critical wait");
            List<PillCell> fc = Painter.Cells(Failed(), cfg, Now);
            c.Eq("!", fc[0].Value, "pill shows ! when no account can read");
            List<PillCell> stale = Painter.Cells(s, cfg, Now.AddMinutes(10));
            c.Eq("!", stale[0].Value, "pill shows ! when the data is 10 min old");
            List<CardLink> sl = new List<CardLink>();
            using (Painter.Card(s, cfg, Palette.For(true), 1f, false, Now, 2000, sl)) { }
            c.That(sl.Exists(delegate(CardLink l) { return l.Target.StartsWith("https://github.com/acme/web-app/actions/runs/1/job/", StringComparison.Ordinal); }), "job rows open the job on GitHub");
            c.That(Painter.CardAccessibleText(s, Now).IndexOf("Build box", StringComparison.Ordinal) >= 0, "card has text for screen readers");
            c.That(Painter.AccessibleText(sc2).IndexOf("Lab PC offline", StringComparison.Ordinal) >= 0, "pill has text for screen readers");

            // The view filter of the Show all window.
            List<JobRow> all = s.Jobs;
            c.Eq(3, ViewFilter.Apply(all, "", "Build box", "", "elapsed").Count, "filter by machine");
            c.Eq(1, ViewFilter.Apply(all, "", "", "queued", "elapsed").Count, "filter by status");
            c.Eq(1, ViewFilter.Apply(all, "E2E chrome", "", "", "elapsed").Count, "search: every word must match");
            c.Eq("api", ViewFilter.Apply(all, "", "", "running", "elapsed")[0].RepoShort, "sort by elapsed, longest first");
            c.Eq("api", ViewFilter.Apply(all, "", "", "", "repo")[0].RepoShort, "sort by repo");

            QaRegression(c, cfg, s);
        }

        // Pixels with ink after drawing s in the pill's value font into a box of the given width (left aligned, as Text() trims).
        static int InkCount(string s, float width)
        {
            using (Bitmap bmp = new Bitmap(220, 30))
            {
                bmp.SetResolution(96, 96);
                using (Graphics g = Graphics.FromImage(bmp))
                using (Font f = new Font("Segoe UI", 10f, FontStyle.Bold, GraphicsUnit.Point))
                {
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    Painter.Text(g, s, f, Color.Black, new RectangleF(0, 0, width, 30), StringAlignment.Near, StringAlignment.Center);
                }
                int n = 0;
                for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) if (bmp.GetPixel(x, y).A > 60) n++;
                return n;
            }
        }

        // Regression tests for the QA campaign's bugs, each named by its test case (TC-<case issue>).
        static void QaRegression(Checks c, Config cfg, Snapshot sample)
        {
            // TC-30: a 4-digit count is shortened in the pill's title and machine cells, never cut to "1…".
            Snapshot big = new Snapshot(); big.TakenUtc = Now;
            for (int i = 0; i < 1234; i++)
            {
                JobRow j = new JobRow(); j.Repo = "acme/web-app"; j.Workflow = "CI"; j.Job = "t" + i.ToString(CultureInfo.InvariantCulture);
                j.Url = "https://github.com/acme/web-app/actions/runs/1/job/" + i.ToString(CultureInfo.InvariantCulture);
                j.Runner = "build-box-1"; j.Status = "in_progress"; j.StartedUtc = Now.AddMinutes(-1); big.Jobs.Add(j);
            }
            big.Machines = Model.BuildMachines(big.Jobs, cfg.Machines, null);
            AccountHealth bh = new AccountHealth(); bh.Name = "acme"; bh.Remaining = 4000; bh.Limit = 5000; big.Accounts.Add(bh);
            List<PillCell> bc = Painter.Cells(big, cfg, Now);
            c.Eq("1.2k", bc[0].Value, "TC-30 pill title shortens 1,234 to 1.2k");
            c.That(bc.Exists(delegate(PillCell x) { return x.Code == "BB" && x.Value == "1.2k"; }), "TC-30 machine cell shortens 1,234 to 1.2k");
            // ...and each cell is wide enough for its value: the title's value area (58% of it) and a machine cell hold "1.2k" whole.
            float vw = Painter.ValueWidth("1.2k");
            c.That(Painter.CellWidth(bc[0]) * 0.58f - 2 >= vw, "TC-30 pill title is wide enough for 1.2k");
            c.That(bc.TrueForAll(delegate(PillCell x) { return x.Kind == CellKind.Title || Painter.CellWidth(x) >= Painter.ValueWidth(x.Value) + 4; }), "TC-30 machine cells are wide enough for their values");
            c.That(Painter.CellWidth(bc[0]) > Painter.TitleW, "TC-30 title cell grows past its base width for 1.2k");
            // Drawn into the title's value area, "1.2k" leaves the same ink as in a wide box: nothing was trimmed.
            float area = Painter.CellWidth(bc[0]) * 0.58f - 2;
            c.That(InkCount("1.2k", area) == InkCount("1.2k", 200), "TC-30 1.2k is drawn whole in the title cell (ink " + InkCount("1.2k", area).ToString(CultureInfo.InvariantCulture) + " vs " + InkCount("1.2k", 200).ToString(CultureInfo.InvariantCulture) + ")");

            // TC-35: the line under a machine whose rows are not drawn says how many jobs it has, not "+N more".
            c.Eq("+3 more jobs on Build box", Painter.MoreJobsText(3, 2, "Build box"), "TC-35 more jobs after shown rows");
            c.Eq("2 jobs on Build box", Painter.MoreJobsText(2, 0, "Build box"), "TC-35 no rows shown: no \"more\"");

            // TC-36: 30 busy machines keep the card under its height cap, the rest in a "+N more machines" line.
            Snapshot wide = new Snapshot(); wide.TakenUtc = Now;
            for (int i = 0; i < 60; i++)
            {
                int m = i / 2;
                string rig = "rig-" + (char)('a' + m / 26) + (char)('a' + m % 26);
                JobRow j = new JobRow(); j.Repo = "acme/api"; j.Workflow = "CI"; j.Job = "j" + i.ToString(CultureInfo.InvariantCulture);
                j.Url = "https://github.com/acme/api/actions/runs/2/job/" + i.ToString(CultureInfo.InvariantCulture);
                j.Runner = rig + "-1"; j.Status = "in_progress"; j.StartedUtc = Now.AddMinutes(-i); wide.Jobs.Add(j);
            }
            wide.Machines = Model.BuildMachines(wide.Jobs, new List<MachineRule>(), null);
            AccountHealth wh = new AccountHealth(); wh.Name = "acme"; wh.Remaining = 4000; wh.Limit = 5000; wide.Accounts.Add(wh);
            List<CardLink> wl = new List<CardLink>();
            using (System.Drawing.Bitmap card = Painter.Card(wide, cfg, Palette.For(true), 1f, false, Now, 1000, wl))
                c.That(card.Height <= 1040, "TC-36 card with 30 busy machines fits its 1,000 px cap (" + card.Height.ToString(CultureInfo.InvariantCulture) + ")");
            c.Eq(11, Painter.FitCount(30, 500, 40, 22), "TC-36 machines that fit keep room for the +N line");
            c.Eq(3, Painter.FitCount(3, 500, 40, 22), "TC-36 all machines fit: no +N line");

            // TC-37: idle chips that don't all fit end with "+N", with room kept for it.
            float[] chips29 = new float[29];
            for (int i = 0; i < chips29.Length; i++) chips29[i] = 40;
            Func<int, float> plus = delegate(int left) { return 20f; };
            c.Eq(6, Painter.ChipsFit(chips29, 0, 300, 5, plus), "TC-37 6 of 29 chips fit, room kept for +23");
            c.Eq(2, Painter.ChipsFit(new float[] { 40, 40 }, 0, 85, 5, plus), "TC-37 the last chip needs no room for +N");

            // TC-38: a redraw of Show all keeps the selected job.
            using (AllViewForm av = new AllViewForm(Settings.Parse(new string[0])))
            {
                IntPtr handle = av.Handle;
                foreach (System.Windows.Forms.Control ctl in av.Controls) handle = ctl.Handle;   // the list's window, as when shown
                av.SetData(sample);
                string url = sample.Jobs[sample.Jobs.Count - 1].Url;
                av.SelectUrl(url);
                c.Eq(url, av.SelectedUrl, "TC-38 a job can be selected");
                av.SetData(sample);
                c.Eq(url, av.SelectedUrl, "TC-38 Show all keeps the selected job across a redraw");
            }

            // TC-39: a config saved mid-edit keeps the last good config.
            Config good = Config.Parse(SampleConfig);
            Config bad = Config.FromText("{ \"machines\": [", good);
            c.That(bad.Machines.Count == 3 && bad.Accounts[0].Name == "acme" && bad.Error.IndexOf("last good config", StringComparison.Ordinal) >= 0,
                   "TC-39 broken config keeps the last good one and says so (" + bad.Error + ")");
            c.That(Config.FromText("{ not json", null).Error.Length > 0, "TC-39 broken first config is reported");

            // TC-42: a warning set alone never puts the critical wait under it.
            Config warn = Config.Parse("{\"queue_warn_minutes\": 60}");
            c.That(warn.QueueCritMinutes >= warn.QueueWarnMinutes, "TC-42 critical wait at least the warning (" + warn.QueueCritMinutes.ToString(CultureInfo.InvariantCulture) + ")");

            // TC-43: an emoji machine code is never cut inside a character.
            Config emo = Config.Parse("{\"machines\": [{\"name\": \"x\", \"code\": \"\uD83D\uDE80\uD83D\uDE80\"}]}");
            c.Eq("\uD83D\uDE80\uD83D\uDE80", emo.Machines[0].Code, "TC-43 2 emoji kept whole");
            c.Eq(6, Model.FirstChars("\uD83D\uDE80\uD83D\uDE80\uD83D\uDE80\uD83D\uDE80", 3).Length, "TC-43 3 of 4 emoji, no lone surrogate");

            // TC-46: 2 accounts that see the same org count each job, queue and repo once.
            Config two = Config.Parse(SampleConfig.Replace("{ \"name\": \"acme\", \"orgs\": [\"acme\"], \"runner_orgs\": [\"acme\"] }",
                "{ \"name\": \"one\", \"orgs\": [\"acme\"] }, { \"name\": \"two\", \"orgs\": [\"acme\"] }"));
            FakeFetcher tf = SampleFetcher();
            Snapshot ts = new Collector(two, delegate(AccountConfig a) { return (IFetcher)tf; }).Round(Now);
            c.Eq(2, two.Accounts.Count, "TC-46 config has 2 accounts");
            c.That(ts.RunningCount == 6 && ts.Jobs.Count == 7 && ts.QueuedCount == 243 && ts.ReposWatched == 4,
                   "TC-46 each job counted once (running " + ts.RunningCount.ToString(CultureInfo.InvariantCulture) + ", queued " + ts.QueuedCount.ToString(CultureInfo.InvariantCulture) + ", repos " + ts.ReposWatched.ToString(CultureInfo.InvariantCulture) + ")");

            // TC-47 to TC-50: a failing API is an error or a note, never "nothing running".
            FakeFetcher f401 = SampleFetcher(); f401.FailAll = 401;
            Snapshot s401 = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f401; }).Round(Now);
            c.That(s401.FirstError.IndexOf("refused the token", StringComparison.Ordinal) >= 0, "TC-47 a refused token is an error (" + s401.FirstError + ")");
            FakeFetcher f500 = SampleFetcher(); f500.FailAll = 500;
            Snapshot s500 = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f500; }).Round(Now);
            c.That(s500.FirstError.IndexOf("could not list the repos of acme", StringComparison.Ordinal) >= 0, "TC-48 a 500 is an error (" + s500.FirstError + ")");
            FakeFetcher fbad = SampleFetcher(); fbad.Garbage = true;
            Snapshot sbad = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)fbad; }).Round(Now);
            c.That(sbad.FirstError.IndexOf("unreadable answer", StringComparison.Ordinal) >= 0, "TC-49 unreadable JSON is an error (" + sbad.FirstError + ")");
            FakeFetcher f502 = SampleFetcher(); f502.FailPath["/repos/acme/web-app/actions/runs/101/jobs?filter=latest&per_page=100"] = 502;
            Snapshot s502 = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f502; }).Round(Now);
            c.That(s502.Note.IndexOf("1 repo could not be read", StringComparison.Ordinal) >= 0, "TC-50 a run whose jobs can't be read is noted (" + s502.Note + ")");

            // TC-51: out of API calls is a pause, never an account error.
            FakeFetcher f403 = SampleFetcher(); f403.FailAll = 403; f403.Remaining = 0;
            Snapshot s403 = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)f403; }).Round(Now);
            c.That(s403.FirstError.Length == 0 && s403.Note == "Paused until the API limit resets", "TC-51 rate limit: paused, no error (" + s403.FirstError + " / " + s403.Note + ")");

            // TC-53: a gh that hangs is stopped after the timeout, not waited for.
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            string outText;
            int code = Tokens.Run(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c ping -n 8 127.0.0.1", 1000, out outText);
            sw.Stop();
            c.That(code == -1 && sw.ElapsedMilliseconds < 5000, "TC-53 a hanging helper is stopped after 1 s (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)");

            // TC-54 and TC-55: a 150-job matrix and 130 runs are read past the first page.
            Config pages = Config.Parse("{ \"accounts\": [ { \"name\": \"acme\", \"repos\": [\"acme/big\", \"acme/many\"] } ] }");
            FakeFetcher pf = new FakeFetcher();
            string jp = "/repos/acme/big/actions/runs/501/jobs?filter=latest&per_page=100";
            pf.Answers["/repos/acme/big/actions/runs?status=in_progress&per_page=100"] = Runs(1, Run("acme/big", 501, "CI", "main", 0, 5, "u1"));
            pf.Answers["/repos/acme/big/actions/runs?status=queued&per_page=100"] = Runs(0);
            object[] jobs1 = new object[100], jobs2 = new object[50];
            for (int i = 0; i < 150; i++)
            {
                object jo = Job("acme/big", 10000 + i, "shard " + i.ToString(CultureInfo.InvariantCulture), "in_progress", "build-box-1", 5, new string[] { "self-hosted" }, Steps, 3);
                if (i < 100) jobs1[i] = jo; else jobs2[i - 100] = jo;
            }
            pf.Answers[jp] = D("total_count", 150, "jobs", jobs1);
            pf.Answers[jp + "&page=2"] = D("total_count", 150, "jobs", jobs2);
            string rp = "/repos/acme/many/actions/runs?status=in_progress&per_page=100";
            object[] runs1 = new object[100], runs2 = new object[30];
            for (int i = 0; i < 130; i++)
            {
                long id = 7000 + i;
                object ro = Run("acme/many", id, "CI", "main", 0, 5, "u1");
                if (i < 100) runs1[i] = ro; else runs2[i - 100] = ro;
                pf.Answers["/repos/acme/many/actions/runs/" + id.ToString(CultureInfo.InvariantCulture) + "/jobs?filter=latest&per_page=100"] =
                    D("total_count", 1, "jobs", new object[] { Job("acme/many", 20000 + i, "test", "in_progress", "build-box-2", 5, new string[] { "self-hosted" }, Steps, 3) });
            }
            pf.Answers[rp] = Runs(130, runs1);
            pf.Answers[rp + "&page=2"] = Runs(130, runs2);
            pf.Answers["/repos/acme/many/actions/runs?status=queued&per_page=100"] = Runs(0);
            Snapshot psn = new Collector(pages, delegate(AccountConfig a) { return (IFetcher)pf; }).Round(Now);
            c.Eq(150, psn.Jobs.FindAll(delegate(JobRow j) { return j.Repo == "acme/big" && j.Running; }).Count, "TC-54 all 150 jobs of a big matrix");
            c.Eq(130, psn.Jobs.FindAll(delegate(JobRow j) { return j.Repo == "acme/many" && j.Running; }).Count, "TC-55 all 130 runs in progress");

            // TC-60: a Startup change that fails tells the user, with the folder.
            string notDir = Path.GetTempFileName();
            string told = null;
            bool set = Startup.Toggle(true, Path.Combine(notDir, "Startup"), delegate(string m) { told = m; });
            try { File.Delete(notDir); } catch { }
            c.That(!set && told != null && told.IndexOf("Could not add CI Widget", StringComparison.Ordinal) >= 0, "TC-60 a failed Startup change is told (" + (told ?? "nothing") + ")");
        }

        static MachineView Find(Snapshot s, string name)
        {
            foreach (MachineView m in s.Machines) if (m.Name == name) return m;
            return null;
        }

        // WCAG 2 contrast: relative luminance of opaque colours, and the lowest ratio of any text colour on any background.
        static double Luminance(Color c)
        {
            double[] ch = { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
            for (int i = 0; i < 3; i++) ch[i] = ch[i] <= 0.03928 ? ch[i] / 12.92 : Math.Pow((ch[i] + 0.055) / 1.055, 2.4);
            return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
        }

        static double MinContrast(IEnumerable<Color> texts, Color[] backgrounds)
        {
            double min = double.MaxValue;
            foreach (Color t in texts)
                foreach (Color b in backgrounds)
                {
                    double x = Luminance(t), y = Luminance(b);
                    min = Math.Min(min, (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05));
                }
            return Math.Floor(min * 100) / 100;
        }

        // ------------------------------------------------------------------ renders (README images, design review)
        public static int Render(string dir)
        {
            Directory.CreateDirectory(dir);
            Config cfg;
            Snapshot sample = Sample(out cfg);
            Dictionary<string, Snapshot> kinds = new Dictionary<string, Snapshot>();
            kinds["sample"] = sample; kinds["heavy"] = Heavy(cfg); kinds["error"] = Failed(); kinds["loading"] = new Snapshot();
            Snapshot quiet = new Snapshot(); quiet.TakenUtc = Now; quiet.ReposWatched = 4; quiet.Machines = Model.BuildMachines(quiet.Jobs, cfg.Machines, null);
            AccountHealth qh = new AccountHealth(); qh.Name = "acme"; qh.Remaining = 4980; qh.Limit = 5000; quiet.Accounts.Add(qh);
            kinds["quiet"] = quiet;
            int n = 0;
            foreach (bool dark in new bool[] { true, false })
            {
                Palette p = Palette.For(dark);
                Color bar = dark ? Palette.Hex("#1C1C1C") : Palette.Hex("#EEEEEE");
                foreach (KeyValuePair<string, Snapshot> k in kinds)
                {
                    foreach (float sc in new float[] { 1f, 1.5f, 2f })
                    {
                        using (Bitmap pill = Painter.Pill(Painter.Cells(k.Value, cfg, Now), p, sc, false))
                        using (Bitmap canvas = new Bitmap(pill.Width + (int)(48 * sc), (int)(48 * sc), PixelFormat.Format32bppArgb))
                        {
                            canvas.SetResolution(96, 96);
                            canvas.SetResolution(96, 96);
                        using (Graphics g = Graphics.FromImage(canvas))
                            {
                                g.Clear(bar);
                                g.DrawImage(pill, (int)(24 * sc), (canvas.Height - pill.Height) / 2, pill.Width, pill.Height);
                            }
                            canvas.Save(Path.Combine(dir, string.Format(CultureInfo.InvariantCulture, "pill-{0}-{1}-{2:000}.png", dark ? "dark" : "light", k.Key, sc * 100)), ImageFormat.Png);
                            n++;
                        }
                    }
                    using (Bitmap card = Painter.Card(k.Value, cfg, p, 1.25f, false, Now, 900, null))
                    using (Bitmap canvas = new Bitmap(card.Width + 40, card.Height + 40, PixelFormat.Format32bppArgb))
                    {
                        canvas.SetResolution(96, 96);
                        using (Graphics g = Graphics.FromImage(canvas))
                        {
                            using (LinearGradientBrush wall = new LinearGradientBrush(new Rectangle(0, 0, canvas.Width, canvas.Height),
                                dark ? Palette.Hex("#0B1026") : Palette.Hex("#DCE6F5"), dark ? Palette.Hex("#2B1640") : Palette.Hex("#F5E6F0"), 60f))
                                g.FillRectangle(wall, 0, 0, canvas.Width, canvas.Height);
                            g.DrawImage(card, 20, 20, card.Width, card.Height);
                        }
                        canvas.Save(Path.Combine(dir, string.Format("card-{0}-{1}-125.png", dark ? "dark" : "light", k.Key)), ImageFormat.Png);
                        n++;
                    }
                }
            }
            File.WriteAllText(Path.Combine(dir, "render.txt"), n.ToString(CultureInfo.InvariantCulture) + " images\r\n");
            return 0;
        }

        // ------------------------------------------------------------------ live probe: 1 real round, written as JSON
        public static int Probe(string outFile)
        {
            Config cfg = Config.Load();
            Collector col = new Collector(cfg, delegate(AccountConfig a) { return (IFetcher)new GitHubFetcher(a); });
            DateTime now = DateTime.UtcNow;
            Snapshot s = col.Round(now);
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["taken_utc"] = T(now);
            root["config_error"] = cfg.Error;
            root["repos_watched"] = s.ReposWatched;
            root["running"] = s.RunningCount;
            root["queued"] = s.QueuedCount;
            root["note"] = s.Note;
            List<object> acc = new List<object>();
            foreach (AccountHealth a in s.Accounts) acc.Add(D("name", a.Name, "error", a.Error, "remaining", a.Remaining, "limit", a.Limit, "calls_last_hour", a.CallsLastHour));
            root["accounts"] = acc.ToArray();
            List<object> ms = new List<object>();
            foreach (MachineView m in s.Machines)
            {
                List<object> jobs = new List<object>();
                foreach (JobRow j in m.Jobs) jobs.Add(D("repo", j.Repo, "workflow", j.Workflow, "job", j.Job, "runner", j.Runner, "step", Model.StepText(j), "elapsed", Model.Elapsed(now - j.StartedUtc), "url", j.Url));
                ms.Add(D("name", m.Name, "code", m.Code, "state", m.State.ToString(), "runners_online", m.RunnersOnline, "runners_total", m.RunnersTotal, "jobs", jobs.ToArray()));
            }
            root["machines"] = ms.ToArray();
            List<object> qs = new List<object>();
            foreach (QueueRow q in s.Queues) qs.Add(D("repo", q.Repo, "count", q.Count, "oldest", q.OldestUtc == DateTime.MinValue ? "" : Model.Elapsed(now - q.OldestUtc)));
            root["queues"] = qs.ToArray();
            int waiting = 0; foreach (JobRow j in s.Jobs) if (!j.Running) waiting++;
            root["jobs_waiting_for_runner"] = waiting;
            try { File.WriteAllText(outFile, Json.Write(root), new UTF8Encoding(false)); } catch { return 2; }
            return s.FirstError.Length == 0 ? 0 : 5;
        }
    }
}
