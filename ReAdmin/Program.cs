using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ReAdmin
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (Array.Exists(args, a => string.Equals(a, "--kurscheck", StringComparison.OrdinalIgnoreCase)))
            {
                KursCheck.Run();
                return;
            }
            Application.Run(new MainForm());
        }
    }

    // Gemeinsamer Speicher: welche Meldungen schon gezeigt wurden (damit höchstens eine pro Tag kommt)
    static class Meldung
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "re admin");
        static string StateFile { get { return Path.Combine(Dir, "meldungen.txt"); } }

        public static bool SchonGezeigt(string key)
        {
            try { return File.Exists(StateFile) && Array.IndexOf(File.ReadAllLines(StateFile), key) >= 0; }
            catch { return false; }
        }

        public static void Merken(string key)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var l = new List<string>(File.Exists(StateFile) ? File.ReadAllLines(StateFile) : new string[0]);
                l.Add(key);
                if (l.Count > 60) l.RemoveRange(0, l.Count - 60);
                File.WriteAllLines(StateFile, l.ToArray());
            }
            catch { }
        }

        public static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Information; }
        }
    }

    // Täglicher Kurs-Check (Windows-Aufgabenplanung startet "ReAdmin.exe --kurscheck")
    static class KursCheck
    {
        public const string TaskName = "re admin Kurscheck";
        static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static Dictionary<string, object> ReadJson(string file)
        {
            return json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8));
        }

        // Meldebereich aus daten.json im re-admin-Ordner (Standard 0.93 – 0.95)
        static bool Einstellungen(out bool aktiv, out double min, out double max)
        {
            aktiv = true; min = 0.93; max = 0.95;
            try
            {
                var cfg = ReadJson(Path.Combine(Meldung.Dir, "config.json"));
                object f;
                if (!cfg.TryGetValue("folder", out f)) return true;
                string daten = Path.Combine((string)f, "daten.json");
                if (!File.Exists(daten)) return true;
                var d = ReadJson(daten);
                object st, ka;
                var s = d.TryGetValue("settings", out st) ? st as Dictionary<string, object> : null;
                var a = s != null && s.TryGetValue("kursAlarm", out ka) ? ka as Dictionary<string, object> : null;
                if (a == null) return true;
                object v;
                if (a.TryGetValue("aktiv", out v) && v is bool) aktiv = (bool)v;
                if (a.TryGetValue("min", out v) && v != null) min = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                if (a.TryGetValue("max", out v) && v != null) max = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                if (min > max) { var t = min; min = max; max = t; }
            }
            catch { }
            return true;
        }

        static double KursLaden()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            string[] urls = { "https://api.frankfurter.dev/v1/latest?symbols=CHF", "https://api.frankfurter.app/latest?from=EUR&to=CHF" };
            for (int versuch = 0; versuch < 6; versuch++)
            {
                foreach (var u in urls)
                {
                    try
                    {
                        using (var wc = new WebClient())
                        {
                            wc.Encoding = Encoding.UTF8;
                            wc.Headers[HttpRequestHeader.UserAgent] = "re admin";
                            var d = json.Deserialize<Dictionary<string, object>>(wc.DownloadString(u));
                            var r = d["rates"] as Dictionary<string, object>;
                            double k = Convert.ToDouble(r["CHF"], System.Globalization.CultureInfo.InvariantCulture);
                            if (k > 0) return k;
                        }
                    }
                    catch { }
                }
                System.Threading.Thread.Sleep(60000); // z. B. direkt nach dem Hochfahren noch kein Netz
            }
            return 0;
        }

        public static void Run()
        {
            bool aktiv; double min, max;
            Einstellungen(out aktiv, out min, out max);
            if (!aktiv) return;
            string key = "kurs-" + DateTime.Now.ToString("yyyy-MM-dd");
            if (Meldung.SchonGezeigt(key)) return;
            double k = KursLaden();
            if (k <= 0 || k < min || k > max) return;
            Meldung.Merken(key);
            var ci = new System.Globalization.CultureInfo("de-CH");
            using (var tray = new NotifyIcon { Icon = Meldung.AppIcon(), Text = "re admin – Wechselkurs", Visible = true })
            {
                var timer = new Timer { Interval = 180000 };
                timer.Tick += (s, e) => Application.Exit();
                tray.BalloonTipClicked += (s, e) =>
                {
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true }); } catch { }
                    Application.Exit();
                };
                tray.Click += (s, e) => { try { Process.Start(Application.ExecutablePath); } catch { } Application.Exit(); };
                tray.ShowBalloonTip(30000, "re admin – Wechselkurs",
                    "1 EUR = CHF " + k.ToString("0.0000", ci) + " liegt im Meldebereich " + min.ToString("0.000", ci) + " – " + max.ToString("0.000", ci) +
                    ". Guter Moment, vom Euro- aufs Frankenkonto umzubuchen.", ToolTipIcon.Info);
                timer.Start();
                Application.Run();
                tray.Visible = false;
            }
        }

        // Legt die tägliche Aufgabe (9 Uhr, bei verpasstem Termin sobald möglich) für den angemeldeten Benutzer an
        public static void AufgabeEinrichten()
        {
            try
            {
                string exe = Application.ExecutablePath;
                string mark = Path.Combine(Meldung.Dir, "kurscheck-aufgabe.txt");
                if (File.Exists(mark) && File.ReadAllText(mark) == exe) return;
                string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                    "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                    "<RegistrationInfo><Description>re admin: prüft täglich den Wechselkurs EUR/CHF und meldet, wenn er im Meldebereich liegt.</Description></RegistrationInfo>" +
                    "<Triggers><CalendarTrigger><StartBoundary>2026-01-01T09:00:00</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger></Triggers>" +
                    "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
                    "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                    "<StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT15M</ExecutionTimeLimit><Enabled>true</Enabled></Settings>" +
                    "<Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command><Arguments>--kurscheck</Arguments></Exec></Actions></Task>";
                Directory.CreateDirectory(Meldung.Dir);
                string tmp = Path.Combine(Path.GetTempPath(), "re-admin-kurscheck.xml");
                File.WriteAllText(tmp, xml, Encoding.Unicode);
                var p = Process.Start(new ProcessStartInfo("schtasks.exe", "/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                p.WaitForExit(20000);
                if (p.ExitCode == 0) File.WriteAllText(mark, exe);
                try { File.Delete(tmp); } catch { }
            }
            catch { }
        }
    }

    // Hauptfenster: zeigt Admin.html aus dem OneDrive-Ordner in WebView2 an
    // und erledigt für die App das Lesen und Schreiben von Dateien.
    public class MainForm : Form
    {
        const string Host = "readmin.app";
        readonly WebView2 web;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        readonly string cfgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "re admin");
        string folder;
        NotifyIcon tray;

        void ShowBalloon(string title, string text)
        {
            if (tray == null)
            {
                tray = new NotifyIcon { Icon = Meldung.AppIcon(), Text = "re admin" };
                tray.BalloonTipClicked += (s, e) => { if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); };
                FormClosed += (s, e) => { tray.Visible = false; tray.Dispose(); };
            }
            tray.Visible = true;
            tray.ShowBalloonTip(20000, title, text, ToolTipIcon.Info);
        }

        public MainForm()
        {
            Text = "re admin";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Size = new Size(1440, 920);
            MinimumSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;
            web = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(web);
            Load += async (s, e) => await Start();
        }

        string ConfigFile { get { return Path.Combine(cfgDir, "config.json"); } }

        string LoadFolder()
        {
            try
            {
                if (!File.Exists(ConfigFile)) return null;
                var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(ConfigFile, Encoding.UTF8));
                object f;
                return d != null && d.TryGetValue("folder", out f) ? f as string : null;
            }
            catch { return null; }
        }

        void SaveFolder(string f)
        {
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(ConfigFile, json.Serialize(new Dictionary<string, object> { { "folder", f } }), Encoding.UTF8);
        }

        string PickFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "OneDrive-Ordner von re admin wählen (dort liegen daten.json und Admin.html)";
                dlg.ShowNewFolderButton = true;
                string od = Environment.GetEnvironmentVariable("OneDriveCommercial");
                if (string.IsNullOrEmpty(od)) od = Environment.GetEnvironmentVariable("OneDrive");
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) dlg.SelectedPath = folder;
                else if (!string.IsNullOrEmpty(od) && Directory.Exists(od)) dlg.SelectedPath = od;
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                SaveFolder(dlg.SelectedPath);
                return dlg.SelectedPath;
            }
        }

        static string EmbeddedHtml()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Admin.html"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static string VersionOf(string html)
        {
            var m = Regex.Match(html ?? "", "\"version\"\\s*:\\s*\"([0-9.]+)\"");
            return m.Success ? m.Groups[1].Value : "0";
        }

        static int CompareVersion(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = 0, y = 0;
                if (i < pa.Length) int.TryParse(pa[i], out x);
                if (i < pb.Length) int.TryParse(pb[i], out y);
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        // Legt Admin.html in den Ordner. Ist die mitgelieferte Version neuer, wird sie ersetzt
        // (die alte Version kommt nach Update\Alt).
        void EnsureHtml()
        {
            string target = Path.Combine(folder, "Admin.html");
            string emb = EmbeddedHtml();
            if (!File.Exists(target))
            {
                File.WriteAllText(target, emb, new UTF8Encoding(false));
                return;
            }
            string cur = File.ReadAllText(target, Encoding.UTF8);
            if (CompareVersion(VersionOf(emb), VersionOf(cur)) > 0)
            {
                string alt = Path.Combine(folder, "Update", "Alt");
                Directory.CreateDirectory(alt);
                File.Copy(target, Path.Combine(alt, "Admin_v" + VersionOf(cur) + "_" + DateTime.Now.ToString("yyyy-MM-dd") + ".html"), true);
                File.WriteAllText(target, emb, new UTF8Encoding(false));
            }
        }

        async Task Start()
        {
            folder = LoadFolder();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show(this,
                    "Willkommen bei re admin.\n\nWähle jetzt den Ordner in deinem OneDrive, in dem re admin seine Daten speichert " +
                    "(den bisherigen Ordner mit daten.json) oder einen neuen, leeren Ordner.",
                    "re admin", MessageBoxButtons.OK, MessageBoxIcon.Information);
                folder = PickFolder();
                if (folder == null) { Close(); return; }
            }
            Task.Run(() => KursCheck.AufgabeEinrichten());
            try { EnsureHtml(); }
            catch (Exception ex) { MessageBox.Show(this, "Admin.html konnte nicht in den Ordner geschrieben werden:\n" + ex.Message, "re admin"); }

            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "re admin", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, data);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                if (MessageBox.Show(this, "re admin braucht die Microsoft Edge WebView2-Laufzeit. Download-Seite jetzt öffnen?",
                        "re admin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    OpenExternal("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                Close();
                return;
            }

            var cw = web.CoreWebView2;
            cw.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
            cw.WebMessageReceived += OnMessage;
            cw.PermissionRequested += (s, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Camera) e.State = CoreWebView2PermissionState.Allow;
            };
            cw.NewWindowRequested += (s, e) => { e.Handled = true; OpenExternal(e.Uri); };
            cw.DocumentTitleChanged += (s, e) => { Text = cw.DocumentTitle; };
            cw.Settings.IsStatusBarEnabled = false;
            Navigate();
        }

        void Navigate()
        {
            web.CoreWebView2.Navigate("https://" + Host + "/Admin.html?t=" + DateTime.Now.Ticks);
        }

        static void OpenExternal(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return;
            if (uri.StartsWith("http://") || uri.StartsWith("https://") || uri.StartsWith("mailto:"))
            {
                try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
            }
        }

        static object Get(Dictionary<string, object> m, string key)
        {
            object v;
            return m.TryGetValue(key, out v) ? v : null;
        }

        // Pfad aus der App (Liste von Ordnernamen) sicher in einen Pfad im re-admin-Ordner umwandeln
        string FullPath(object pathObj)
        {
            string full = folder;
            var parts = new List<string>();
            if (pathObj is string) parts.Add((string)pathObj);
            else
            {
                var en = pathObj as IEnumerable;
                if (en != null) foreach (var p in en) parts.Add(Convert.ToString(p));
            }
            foreach (var p in parts)
            {
                if (string.IsNullOrEmpty(p) || p == "." || p == ".." || p.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new Exception("Ungültiger Pfad: " + p);
                full = Path.Combine(full, p);
            }
            string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
            string res = Path.GetFullPath(full);
            if (!(res + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Pfad ausserhalb des Ordners");
            return res;
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch { return; }
            if (m == null) return;
            object id = Get(m, "id");
            object result = null;
            string error = null;
            try
            {
                string cmd = Convert.ToString(Get(m, "cmd"));
                switch (cmd)
                {
                    case "info":
                        result = new Dictionary<string, object> {
                            { "folder", folder },
                            { "name", new DirectoryInfo(folder).Name },
                            { "exe", Application.ProductVersion } };
                        break;
                    case "list":
                    {
                        string d = FullPath(Get(m, "path"));
                        Directory.CreateDirectory(d);
                        var names = new List<string>();
                        foreach (var f in Directory.GetFiles(d))
                        {
                            string n = Path.GetFileName(f);
                            if (!n.EndsWith(".tmp~")) names.Add(n);
                        }
                        result = names;
                        break;
                    }
                    case "read":
                    {
                        string f = FullPath(Get(m, "path"));
                        if (File.Exists(f))
                        {
                            long ms = (long)(File.GetLastWriteTimeUtc(f) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                            result = new Dictionary<string, object> { { "b64", Convert.ToBase64String(File.ReadAllBytes(f)) }, { "mtime", ms } };
                        }
                        break;
                    }
                    case "write":
                    {
                        string f = FullPath(Get(m, "path"));
                        Directory.CreateDirectory(Path.GetDirectoryName(f));
                        byte[] bytes = Convert.FromBase64String(Convert.ToString(Get(m, "b64")) ?? "");
                        string tmp = f + ".tmp~";
                        File.WriteAllBytes(tmp, bytes);
                        if (File.Exists(f)) File.Delete(f);
                        File.Move(tmp, f);
                        result = true;
                        break;
                    }
                    case "remove":
                    {
                        string f = FullPath(Get(m, "path"));
                        if (File.Exists(f)) File.Delete(f);
                        result = true;
                        break;
                    }
                    case "openPdf":
                    {
                        string dir = Path.Combine(Path.GetTempPath(), "re admin");
                        Directory.CreateDirectory(dir);
                        string name = Regex.Replace(Convert.ToString(Get(m, "name")) ?? "Dokument.pdf", "[\\\\/:*?\"<>|]", "_");
                        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
                        string f = Path.Combine(dir, name);
                        File.WriteAllBytes(f, Convert.FromBase64String(Convert.ToString(Get(m, "b64")) ?? ""));
                        Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
                        result = true;
                        break;
                    }
                    case "notify":
                    {
                        string key = Convert.ToString(Get(m, "key"));
                        if (!string.IsNullOrEmpty(key) && Meldung.SchonGezeigt(key)) { result = false; break; }
                        if (!string.IsNullOrEmpty(key)) Meldung.Merken(key);
                        ShowBalloon(Convert.ToString(Get(m, "title")) ?? "re admin", Convert.ToString(Get(m, "text")) ?? "");
                        result = true;
                        break;
                    }
                    case "openFolder":
                        Process.Start("explorer.exe", "\"" + folder + "\"");
                        result = true;
                        break;
                    case "pickFolder":
                    {
                        string f = PickFolder();
                        if (f != null)
                        {
                            folder = f;
                            EnsureHtml();
                            web.CoreWebView2.ClearVirtualHostNameToFolderMapping(Host);
                            web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
                            BeginInvoke(new Action(Navigate));
                        }
                        result = f != null;
                        break;
                    }
                    default:
                        throw new Exception("Unbekannter Befehl: " + cmd);
                }
            }
            catch (Exception ex) { error = ex.Message; }
            var resp = new Dictionary<string, object> { { "id", id }, { "ok", error == null }, { "result", result }, { "error", error } };
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(resp));
        }
    }
}
