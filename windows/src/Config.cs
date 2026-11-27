// CI Widget for Windows - JSON helpers, the config file (what to watch) and the saved settings (how to show it).
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CiWidget
{
    public static class Json
    {
        public static object Parse(string text)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            js.RecursionLimit = 64;
            return js.DeserializeObject(text);
        }

        public static string Write(object o)
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js.Serialize(o);
        }

        public static object Get(Dictionary<string, object> d, string k)
        {
            object v;
            return d != null && d.TryGetValue(k, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            if (v == null) return "";
            if (v is string) return (string)v;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            if (v == null) return 0;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static long Long(Dictionary<string, object> d, string k)
        {
            object v = Get(d, k);
            if (v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        public static bool Bool(Dictionary<string, object> d, string k, bool dflt)
        {
            object v = Get(d, k);
            return v is bool ? (bool)v : dflt;
        }

        public static List<string> Strings(Dictionary<string, object> d, string k)
        {
            List<string> l = new List<string>();
            object[] a = Get(d, k) as object[];
            if (a != null) foreach (object o in a) if (o is string && ((string)o).Trim().Length > 0) l.Add(((string)o).Trim());
            string one = Get(d, k) as string;
            if (one != null && one.Trim().Length > 0) l.Add(one.Trim());
            return l;
        }
    }

    // 1 GitHub identity and what to watch with it. The token is read at run time, never stored.
    public sealed class AccountConfig
    {
        public string Name = "";        // shown in the card
        public string GhUser = "";      // gh account whose token to use ("" = gh's active account)
        public string TokenEnv = "";    // or an environment variable that holds a token
        public string Api = "https://api.github.com";
        public List<string> Orgs = new List<string>();        // every non-archived repo of these orgs
        public List<string> Users = new List<string>();       // every non-archived repo these users own
        public List<string> Repos = new List<string>();       // single repos, "owner/name"
        public List<string> RunnerOrgs = new List<string>();  // org runner lists (needs admin:org) to tell idle from offline
        public List<string> RunnerRepos = new List<string>(); // repo runner lists (needs repo admin)
        public bool Auto;               // no orgs, users or repos: the token's own repos pushed in the last 30 days
    }

    public sealed class Config
    {
        public List<AccountConfig> Accounts = new List<AccountConfig>();
        public List<MachineRule> Machines = new List<MachineRule>();
        public List<string> Exclude = new List<string>();      // repo patterns to skip, "owner/name" globs
        public int PollSeconds = 30;
        public int MaxCallsPerHour = 900;                      // per account, out of GitHub's 5,000
        public int QueueWarnMinutes = 10, QueueCritMinutes = 30;
        public string Error = "";                              // why the file could not be read ("" = fine)

        public static string Dir
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("CIWIDGET_HOME");
                if (!string.IsNullOrEmpty(over)) return over;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ci-widget");
            }
        }
        public static string FilePath { get { return Path.Combine(Dir, "config.json"); } }

        public const string Example =
"{\r\n" +
"  \"accounts\": [\r\n" +
"    {\r\n" +
"      \"name\": \"work\",\r\n" +
"      \"gh_user\": \"\",\r\n" +
"      \"orgs\": [],\r\n" +
"      \"repos\": [],\r\n" +
"      \"runner_orgs\": []\r\n" +
"    }\r\n" +
"  ],\r\n" +
"  \"machines\": [\r\n" +
"    { \"name\": \"Build box\", \"code\": \"BB\", \"runners\": [\"build-box-*\"] }\r\n" +
"  ],\r\n" +
"  \"exclude_repos\": [],\r\n" +
"  \"poll_seconds\": 30,\r\n" +
"  \"max_calls_per_hour\": 900\r\n" +
"}\r\n";

        // The default without a file: gh's active account, its repos pushed in the last 30 days.
        public static Config Default()
        {
            Config c = new Config();
            AccountConfig a = new AccountConfig(); a.Name = "GitHub"; a.Auto = true;
            c.Accounts.Add(a);
            return c;
        }

        public static Config Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return Default();
                return Parse(File.ReadAllText(FilePath, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                Config c = Default();
                c.Error = "config.json: " + ex.Message;
                return c;
            }
        }

        public static Config Parse(string text)
        {
            Config c = new Config();
            Dictionary<string, object> root = Json.Parse(text) as Dictionary<string, object>;
            if (root == null) throw new FormatException("the file is not a JSON object");
            object[] accounts = Json.Get(root, "accounts") as object[];
            if (accounts != null)
                foreach (object o in accounts)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    AccountConfig a = new AccountConfig();
                    a.GhUser = Json.Str(d, "gh_user");
                    a.TokenEnv = Json.Str(d, "token_env");
                    a.Name = Json.Str(d, "name");
                    if (a.Name.Length == 0) a.Name = a.GhUser.Length > 0 ? a.GhUser : "GitHub";
                    string api = Json.Str(d, "api"); if (api.Length > 0) a.Api = api.TrimEnd('/');
                    a.Orgs = Json.Strings(d, "orgs");
                    a.Users = Json.Strings(d, "users");
                    a.Repos = Json.Strings(d, "repos");
                    a.RunnerOrgs = Json.Strings(d, "runner_orgs");
                    a.RunnerRepos = Json.Strings(d, "runner_repos");
                    a.Auto = a.Orgs.Count == 0 && a.Users.Count == 0 && a.Repos.Count == 0;
                    c.Accounts.Add(a);
                }
            if (c.Accounts.Count == 0) c.Accounts = Default().Accounts;
            object[] machines = Json.Get(root, "machines") as object[];
            if (machines != null)
                foreach (object o in machines)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    MachineRule r = new MachineRule();
                    r.Name = Json.Str(d, "name");
                    r.Code = Json.Str(d, "code");
                    if (r.Code.Length > 3) r.Code = r.Code.Substring(0, 3);
                    r.Patterns = Json.Strings(d, "runners");
                    if (r.Name.Length > 0) c.Machines.Add(r);
                }
            c.Exclude = Json.Strings(root, "exclude_repos");
            int n = Json.Int(root, "poll_seconds"); if (n > 0) c.PollSeconds = Math.Max(10, Math.Min(600, n));
            n = Json.Int(root, "max_calls_per_hour"); if (n > 0) c.MaxCallsPerHour = Math.Max(60, Math.Min(4500, n));
            n = Json.Int(root, "queue_warn_minutes"); if (n > 0) c.QueueWarnMinutes = n;
            n = Json.Int(root, "queue_crit_minutes"); if (n > 0) c.QueueCritMinutes = Math.Max(n, c.QueueWarnMinutes);
            return c;
        }

        public bool Excluded(string repo)
        {
            foreach (string p in Exclude) if (Model.Glob(p, repo)) return true;
            return false;
        }

        // Writes the example next to the widget the first time, so "Edit config" opens something to fill in.
        public static void EnsureExample()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string ex = Path.Combine(Dir, "config.example.json");
                if (!File.Exists(ex)) File.WriteAllText(ex, Example, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // How the widget looks and where it sits: settings.ini beside the config.
    public sealed class Settings
    {
        public int OffsetFromRight = -1;   // DIP from the bar's right edge; -1 = automatic
        public string ViewSearch = "", ViewMachine = "", ViewStatus = "", ViewGroup = "machine";
        public string ViewSort = "elapsed";

        static string FilePath { get { return Path.Combine(Config.Dir, "settings.ini"); } }

        public static Settings Load()
        {
            try { if (File.Exists(FilePath)) return Parse(File.ReadAllLines(FilePath)); } catch { }
            return new Settings();
        }

        internal static Settings Parse(IEnumerable<string> lines)
        {
            Settings s = new Settings();
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                int n;
                if (k == "offset" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) s.OffsetFromRight = n;
                else if (k == "view.search") s.ViewSearch = v;
                else if (k == "view.machine") s.ViewMachine = v;
                else if (k == "view.status") s.ViewStatus = v;
                else if (k == "view.group" && (v == "machine" || v == "repo" || v == "none")) s.ViewGroup = v;
                else if (k == "view.sort") s.ViewSort = v;
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                StringBuilder sb = new StringBuilder();
                sb.Append("offset=").Append(OffsetFromRight.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append("view.search=").Append(OneLine(ViewSearch)).Append("\r\n");
                sb.Append("view.machine=").Append(OneLine(ViewMachine)).Append("\r\n");
                sb.Append("view.status=").Append(OneLine(ViewStatus)).Append("\r\n");
                sb.Append("view.group=").Append(ViewGroup).Append("\r\n");
                sb.Append("view.sort=").Append(OneLine(ViewSort)).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }

        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }
    }
}
