// CI Widget for Windows - colours, the pill and the hover card, drawn into 32-bit bitmaps with per-pixel alpha.
// C# 5 only: compiled by the in-box .NET Framework compiler (csc.exe v4.x).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Text;

namespace CiWidget
{
    public sealed class Palette
    {
        public bool Dark;
        public Color Bg1, Bg2, Text, SubText, Faint, Track, Sep, Run, Queue, Crit, Off, Link, Shadow, Edge, CellBg;

        public static Palette For(bool dark)
        {
            Palette p = new Palette();
            p.Dark = dark;
            if (dark)
            {
                p.Bg1 = Hex("#2A2A40"); p.Bg2 = Hex("#18182A");
                p.Text = Hex("#F8FAFC"); p.SubText = Hex("#A5ABC3"); p.Faint = Hex("#7C829C");
                p.Track = Color.FromArgb(40, 255, 255, 255); p.Sep = Color.FromArgb(30, 255, 255, 255);
                p.Run = Hex("#34D399"); p.Queue = Hex("#FBBF24"); p.Crit = Hex("#FF8FA3"); p.Off = Hex("#6B7280"); p.Link = Hex("#93C5FD");
                p.Shadow = Color.Black; p.Edge = Color.FromArgb(70, 0, 0, 0); p.CellBg = Color.FromArgb(22, 255, 255, 255);
            }
            else
            {
                p.Bg1 = Hex("#FFFFFF"); p.Bg2 = Hex("#EEF1F8");
                p.Text = Hex("#111827"); p.SubText = Hex("#5B6478"); p.Faint = Hex("#6B7280");
                p.Track = Color.FromArgb(46, 17, 24, 39); p.Sep = Color.FromArgb(30, 17, 24, 39);
                p.Run = Hex("#047857"); p.Queue = Hex("#92400E"); p.Crit = Hex("#BE123C"); p.Off = Hex("#9CA3AF"); p.Link = Hex("#1D4ED8");
                p.Shadow = Hex("#1E293B"); p.Edge = Color.FromArgb(34, 15, 23, 42); p.CellBg = Color.FromArgb(16, 17, 24, 39);
            }
            return p;
        }

        public static Color Hex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(int.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
                                  int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
                                  int.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
        }

        public Color Level(int level) { return level >= 2 ? Crit : level == 1 ? Queue : Text; }
    }

    public enum CellKind { Title, Machine, Queue }

    public sealed class PillCell
    {
        public CellKind Kind;
        public string Code = "", Value = "";
        public MachineState State = MachineState.Unknown;
        public int Level;            // queue and title: 0 fine, 1 warn, 2 crit
        public string Spoken = "";
    }

    // A clickable area of the card: a job or queue URL, or "action:showall".
    public sealed class CardLink
    {
        public RectangleF Rect;
        public string Target = "";
    }

    public static class Painter
    {
        public const float PillH = 34, PillPad = 6, CellW = 30, TitleW = 40, CardW = 452, CardMargin = 14, Pad = 16;
        public const int MaxPillMachines = 8;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ pill
        public static List<PillCell> Cells(Snapshot s, Config cfg, DateTime now)
        {
            List<PillCell> cells = new List<PillCell>();
            PillCell t = new PillCell(); t.Kind = CellKind.Title; t.Code = "CI";
            bool stale = s.TakenUtc == DateTime.MinValue || (now - s.TakenUtc).TotalSeconds > Math.Max(180, cfg.PollSeconds * 4);
            bool allFailed = s.Accounts.Count > 0;
            foreach (AccountHealth a in s.Accounts) if (a.Ok) allFailed = false;
            if (s.TakenUtc == DateTime.MinValue && !allFailed) { t.Value = "…"; t.Spoken = "CI Widget: reading GitHub"; }
            else if (allFailed || stale) { t.Value = "!"; t.Level = 2; t.Spoken = "CI Widget: no fresh data"; }
            else { t.Value = s.RunningCount.ToString(Inv); t.Level = 0; t.Spoken = Model.Count(s.RunningCount, "job running", "jobs running"); }
            cells.Add(t);

            int shown = 0, hiddenBusy = 0;
            foreach (MachineView m in s.Machines)
            {
                if (!m.Configured && m.Jobs.Count == 0) continue;
                if (shown >= MaxPillMachines) { hiddenBusy += m.Jobs.Count; continue; }
                PillCell c = new PillCell(); c.Kind = CellKind.Machine; c.Code = m.Code; c.State = m.State;
                c.Value = m.State == MachineState.Offline ? "–" : m.Jobs.Count.ToString(Inv);
                c.Spoken = m.Name + " " + (m.State == MachineState.Offline ? "offline" : Model.Count(m.Jobs.Count, "job", "jobs"));
                cells.Add(c); shown++;
            }
            if (hiddenBusy > 0)
            {
                PillCell c = new PillCell(); c.Kind = CellKind.Machine; c.Code = "+"; c.State = MachineState.Busy;
                c.Value = hiddenBusy.ToString(Inv); c.Spoken = Model.Count(hiddenBusy, "job", "jobs") + " on other machines";
                cells.Add(c);
            }
            PillCell q = new PillCell(); q.Kind = CellKind.Queue; q.Code = "Q";
            int queued = s.QueuedCount;
            q.Value = Short(queued);
            q.Level = queued == 0 ? 0 : Math.Max(1, Model.QueueLevel(s.OldestQueuedUtc, now, cfg.QueueWarnMinutes, cfg.QueueCritMinutes));
            if (queued > 0 && Model.QueueLevel(s.OldestQueuedUtc, now, cfg.QueueWarnMinutes, cfg.QueueCritMinutes) == 0) q.Level = 0;
            q.Spoken = Model.Count(queued, "run queued", "runs queued");
            cells.Add(q);
            return cells;
        }

        // 1,234 -> "1.2k" so the cell keeps its width.
        public static string Short(int n)
        {
            if (n < 1000) return n.ToString(Inv);
            if (n < 10000) return (n / 1000.0).ToString("0.0", Inv) + "k";
            return (n / 1000).ToString(Inv) + "k";
        }

        public static string AccessibleText(List<PillCell> cells)
        {
            List<string> parts = new List<string>();
            foreach (PillCell c in cells) parts.Add(c.Spoken);
            return string.Join(", ", parts.ToArray());
        }

        public static string PillKey(List<PillCell> cells, Palette p, float scale, bool hover)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(p.Dark ? "d" : "l").Append(scale.ToString("0.00", Inv)).Append(hover ? "h" : "");
            foreach (PillCell c in cells) sb.Append('|').Append(c.Code).Append(':').Append(c.Value).Append(':').Append((int)c.State).Append(':').Append(c.Level);
            return sb.ToString();
        }

        static float CellWidth(PillCell c) { return c.Kind == CellKind.Title ? TitleW : CellW; }

        public static Size PillSize(List<PillCell> cells, float scale)
        {
            float w = PillPad * 2;
            foreach (PillCell c in cells) w += CellWidth(c) + 2;
            return new Size((int)Math.Ceiling(w * scale), (int)Math.Ceiling(PillH * scale));
        }

        public static Bitmap Pill(List<PillCell> cells, Palette p, float scale, bool hover)
        {
            Size sz = PillSize(cells, scale);
            Bitmap b = new Bitmap(sz.Width, sz.Height, PixelFormat.Format32bppPArgb);
            b.SetResolution(96, 96);   // fonts are in points: at the screen's DPI they would grow with it twice
            using (Graphics g = Graphics.FromImage(b))
            {
                Prep(g);
                RectangleF all = new RectangleF(0.5f, 0.5f, sz.Width - 1f, sz.Height - 1f);
                using (GraphicsPath path = Round(all, all.Height / 2))
                {
                    using (LinearGradientBrush br = new LinearGradientBrush(all, p.Bg1, p.Bg2, 90f)) g.FillPath(br, path);
                    if (hover) using (SolidBrush hb = new SolidBrush(Color.FromArgb(p.Dark ? 22 : 14, p.Dark ? Color.White : Color.Black))) g.FillPath(hb, path);
                    using (Pen pen = new Pen(p.Edge, 1f)) g.DrawPath(pen, path);
                }
                float x = PillPad * scale, top = 4 * scale, h = sz.Height - 8 * scale;
                using (Font codeF = new Font("Segoe UI", 7f * scale, FontStyle.Bold, GraphicsUnit.Point))
                using (Font valF = new Font("Segoe UI", 10f * scale, FontStyle.Bold, GraphicsUnit.Point))
                using (Font titleF = new Font("Segoe UI", 8f * scale, FontStyle.Bold, GraphicsUnit.Point))
                {
                    foreach (PillCell c in cells)
                    {
                        float w = CellWidth(c) * scale;
                        RectangleF r = new RectangleF(x, top, w, h);
                        DrawCell(g, c, r, p, scale, codeF, valF, titleF);
                        x += w + 2 * scale;
                    }
                }
            }
            return b;
        }

        static void DrawCell(Graphics g, PillCell c, RectangleF r, Palette p, float scale, Font codeF, Font valF, Font titleF)
        {
            Color accent = p.Text, code = p.SubText;
            bool fill = false, outline = false;
            if (c.Kind == CellKind.Title)
            {
                accent = c.Level >= 2 ? p.Crit : p.Run;
                using (GraphicsPath cp = Round(r, 7 * scale))
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(p.Dark ? 34 : 24, accent))) g.FillPath(bg, cp);
                // "CI" and the total side by side.
                RectangleF left = new RectangleF(r.X + 3 * scale, r.Y, r.Width * 0.45f, r.Height);
                RectangleF right = new RectangleF(r.X + r.Width * 0.42f, r.Y, r.Width * 0.58f - 2 * scale, r.Height);
                Text(g, "CI", titleF, p.SubText, left, StringAlignment.Center, StringAlignment.Center);
                Text(g, c.Value, valF, c.Level >= 2 ? p.Crit : p.Text, right, StringAlignment.Center, StringAlignment.Center);
                return;
            }
            if (c.Kind == CellKind.Queue)
            {
                bool any = c.Value != "0";
                accent = !any ? p.Faint : c.Level >= 2 ? p.Crit : c.Level == 1 ? p.Queue : p.Text;
                fill = any && c.Level >= 1;
            }
            else
            {
                switch (c.State)
                {
                    case MachineState.Busy: accent = p.Run; fill = true; break;
                    case MachineState.Idle: accent = p.SubText; break;
                    case MachineState.Offline: accent = p.Off; code = p.Off; outline = true; break;
                    default: accent = p.Faint; break;
                }
            }
            using (GraphicsPath cp = Round(r, 7 * scale))
            {
                if (fill) using (SolidBrush bg = new SolidBrush(Color.FromArgb(p.Dark ? 40 : 28, accent))) g.FillPath(bg, cp);
                else if (!outline) using (SolidBrush bg = new SolidBrush(p.CellBg)) g.FillPath(bg, cp);
                if (outline) using (Pen pen = new Pen(Color.FromArgb(200, accent), Math.Max(1f, 1.2f * scale))) { pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, cp); }
            }
            RectangleF top = new RectangleF(r.X, r.Y + 1 * scale, r.Width, r.Height * 0.42f);
            RectangleF bottom = new RectangleF(r.X, r.Y + r.Height * 0.36f, r.Width, r.Height * 0.64f);
            Text(g, c.Code, codeF, code, top, StringAlignment.Center, StringAlignment.Center);
            Text(g, c.Value, valF, accent, bottom, StringAlignment.Center, StringAlignment.Center);
        }

        // ------------------------------------------------------------------ card
        sealed class Line
        {
            public float H;
            public Action<Graphics, float> Draw;   // gets the line's top y
        }

        // Draws the card at most maxHeight pixels tall; rows that don't fit collapse into "+N more".
        public static Bitmap Card(Snapshot s, Config cfg, Palette p, float scale, bool pinned, DateTime now, int maxHeight, List<CardLink> links)
        {
            float sc = scale, m = CardMargin * sc, pad = Pad * sc, w = CardW * sc, inner = w - 2 * m - 2 * pad;
            float x0 = m + pad;
            Font fTitle = new Font("Segoe UI Semibold", 12f * sc, FontStyle.Regular, GraphicsUnit.Point);
            Font fHead = new Font("Segoe UI Semibold", 9.5f * sc, FontStyle.Regular, GraphicsUnit.Point);
            Font fRow = new Font("Segoe UI Semibold", 9f * sc, FontStyle.Regular, GraphicsUnit.Point);
            Font fSub = new Font("Segoe UI", 8.25f * sc, FontStyle.Regular, GraphicsUnit.Point);
            Font fCode = new Font("Segoe UI", 7f * sc, FontStyle.Bold, GraphicsUnit.Point);
            try
            {
                List<Line> lines = new List<Line>();
                List<CardLink> pending = new List<CardLink>();
                float rowH = 40 * sc, headH = 28 * sc, gap = 10 * sc;

                // Header: title, counts, freshness.
                int running = s.RunningCount, queued = s.QueuedCount;
                bool known = s.TakenUtc != DateTime.MinValue;
                if (known && s.Accounts.Count > 0) { known = false; foreach (AccountHealth a in s.Accounts) if (a.Ok) known = true; }
                AddLine(lines, 30 * sc, delegate(Graphics g, float y)
                {
                    Text(g, "CI now", fTitle, p.Text, new RectangleF(x0, y, inner * 0.4f, 28 * sc), StringAlignment.Near, StringAlignment.Center);
                    if (!known) return;   // loading or no account can read: counts would look like real zeros
                    string q = Model.Count(queued, "queued", "queued");
                    string r = Model.Count(running, "running", "running");
                    float qx = x0 + inner;
                    SizeF qs = g.MeasureString(q, fHead);
                    Chip(g, q, fHead, queued == 0 ? p.Faint : p.Queue, p, new RectangleF(qx - qs.Width - 14 * sc, y + 3 * sc, qs.Width + 14 * sc, 22 * sc), sc);
                    SizeF rs = g.MeasureString(r, fHead);
                    Chip(g, r, fHead, running == 0 ? p.Faint : p.Run, p, new RectangleF(qx - qs.Width - rs.Width - 36 * sc, y + 3 * sc, rs.Width + 14 * sc, 22 * sc), sc);
                });
                string fresh = s.TakenUtc == DateTime.MinValue ? "Reading GitHub…" : "Updated " + s.TakenUtc.ToLocalTime().ToString("HH:mm", Inv) + Ago(now - s.TakenUtc);
                string sub = s.TakenUtc == DateTime.MinValue ? fresh : Model.Count(s.ReposWatched, "repo", "repos") + " watched · " + fresh;
                AddLine(lines, 20 * sc, delegate(Graphics g, float y) { Text(g, sub, fSub, p.SubText, new RectangleF(x0, y, inner, 18 * sc), StringAlignment.Near, StringAlignment.Center); });

                // Problems: an account that can't read, a note from the budget, a bad config file.
                List<string> problems = new List<string>();
                if (cfg.Error.Length > 0) problems.Add(cfg.Error);
                foreach (AccountHealth a in s.Accounts) if (!a.Ok) problems.Add(a.Name + ": " + a.Error);
                if (s.Note.Length > 0) problems.Add(s.Note);
                foreach (string pr in problems)
                {
                    string txt = pr;
                    AddLine(lines, 30 * sc, delegate(Graphics g, float y)
                    {
                        RectangleF box = new RectangleF(x0, y + 3 * sc, inner, 24 * sc);
                        using (GraphicsPath bp = Round(box, 6 * sc))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(p.Dark ? 40 : 26, p.Crit))) g.FillPath(bb, bp);
                        Text(g, txt, fSub, p.Dark ? p.Text : p.Crit, new RectangleF(box.X + 8 * sc, box.Y, box.Width - 16 * sc, box.Height), StringAlignment.Near, StringAlignment.Center);
                    });
                }
                AddLine(lines, 6 * sc, null);

                // Budget for rows: what fits under maxHeight after the fixed parts.
                List<MachineView> busy = new List<MachineView>(), rest = new List<MachineView>();
                foreach (MachineView mv in s.Machines) if (mv.Jobs.Count > 0) busy.Add(mv); else rest.Add(mv);
                List<QueueRow> qs2 = s.Queues;
                int waitingJobs = 0;
                Dictionary<string, int> waitLabels = new Dictionary<string, int>();
                foreach (JobRow j in s.Jobs) if (!j.Running) { waitingJobs++; string k = j.Labels.Length > 0 ? j.Labels : "any runner"; int c; waitLabels.TryGetValue(k, out c); waitLabels[k] = c + 1; }
                float fixedH = 0; foreach (Line l in lines) fixedH += l.H;
                float queueH = qs2.Count == 0 && waitingJobs == 0 ? 0 : headH + Math.Min(qs2.Count, 5) * 22 * sc + (qs2.Count > 5 ? 20 * sc : 0) + (waitingJobs > 0 ? 22 * sc : 0) + gap;
                float restH = rest.Count > 0 ? 30 * sc : 0;
                float footH = 34 * sc;
                // Each busy machine costs its header, a spacer and room for a "+N more" line, then its rows.
                float avail = maxHeight - 2 * m - 2 * pad - fixedH - queueH - restH - footH - busy.Count * (headH + 24 * sc);
                int rowsAllowed = Math.Max(0, (int)(avail / rowH));
                // Share rows: 1 per busy machine in order while they last, then the rest round by round.
                Dictionary<MachineView, int> quota = new Dictionary<MachineView, int>();
                int left = rowsAllowed;
                foreach (MachineView mv in busy) { quota[mv] = left > 0 ? 1 : 0; if (left > 0) left--; }
                bool more = true;
                while (left > 0 && more)
                {
                    more = false;
                    foreach (MachineView mv in busy) if (left > 0 && quota[mv] < mv.Jobs.Count) { quota[mv]++; left--; more = true; }
                }

                bool anyOk = false; foreach (AccountHealth a in s.Accounts) if (a.Ok) anyOk = true;
                if (s.TakenUtc != DateTime.MinValue && busy.Count == 0 && anyOk)
                    AddLine(lines, 44 * sc, delegate(Graphics g, float y)
                    {
                        Text(g, "Nothing running right now.", fHead, p.SubText, new RectangleF(x0, y, inner, 40 * sc), StringAlignment.Center, StringAlignment.Center);
                    });

                foreach (MachineView mv in busy)
                {
                    MachineView mm = mv;
                    AddLine(lines, headH, delegate(Graphics g, float y)
                    {
                        RectangleF badge = new RectangleF(x0, y + 5 * sc, 26 * sc, 18 * sc);
                        using (GraphicsPath bp = Round(badge, 5 * sc))
                        using (SolidBrush bb = new SolidBrush(Color.FromArgb(p.Dark ? 48 : 30, p.Run))) g.FillPath(bb, bp);
                        Text(g, mm.Code, fCode, p.Run, badge, StringAlignment.Center, StringAlignment.Center);
                        Text(g, mm.Name, fHead, p.Text, new RectangleF(x0 + 32 * sc, y, inner * 0.55f, 28 * sc), StringAlignment.Near, StringAlignment.Center);
                        string right = Model.Count(mm.Jobs.Count, "job", "jobs");
                        if (mm.RunnersTotal > 0) right += " · " + mm.RunnersOnline.ToString(Inv) + "/" + mm.RunnersTotal.ToString(Inv) + " runners online";
                        Text(g, right, fSub, p.SubText, new RectangleF(x0 + inner * 0.4f, y, inner * 0.6f, 28 * sc), StringAlignment.Far, StringAlignment.Center);
                    });
                    int n = quota[mv];
                    for (int i = 0; i < Math.Min(n, mv.Jobs.Count); i++)
                    {
                        JobRow j = mv.Jobs[i];
                        CardLink link = new CardLink(); link.Target = j.Url; pending.Add(link);
                        AddLine(lines, rowH, delegate(Graphics g, float y) { DrawJob(g, j, p, sc, x0, y, inner, rowH, now, fRow, fSub); link.Rect = new RectangleF(x0 - 6 * sc, y, inner + 12 * sc, rowH); });
                    }
                    int hidden = mv.Jobs.Count - n;
                    if (hidden > 0)
                    {
                        CardLink link = new CardLink(); link.Target = "action:showall"; pending.Add(link);
                        AddLine(lines, 20 * sc, delegate(Graphics g, float y)
                        {
                            Text(g, "+" + Model.Count(hidden, "more job", "more jobs") + " on " + mm.Name, fSub, p.Link, new RectangleF(x0 + 32 * sc, y, inner, 18 * sc), StringAlignment.Near, StringAlignment.Center);
                            link.Rect = new RectangleF(x0, y, inner, 20 * sc);
                        });
                    }
                    AddLine(lines, 4 * sc, null);
                }

                // Machines with nothing running: 1 line of chips.
                if (rest.Count > 0)
                {
                    AddLine(lines, restH, delegate(Graphics g, float y)
                    {
                        float x = x0;
                        Text(g, "No jobs:", fSub, p.SubText, new RectangleF(x, y, 60 * sc, 26 * sc), StringAlignment.Near, StringAlignment.Center);
                        x += 56 * sc;
                        foreach (MachineView mv in rest)
                        {
                            string label = mv.Code + (mv.State == MachineState.Offline ? " offline" : mv.State == MachineState.Idle ? " idle" : "");
                            SizeF ls = g.MeasureString(label, fCode);
                            RectangleF chip = new RectangleF(x, y + 5 * sc, ls.Width + 10 * sc, 17 * sc);
                            if (chip.Right > x0 + inner) break;
                            using (GraphicsPath cp = Round(chip, 5 * sc))
                            {
                                Color c = mv.State == MachineState.Offline ? p.Off : p.SubText;
                                if (mv.State == MachineState.Offline) using (Pen pen = new Pen(c, Math.Max(1f, sc))) { pen.DashStyle = DashStyle.Dash; g.DrawPath(pen, cp); }
                                else using (SolidBrush cb = new SolidBrush(p.CellBg)) g.FillPath(cb, cp);
                                Text(g, label, fCode, c, chip, StringAlignment.Center, StringAlignment.Center);
                            }
                            x = chip.Right + 5 * sc;
                        }
                    });
                }

                // Queue: count and oldest wait per repo, the longest wait first.
                if (queueH > 0)
                {
                    AddLine(lines, gap, null);
                    AddLine(lines, headH, delegate(Graphics g, float y)
                    {
                        Text(g, "Waiting", fHead, p.Text, new RectangleF(x0, y, inner * 0.5f, 28 * sc), StringAlignment.Near, StringAlignment.Center);
                        DateTime o = s.OldestQueuedUtc;
                        string right = Model.Count(queued, "run queued", "runs queued") + (o != DateTime.MinValue ? " · oldest " + Model.Elapsed(now - o) : "");
                        Text(g, right, fSub, p.SubText, new RectangleF(x0 + inner * 0.3f, y, inner * 0.7f, 28 * sc), StringAlignment.Far, StringAlignment.Center);
                    });
                    for (int i = 0; i < Math.Min(5, qs2.Count); i++)
                    {
                        QueueRow qr = qs2[i];
                        CardLink link = new CardLink(); link.Target = qr.Url; pending.Add(link);
                        AddLine(lines, 22 * sc, delegate(Graphics g, float y)
                        {
                            int lv = Model.QueueLevel(qr.OldestUtc, now, cfg.QueueWarnMinutes, cfg.QueueCritMinutes);
                            Color c = lv >= 2 ? p.Crit : lv == 1 ? p.Queue : p.SubText;
                            using (SolidBrush d = new SolidBrush(c)) g.FillEllipse(d, x0 + 2 * sc, y + 8 * sc, 7 * sc, 7 * sc);
                            Text(g, qr.RepoShort, fRow, p.Text, new RectangleF(x0 + 16 * sc, y, inner * 0.55f, 22 * sc), StringAlignment.Near, StringAlignment.Center);
                            string r = qr.Count.ToString("N0", Inv) + " · " + (qr.OldestUtc == DateTime.MinValue ? "" : Model.Elapsed(now - qr.OldestUtc));
                            Text(g, r, fSub, c, new RectangleF(x0 + inner * 0.55f, y, inner * 0.45f, 22 * sc), StringAlignment.Far, StringAlignment.Center);
                            link.Rect = new RectangleF(x0 - 6 * sc, y, inner + 12 * sc, 22 * sc);
                        });
                    }
                    if (qs2.Count > 5)
                    {
                        int others = 0; for (int i = 5; i < qs2.Count; i++) others += qs2[i].Count;
                        string t = "+" + Model.Count(qs2.Count - 5, "more repo", "more repos") + " (" + others.ToString("N0", Inv) + " queued)";
                        AddLine(lines, 20 * sc, delegate(Graphics g, float y) { Text(g, t, fSub, p.SubText, new RectangleF(x0 + 16 * sc, y, inner, 18 * sc), StringAlignment.Near, StringAlignment.Center); });
                    }
                    if (waitingJobs > 0)
                    {
                        List<string> parts = new List<string>();
                        foreach (KeyValuePair<string, int> kv in waitLabels) parts.Add(waitLabels.Count == 1 ? kv.Key : kv.Key + " (" + kv.Value.ToString(Inv) + ")");
                        string t = Model.Count(waitingJobs, "job waits", "jobs wait") + " for a runner: " + string.Join(", ", parts.ToArray());
                        AddLine(lines, 22 * sc, delegate(Graphics g, float y) { Text(g, t, fSub, p.Queue, new RectangleF(x0 + 16 * sc, y, inner - 16 * sc, 20 * sc), StringAlignment.Near, StringAlignment.Center); });
                    }
                }

                // Footer: Show all, API budget, pin state.
                CardLink all = new CardLink(); all.Target = "action:showall"; pending.Add(all);
                AddLine(lines, footH, delegate(Graphics g, float y)
                {
                    using (Pen sp = new Pen(p.Sep, 1f)) g.DrawLine(sp, x0, y + 4 * sc, x0 + inner, y + 4 * sc);
                    string t = known ? "Show all (" + Model.Count(s.Jobs.Count, "job", "jobs") + ")" : "Show all";
                    SizeF ts = g.MeasureString(t, fHead);
                    Text(g, t, fHead, p.Link, new RectangleF(x0, y + 6 * sc, ts.Width + 4 * sc, footH - 6 * sc), StringAlignment.Near, StringAlignment.Center);
                    all.Rect = new RectangleF(x0 - 4 * sc, y + 6 * sc, ts.Width + 12 * sc, footH - 6 * sc);
                    string api = ApiText(s) + (pinned ? " · pinned" : "");
                    Text(g, api, fSub, p.Faint, new RectangleF(x0 + ts.Width + 10 * sc, y + 6 * sc, inner - ts.Width - 10 * sc, footH - 6 * sc), StringAlignment.Far, StringAlignment.Center);
                });

                float total = 0; foreach (Line l in lines) total += l.H;
                int hgt = (int)Math.Ceiling(total + 2 * m + 2 * pad);
                Bitmap b = new Bitmap((int)Math.Ceiling(w), hgt, PixelFormat.Format32bppPArgb);
                b.SetResolution(96, 96);   // scale already holds the DPI factor
                using (Graphics g = Graphics.FromImage(b))
                {
                    Prep(g);
                    RectangleF body = new RectangleF(m, m, w - 2 * m, hgt - 2 * m);
                    Shadow(g, body, 12 * sc, p, sc);
                    using (GraphicsPath bp = Round(body, 12 * sc))
                    {
                        using (LinearGradientBrush br = new LinearGradientBrush(body, p.Bg1, p.Bg2, 90f)) g.FillPath(br, bp);
                        using (Pen pen = new Pen(pinned ? p.Link : p.Edge, pinned ? 1.5f * sc : 1f)) g.DrawPath(pen, bp);
                    }
                    float y = m + pad;
                    foreach (Line l in lines) { if (l.Draw != null) l.Draw(g, y); y += l.H; }
                }
                if (links != null) { links.Clear(); foreach (CardLink l in pending) if (l.Rect.Width > 0) links.Add(l); }
                return b;
            }
            finally { fTitle.Dispose(); fHead.Dispose(); fRow.Dispose(); fSub.Dispose(); fCode.Dispose(); }
        }

        static void AddLine(List<Line> lines, float h, Action<Graphics, float> draw)
        {
            Line l = new Line(); l.H = h; l.Draw = draw; lines.Add(l);
        }

        public static string ApiText(Snapshot s)
        {
            int rem = -1, lim = -1;
            foreach (AccountHealth a in s.Accounts) if (a.Remaining >= 0 && (rem < 0 || a.Remaining < rem)) { rem = a.Remaining; lim = a.Limit; }
            if (rem < 0) return "API: not read yet";
            return "API " + rem.ToString("N0", Inv) + "/" + lim.ToString("N0", Inv) + " left";
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 90) return "";
            return " (" + Model.Elapsed(t) + " ago)";
        }

        // Job colour by how long it has run: over 1 h is long, over 6 h looks stuck (GitHub's default limit).
        public static int RunLevel(TimeSpan t) { return t.TotalHours >= 6 ? 2 : t.TotalHours >= 1 ? 1 : 0; }

        static void DrawJob(Graphics g, JobRow j, Palette p, float sc, float x0, float y, float inner, float rowH, DateTime now, Font fRow, Font fSub)
        {
            TimeSpan el = j.StartedUtc == DateTime.MinValue ? TimeSpan.Zero : now - j.StartedUtc;
            int lv = RunLevel(el);
            Color c = lv >= 2 ? p.Crit : lv == 1 ? p.Queue : p.Run;
            using (SolidBrush d = new SolidBrush(c)) g.FillEllipse(d, x0 + 2 * sc, y + 8 * sc, 7 * sc, 7 * sc);
            float tx = x0 + 16 * sc, tw = inner - 16 * sc;
            string elapsed = j.StartedUtc == DateTime.MinValue ? "" : Model.Elapsed(el);
            SizeF es = g.MeasureString(elapsed, fRow);
            Text(g, j.RepoShort + " · " + j.Workflow, fRow, p.Text, new RectangleF(tx, y + 1 * sc, tw - es.Width - 8 * sc, 18 * sc), StringAlignment.Near, StringAlignment.Center);
            Text(g, elapsed, fRow, lv == 0 ? p.Text : c, new RectangleF(tx + tw - es.Width - 4 * sc, y + 1 * sc, es.Width + 4 * sc, 18 * sc), StringAlignment.Far, StringAlignment.Center);
            List<string> parts = new List<string>();
            if (j.Job.Length > 0 && j.Job != j.Workflow) parts.Add(j.Job);
            string where = Model.Where(j); if (where.Length > 0) parts.Add(where);
            string step = Model.StepText(j); if (step.Length > 0) parts.Add(step);
            Text(g, string.Join(" · ", parts.ToArray()), fSub, p.SubText, new RectangleF(tx, y + 18 * sc, tw, 15 * sc), StringAlignment.Near, StringAlignment.Center);
            // Step progress: a thin bar under the row.
            RectangleF track = new RectangleF(tx, y + rowH - 5 * sc, tw, 2.5f * sc);
            using (GraphicsPath tp = Round(track, 1.25f * sc)) using (SolidBrush tb = new SolidBrush(p.Track)) g.FillPath(tb, tp);
            if (j.StepCount > 0 && j.StepIndex > 0)
            {
                float f = Math.Max(0.04f, Math.Min(1f, (j.StepIndex - 0.5f) / j.StepCount));
                RectangleF bar = new RectangleF(track.X, track.Y, track.Width * f, track.Height);
                using (GraphicsPath bp = Round(bar, 1.25f * sc)) using (SolidBrush bb = new SolidBrush(c)) g.FillPath(bb, bp);
            }
        }

        public static string CardAccessibleText(Snapshot s, DateTime now)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(Model.Count(s.RunningCount, "job running", "jobs running")).Append(", ").Append(Model.Count(s.QueuedCount, "run queued", "runs queued")).Append(". ");
            foreach (MachineView m in s.Machines)
            {
                if (m.Jobs.Count == 0) continue;
                sb.Append(m.Name).Append(": ");
                List<string> jobs = new List<string>();
                foreach (JobRow j in m.Jobs) jobs.Add(j.RepoShort + " " + j.Workflow + " " + Model.Elapsed(now - j.StartedUtc));
                sb.Append(string.Join("; ", jobs.ToArray())).Append(". ");
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ helpers
        static void Prep(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath path = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static void Shadow(Graphics g, RectangleF body, float rad, Palette p, float sc)
        {
            for (int i = 6; i >= 1; i--)
            {
                float grow = i * 1.6f * sc;
                RectangleF r = new RectangleF(body.X - grow, body.Y - grow + 2 * sc, body.Width + 2 * grow, body.Height + 2 * grow);
                using (GraphicsPath sp = Round(r, rad + grow))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(p.Dark ? 9 : 6, p.Shadow))) g.FillPath(sb, sp);
            }
        }

        static void Chip(Graphics g, string text, Font f, Color c, Palette p, RectangleF r, float sc)
        {
            using (GraphicsPath cp = Round(r, r.Height / 2))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(p.Dark ? 40 : 26, c))) g.FillPath(b, cp);
            Text(g, text, f, c, r, StringAlignment.Center, StringAlignment.Center);
        }

        public static void Text(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment h, StringAlignment v)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 1) return;
            using (StringFormat sf = new StringFormat(StringFormatFlags.NoWrap))
            using (SolidBrush b = new SolidBrush(c))
            {
                sf.Alignment = h; sf.LineAlignment = v; sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(s, f, b, r, sf);
            }
        }
    }
}
