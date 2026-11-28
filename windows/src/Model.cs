// CI Widget for Windows - the data model: jobs, machines, queues, and the pure functions that build them.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CiWidget
{
    public enum MachineState { Busy, Idle, Offline, Unknown }

    // 1 job that runs, or waits inside a run that already started.
    public sealed class JobRow
    {
        public string Account = "", Repo = "", Workflow = "", Job = "", Branch = "", Event = "", Url = "", Runner = "";
        public string Status = "in_progress";      // in_progress or queued
        public string Step = "";                    // the step that runs now, "" when unknown
        public int StepIndex, StepCount;            // 1-based index of the running step, of all steps
        public int Pr;                              // pull request number, 0 when none
        public DateTime StartedUtc = DateTime.MinValue, CreatedUtc = DateTime.MinValue;
        public string Labels = "";                  // runs-on labels, comma separated
        public string Machine = "";                 // filled by Model.BuildMachines
        public string UpdatedAt = "";               // the run's updated_at: a change means its jobs changed

        public bool Running { get { return Status == "in_progress"; } }
        public string RepoShort { get { int i = Repo.IndexOf('/'); return i >= 0 ? Repo.Substring(i + 1) : Repo; } }
    }

    // Queued runs of 1 repo: a count and the oldest wait, never a row per run.
    public sealed class QueueRow
    {
        public string Repo = "", Url = "";
        public int Count;
        public DateTime OldestUtc = DateTime.MinValue;
        public string RepoShort { get { int i = Repo.IndexOf('/'); return i >= 0 ? Repo.Substring(i + 1) : Repo; } }
    }

    public sealed class RunnerInfo
    {
        public string Name = "", Status = "";       // online or offline
        public bool Busy;
    }

    public sealed class MachineView
    {
        public string Name = "", Code = "";
        public MachineState State = MachineState.Unknown;
        public List<JobRow> Jobs = new List<JobRow>();
        public int RunnersOnline, RunnersTotal;
        public bool Configured;                     // listed in the config (shown even with nothing running)
        public bool Cloud;                          // GitHub-hosted runners
    }

    public sealed class AccountHealth
    {
        public string Name = "", Error = "";
        public int Remaining = -1, Limit = -1, CallsLastHour;
        public DateTime ResetUtc = DateTime.MinValue;
        public bool Ok { get { return Error.Length == 0; } }
    }

    public sealed class Snapshot
    {
        public DateTime TakenUtc = DateTime.MinValue;   // MinValue = nothing read yet
        public List<JobRow> Jobs = new List<JobRow>();
        public List<QueueRow> Queues = new List<QueueRow>();
        public List<MachineView> Machines = new List<MachineView>();
        public List<AccountHealth> Accounts = new List<AccountHealth>();
        public int ReposWatched;
        public string Note = "";                       // a line for the card, e.g. "Slowed down: 800 API calls left"

        public int RunningCount { get { int n = 0; foreach (JobRow j in Jobs) if (j.Running) n++; return n; } }
        public int QueuedCount { get { int n = 0; foreach (QueueRow q in Queues) n += q.Count; return n; } }
        public DateTime OldestQueuedUtc
        {
            get
            {
                DateTime o = DateTime.MaxValue;
                foreach (QueueRow q in Queues) if (q.Count > 0 && q.OldestUtc != DateTime.MinValue && q.OldestUtc < o) o = q.OldestUtc;
                return o == DateTime.MaxValue ? DateTime.MinValue : o;
            }
        }
        public string FirstError
        {
            get { foreach (AccountHealth a in Accounts) if (!a.Ok) return a.Name + ": " + a.Error; return ""; }
        }
    }

    public sealed class MachineRule
    {
        public string Name = "", Code = "";
        public List<string> Patterns = new List<string>();
    }

    public static class Model
    {
        public const string CloudName = "GitHub cloud", CloudCode = "GH", OtherName = "Other";

        // "build-pc-*" style patterns: * = any run of characters, ? = 1 character, case-insensitive, whole name.
        public static bool Glob(string pattern, string text)
        {
            if (pattern == null || text == null) return false;
            string rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // GitHub's own runners are named "GitHub Actions 12" (or "Hosted Agent" on older images).
        public static bool IsCloudRunner(string runner)
        {
            if (string.IsNullOrEmpty(runner)) return false;
            return runner.StartsWith("GitHub Actions", StringComparison.OrdinalIgnoreCase) || runner.StartsWith("Hosted Agent", StringComparison.OrdinalIgnoreCase);
        }

        // Without a rule, a runner's machine is its name without a trailing "-<number>": "build-box-3" -> "build-box".
        public static string DefaultMachine(string runner)
        {
            if (string.IsNullOrEmpty(runner)) return OtherName;
            string m = Regex.Replace(runner, "[-_ ]?\\d+$", "");
            return m.Length == 0 ? runner : m;
        }

        public static string MachineFor(string runner, List<MachineRule> rules)
        {
            if (IsCloudRunner(runner)) return CloudName;
            if (string.IsNullOrEmpty(runner)) return "";
            foreach (MachineRule r in rules)
                foreach (string p in r.Patterns)
                    if (Glob(p, runner)) return r.Name;
            return DefaultMachine(runner);
        }

        // A 2-letter code for the pill: the rule's own, else the first 2 letters (or initials of 2 words).
        public static string CodeFor(string name, List<MachineRule> rules)
        {
            foreach (MachineRule r in rules) if (r.Name == name && r.Code.Length > 0) return r.Code.ToUpperInvariant();
            if (name == CloudName) return CloudCode;
            string[] words = Regex.Split(name.Trim(), "[^A-Za-z0-9]+");
            List<string> w = new List<string>();
            foreach (string s in words) if (s.Length > 0) w.Add(s);
            if (w.Count >= 2) return (w[0].Substring(0, 1) + w[1].Substring(0, 1)).ToUpperInvariant();
            if (w.Count == 1) return (w[0].Length >= 2 ? w[0].Substring(0, 2) : w[0]).ToUpperInvariant();
            return "??";
        }

        // Groups running jobs by machine and decides each machine's state. Configured machines always appear (in config
        // order); the others follow by job count. Runner lists, when readable, tell idle from offline.
        public static List<MachineView> BuildMachines(List<JobRow> jobs, List<MachineRule> rules, List<RunnerInfo> runners)
        {
            Dictionary<string, MachineView> by = new Dictionary<string, MachineView>(StringComparer.OrdinalIgnoreCase);
            List<MachineView> order = new List<MachineView>();
            foreach (MachineRule r in rules)
            {
                if (by.ContainsKey(r.Name)) continue;
                MachineView mv = new MachineView(); mv.Name = r.Name; mv.Code = CodeFor(r.Name, rules); mv.Configured = true;
                by[r.Name] = mv; order.Add(mv);
            }
            List<MachineView> extra = new List<MachineView>();
            foreach (JobRow j in jobs)
            {
                if (!j.Running) continue;
                j.Machine = MachineFor(j.Runner, rules);
                if (j.Machine.Length == 0) j.Machine = OtherName;
                MachineView mv;
                if (!by.TryGetValue(j.Machine, out mv))
                {
                    mv = new MachineView(); mv.Name = j.Machine; mv.Code = CodeFor(j.Machine, rules); mv.Cloud = j.Machine == CloudName;
                    by[j.Machine] = mv; extra.Add(mv);
                }
                mv.Jobs.Add(j);
            }
            if (runners != null)
                foreach (RunnerInfo ri in runners)
                {
                    string m = MachineFor(ri.Name, rules);
                    MachineView mv;
                    if (!by.TryGetValue(m, out mv)) continue;   // an unconfigured idle runner adds no cell
                    mv.RunnersTotal++;
                    if (ri.Status == "online") mv.RunnersOnline++;
                }
            foreach (MachineView mv in by.Values)
            {
                if (mv.Jobs.Count > 0) mv.State = MachineState.Busy;
                else if (mv.RunnersTotal > 0) mv.State = mv.RunnersOnline > 0 ? MachineState.Idle : MachineState.Offline;
                else mv.State = MachineState.Unknown;
                mv.Jobs.Sort(delegate(JobRow a, JobRow b) { return a.StartedUtc.CompareTo(b.StartedUtc); });   // longest-running first
            }
            extra.Sort(delegate(MachineView a, MachineView b)
            {
                if (a.Cloud != b.Cloud) return a.Cloud ? 1 : -1;   // GitHub cloud last
                int c = b.Jobs.Count.CompareTo(a.Jobs.Count);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            order.AddRange(extra);
            UniqueCodes(order, rules);
            return order;
        }

        // Every cell gets its own code (TC-141). Codes set in the config and GitHub cloud's are kept; a derived code that is
        // taken becomes the name's first letter with a later one ("lab-pc" -> "LA"), else with a digit. Configured machines
        // pick first, then the rest by name, so a code does not change while the same machines are busy.
        static void UniqueCodes(List<MachineView> machines, List<MachineRule> rules)
        {
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            used.Add(CloudCode);
            foreach (MachineRule r in rules)
                if (r.Code.Length > 0) { used.Add(r.Code.ToUpperInvariant()); own.Add(r.Name); }
            List<MachineView> derived = new List<MachineView>(), rest = new List<MachineView>();
            foreach (MachineView mv in machines)
            {
                if (mv.Name == CloudName || own.Contains(mv.Name)) continue;
                (mv.Configured ? derived : rest).Add(mv);
            }
            rest.Sort(delegate(MachineView a, MachineView b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            derived.AddRange(rest);
            foreach (MachineView mv in derived)
            {
                mv.Code = FreeCode(mv.Name, mv.Code, used);
                used.Add(mv.Code);
            }
        }

        static string FreeCode(string name, string code, HashSet<string> used)
        {
            if (!used.Contains(code)) return code;
            string letters = Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9]", "");
            if (letters.Length == 0) return code;
            string first = letters.Substring(0, 1);
            for (int i = 1; i < letters.Length; i++) if (!used.Contains(first + letters[i])) return first + letters[i];
            for (int d = 2; d <= 9; d++) if (!used.Contains(first + d.ToString(CultureInfo.InvariantCulture))) return first + d.ToString(CultureInfo.InvariantCulture);
            return code;
        }

        // ---------------------------------------------------------------- GitHub JSON -> rows
        public static DateTime Time(object v)
        {
            string s = v as string;
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return DateTime.MinValue;
        }

        // 1 run from /actions/runs into its base row (the job fields come later).
        public static JobRow FromRun(Dictionary<string, object> run, string account, string repo)
        {
            JobRow j = new JobRow();
            j.Account = account; j.Repo = repo;
            j.Workflow = Json.Str(run, "name");
            if (j.Workflow.Length == 0) j.Workflow = Json.Str(run, "display_title");
            j.Branch = Json.Str(run, "head_branch");
            j.Event = Json.Str(run, "event");
            j.Url = Json.Str(run, "html_url");
            j.Status = Json.Str(run, "status");
            j.CreatedUtc = Time(Json.Get(run, "created_at"));
            j.StartedUtc = Time(Json.Get(run, "run_started_at"));
            j.UpdatedAt = Json.Str(run, "updated_at");
            object[] prs = Json.Get(run, "pull_requests") as object[];
            if (prs != null && prs.Length > 0) j.Pr = Json.Int(prs[0] as Dictionary<string, object>, "number");
            return j;
        }

        // The jobs of 1 run that are not done yet, each a row with its runner and running step.
        public static List<JobRow> FromJobs(Dictionary<string, object> resp, JobRow run)
        {
            List<JobRow> list = new List<JobRow>();
            object[] jobs = Json.Get(resp, "jobs") as object[];
            if (jobs == null) return list;
            foreach (object o in jobs)
            {
                Dictionary<string, object> job = o as Dictionary<string, object>;
                if (job == null) continue;
                string st = Json.Str(job, "status");
                if (st == "completed") continue;
                JobRow j = new JobRow();
                j.Account = run.Account; j.Repo = run.Repo; j.Workflow = run.Workflow; j.Branch = run.Branch; j.Event = run.Event; j.Pr = run.Pr;
                j.Job = Json.Str(job, "name");
                j.Url = Json.Str(job, "html_url"); if (j.Url.Length == 0) j.Url = run.Url;
                j.Status = st == "in_progress" ? "in_progress" : "queued";
                j.Runner = Json.Str(job, "runner_name");
                j.StartedUtc = Time(Json.Get(job, "started_at"));
                if (j.StartedUtc == DateTime.MinValue) j.StartedUtc = run.StartedUtc;
                j.CreatedUtc = Time(Json.Get(job, "created_at")); if (j.CreatedUtc == DateTime.MinValue) j.CreatedUtc = run.CreatedUtc;
                object[] labels = Json.Get(job, "labels") as object[];
                if (labels != null) { List<string> l = new List<string>(); foreach (object x in labels) if (x is string) l.Add((string)x); j.Labels = string.Join(",", l.ToArray()); }
                object[] steps = Json.Get(job, "steps") as object[];
                if (steps != null)
                {
                    j.StepCount = steps.Length;
                    for (int i = 0; i < steps.Length; i++)
                    {
                        Dictionary<string, object> s = steps[i] as Dictionary<string, object>;
                        if (s != null && Json.Str(s, "status") == "in_progress") { j.StepIndex = i + 1; j.Step = Json.Str(s, "name"); break; }
                    }
                    if (j.StepIndex == 0)   // between steps: the next one not yet done
                        for (int i = 0; i < steps.Length; i++)
                        {
                            Dictionary<string, object> s = steps[i] as Dictionary<string, object>;
                            if (s != null && Json.Str(s, "status") != "completed") { j.StepIndex = i + 1; j.Step = Json.Str(s, "name"); break; }
                        }
                }
                list.Add(j);
            }
            return list;
        }

        // ---------------------------------------------------------------- text
        public static string Elapsed(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalSeconds < 60) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (t.TotalMinutes < 60) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            if (t.TotalHours < 24) return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)t.TotalDays).ToString(CultureInfo.InvariantCulture) + " d " + t.Hours.ToString(CultureInfo.InvariantCulture) + " h";
        }

        // The first n characters as the reader sees them (text elements), never half an emoji or an accent alone.
        public static string FirstChars(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringInfo si = new StringInfo(s);
            return si.LengthInTextElements <= n ? s : si.SubstringByTextElements(0, n);
        }

        public static string Count(int n, string one, string many)
        {
            return n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        }

        // "main", "PR 4348" or "" for the second line of a job.
        public static string Where(JobRow j)
        {
            if (j.Pr > 0) return "PR " + j.Pr.ToString(CultureInfo.InvariantCulture);
            return j.Branch;
        }

        public static string StepText(JobRow j)
        {
            if (j.StepCount == 0) return "";
            string s = "step " + j.StepIndex.ToString(CultureInfo.InvariantCulture) + "/" + j.StepCount.ToString(CultureInfo.InvariantCulture);
            return j.Step.Length > 0 ? s + " " + j.Step : s;
        }

        // Sums and sorts queues: the longest wait first, empty queues dropped.
        public static List<QueueRow> SortQueues(List<QueueRow> q)
        {
            List<QueueRow> list = new List<QueueRow>();
            foreach (QueueRow r in q) if (r.Count > 0) list.Add(r);
            list.Sort(delegate(QueueRow a, QueueRow b)
            {
                int c = a.OldestUtc.CompareTo(b.OldestUtc);
                return c != 0 ? c : b.Count.CompareTo(a.Count);
            });
            return list;
        }

        // Queue colour level: 0 fine, 1 waiting long (> warn min), 2 stuck (> crit min).
        public static int QueueLevel(DateTime oldestUtc, DateTime nowUtc, int warnMin, int critMin)
        {
            if (oldestUtc == DateTime.MinValue) return 0;
            double m = (nowUtc - oldestUtc).TotalMinutes;
            return m > critMin ? 2 : m > warnMin ? 1 : 0;
        }
    }
}
