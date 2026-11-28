// CI Widget for Windows - "Show all": every job in a sortable, filterable list, and the filter it uses.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace CiWidget
{
    // Pure filter and sort, so the self-test can check it.
    public static class ViewFilter
    {
        // search: every word must appear in repo, workflow, job, branch, PR, runner or machine (case-insensitive).
        // machine: "" = all. status: "", "running" or "queued". sort: elapsed, repo, machine, workflow, step.
        public static List<JobRow> Apply(List<JobRow> jobs, string search, string machine, string status, string sort)
        {
            string[] words = (search ?? "").Trim().ToLowerInvariant().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            List<JobRow> list = new List<JobRow>();
            foreach (JobRow j in jobs)
            {
                if (!string.IsNullOrEmpty(machine) && !string.Equals(MachineOf(j), machine, StringComparison.OrdinalIgnoreCase)) continue;
                if (status == "running" && !j.Running) continue;
                if (status == "queued" && j.Running) continue;
                string hay = (j.Repo + " " + j.Workflow + " " + j.Job + " " + j.Branch + " " + (j.Pr > 0 ? "pr " + j.Pr.ToString(CultureInfo.InvariantCulture) : "") + " " + j.Runner + " " + MachineOf(j) + " " + j.Step).ToLowerInvariant();
                bool all = true;
                foreach (string w in words) if (hay.IndexOf(w, StringComparison.Ordinal) < 0) { all = false; break; }
                if (all) list.Add(j);
            }
            Comparison<JobRow> cmp;
            switch (sort)
            {
                case "repo": cmp = delegate(JobRow a, JobRow b) { int c = string.Compare(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : Start(a).CompareTo(Start(b)); }; break;
                case "machine": cmp = delegate(JobRow a, JobRow b) { int c = string.Compare(MachineOf(a), MachineOf(b), StringComparison.OrdinalIgnoreCase); return c != 0 ? c : Start(a).CompareTo(Start(b)); }; break;
                case "workflow": cmp = delegate(JobRow a, JobRow b) { int c = string.Compare(a.Workflow, b.Workflow, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : Start(a).CompareTo(Start(b)); }; break;
                default: cmp = delegate(JobRow a, JobRow b) { return Start(a).CompareTo(Start(b)); }; break;   // longest-running first
            }
            list.Sort(cmp);
            return list;
        }

        // A waiting job has no runner yet: it sorts by when it was created.
        static DateTime Start(JobRow j) { return j.StartedUtc != DateTime.MinValue && j.Running ? j.StartedUtc : j.CreatedUtc; }

        public static string MachineOf(JobRow j) { return j.Running ? (j.Machine.Length > 0 ? j.Machine : Model.OtherName) : "Waiting"; }
    }

    internal sealed class AllViewForm : Form
    {
        readonly TextBox search = new TextBox();
        readonly ComboBox machine = new ComboBox(), status = new ComboBox(), group = new ComboBox();
        readonly Label count = new Label();
        readonly ListView list = new ListView();
        readonly Settings settings;
        Snapshot snap = new Snapshot();
        string sort;
        bool loading;

        static readonly string[] Columns = { "Repo", "Workflow / job", "Branch", "Machine", "Runner", "Step", "Elapsed" };
        static readonly string[] SortKeys = { "repo", "workflow", "", "machine", "", "", "elapsed" };

        public AllViewForm(Settings s)
        {
            settings = s;
            sort = s.ViewSort.Length > 0 ? s.ViewSort : "elapsed";
            Text = "CI Widget: all jobs";
            Font = new Font("Segoe UI", 9f);
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1100, 640);
            MinimumSize = new Size(640, 320);
            ShowInTaskbar = true;

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Top; bar.Height = 40; bar.Padding = new Padding(8, 8, 8, 4); bar.WrapContents = false;
            Label sl = new Label(); sl.Text = "Search"; sl.AutoSize = true; sl.Margin = new Padding(0, 6, 4, 0);
            search.Width = 260; search.Text = s.ViewSearch;
            search.AccessibleName = "Search jobs";
            Label ml = new Label(); ml.Text = "Machine"; ml.AutoSize = true; ml.Margin = new Padding(12, 6, 4, 0);
            machine.DropDownStyle = ComboBoxStyle.DropDownList; machine.Width = 160; machine.AccessibleName = "Machine filter";
            Label tl = new Label(); tl.Text = "Status"; tl.AutoSize = true; tl.Margin = new Padding(12, 6, 4, 0);
            status.DropDownStyle = ComboBoxStyle.DropDownList; status.Width = 140; status.AccessibleName = "Status filter";
            status.Items.AddRange(new object[] { "All", "Running", "Waiting for a runner" });
            Label gl = new Label(); gl.Text = "Group by"; gl.AutoSize = true; gl.Margin = new Padding(12, 6, 4, 0);
            group.DropDownStyle = ComboBoxStyle.DropDownList; group.Width = 110; group.AccessibleName = "Group by";
            group.Items.AddRange(new object[] { "Machine", "Repo", "None" });
            count.AutoSize = true; count.Margin = new Padding(16, 6, 0, 0);
            bar.Controls.AddRange(new Control[] { sl, search, ml, machine, tl, status, gl, group, count });

            list.Dock = DockStyle.Fill; list.View = View.Details; list.FullRowSelect = true; list.HideSelection = false;
            list.ShowGroups = true; list.AccessibleName = "Jobs";
            for (int i = 0; i < Columns.Length; i++) list.Columns.Add(Columns[i], i == 1 ? 260 : i == 0 ? 170 : i == 5 ? 200 : 110);
            list.ColumnClick += delegate(object o, ColumnClickEventArgs e) { if (SortKeys[e.Column].Length > 0) { sort = SortKeys[e.Column]; Save(); Fill(); } };
            list.ItemActivate += delegate { Open(); };

            Label hint = new Label();
            hint.Dock = DockStyle.Bottom; hint.Height = 24; hint.Padding = new Padding(8, 4, 8, 0);
            hint.Text = "Double-click a job to open it on GitHub. Click a column title to sort.";
            hint.ForeColor = SystemColors.GrayText;

            Controls.Add(list); Controls.Add(bar); Controls.Add(hint);

            loading = true;
            status.SelectedIndex = s.ViewStatus == "running" ? 1 : s.ViewStatus == "queued" ? 2 : 0;
            group.SelectedIndex = s.ViewGroup == "repo" ? 1 : s.ViewGroup == "none" ? 2 : 0;
            loading = false;
            // Search filters at once; the setting is written once typing pauses, not on every key.
            saveSoon.Interval = 600; saveSoon.Tick += delegate { saveSoon.Stop(); Save(); };
            search.TextChanged += delegate { saveSoon.Stop(); saveSoon.Start(); Fill(); };
            FormClosed += delegate { if (saveSoon.Enabled) { saveSoon.Stop(); Save(); } saveSoon.Dispose(); };
            machine.SelectedIndexChanged += delegate { if (!loading) { Save(); Fill(); } };
            status.SelectedIndexChanged += delegate { if (!loading) { Save(); Fill(); } };
            group.SelectedIndexChanged += delegate { if (!loading) { Save(); Fill(); } };
        }

        string StatusKey { get { return status.SelectedIndex == 1 ? "running" : status.SelectedIndex == 2 ? "queued" : ""; } }
        string GroupKey { get { return group.SelectedIndex == 1 ? "repo" : group.SelectedIndex == 2 ? "none" : "machine"; } }
        string MachineKey { get { return machine.SelectedIndex > 0 ? (string)machine.SelectedItem : ""; } }

        void Save()
        {
            if (loading) return;
            settings.ViewSearch = search.Text; settings.ViewStatus = StatusKey; settings.ViewGroup = GroupKey;
            settings.ViewMachine = MachineKey; settings.ViewSort = sort;
            settings.Save();
        }

        public void SetData(Snapshot s)
        {
            snap = s ?? new Snapshot();
            loading = true;
            string want = machine.SelectedIndex > 0 ? (string)machine.SelectedItem : settings.ViewMachine;
            machine.BeginUpdate();
            machine.Items.Clear(); machine.Items.Add("All machines");
            List<string> names = new List<string>();
            foreach (MachineView m in snap.Machines) names.Add(m.Name);
            foreach (JobRow j in snap.Jobs) if (!j.Running && !names.Contains("Waiting")) names.Add("Waiting");
            if (want.Length > 0 && !names.Contains(want)) names.Add(want);   // keep a saved filter even while that machine is quiet
            foreach (string n in names) machine.Items.Add(n);
            int idx = want.Length > 0 ? machine.Items.IndexOf(want) : 0;
            machine.SelectedIndex = idx < 0 ? 0 : idx;
            machine.EndUpdate();
            loading = false;
            Fill();
        }

        readonly Timer saveSoon = new Timer();

        // The selected job and the first visible one, by URL: kept across a refill, so a redraw never loses the user's place.
        public string SelectedUrl { get { return list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null; } }
        public void SelectUrl(string url) { foreach (ListViewItem it in list.Items) it.Selected = url != null && (it.Tag as string) == url; }

        void Fill()
        {
            DateTime now = DateTime.UtcNow;
            List<JobRow> rows = ViewFilter.Apply(snap.Jobs, search.Text, MachineKey, StatusKey, sort);
            string selUrl = SelectedUrl;
            string topUrl = list.Items.Count > 0 && list.TopItem != null ? list.TopItem.Tag as string : null;
            list.BeginUpdate();
            list.Items.Clear(); list.Groups.Clear();
            Dictionary<string, ListViewGroup> groups = new Dictionary<string, ListViewGroup>();
            string gk = GroupKey;
            list.ShowGroups = gk != "none";
            foreach (JobRow j in rows)
            {
                string mach = ViewFilter.MachineOf(j);
                string elapsed = j.Running && j.StartedUtc != DateTime.MinValue ? Model.Elapsed(now - j.StartedUtc) : j.CreatedUtc != DateTime.MinValue ? "waiting " + Model.Elapsed(now - j.CreatedUtc) : "";
                string wf = j.Job.Length > 0 && j.Job != j.Workflow ? j.Workflow + " / " + j.Job : j.Workflow;
                ListViewItem it = new ListViewItem(new string[] { j.Repo, wf, Model.Where(j), mach, j.Runner.Length > 0 ? j.Runner : (j.Labels.Length > 0 ? "needs " + j.Labels : ""), Model.StepText(j), elapsed });
                it.Tag = j.Url;
                if (gk != "none")
                {
                    string key = gk == "repo" ? j.Repo : mach;
                    ListViewGroup g;
                    if (!groups.TryGetValue(key, out g)) { g = new ListViewGroup(key, key); groups[key] = g; list.Groups.Add(g); }
                    it.Group = g;
                }
                list.Items.Add(it);
            }
            foreach (ListViewGroup g in groups.Values) g.Header = g.Name + " (" + g.Items.Count.ToString(CultureInfo.InvariantCulture) + ")";
            ListViewItem top = null;
            foreach (ListViewItem it in list.Items)
            {
                string u = it.Tag as string;
                if (selUrl != null && u == selUrl) { it.Selected = true; it.Focused = true; }
                if (topUrl != null && u == topUrl && top == null) top = it;
            }
            list.EndUpdate();
            if (top != null) try { list.TopItem = top; } catch (Exception) { top.EnsureVisible(); }   // TopItem can't be set with groups shown
            else if (list.SelectedItems.Count > 0) list.SelectedItems[0].EnsureVisible();
            string q = snap.QueuedCount > 0 ? " · " + Model.Count(snap.QueuedCount, "run queued", "runs queued") : "";
            count.Text = rows.Count.ToString("N0", CultureInfo.InvariantCulture) + " of " + Model.Count(snap.Jobs.Count, "job", "jobs") + q;
        }

        void Open()
        {
            if (list.SelectedItems.Count == 0) return;
            string url = list.SelectedItems[0].Tag as string;
            if (!string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) try { Process.Start(url); } catch { }
        }
    }
}
