using System;
using System.Reflection;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Roblox Images Fix")]
[assembly: AssemblyDescription("Диагностика и исправление загрузки картинок в Roblox")]
[assembly: AssemblyProduct("Roblox Images Fix")]
[assembly: AssemblyCopyright("MIT License")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace RobloxImagesFix
{
    static class Program
    {
        static bool IsAdmin()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        [STAThread]
        static void Main()
        {
            if (!IsAdmin())
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    Process.Start(psi);
                }
                catch (Exception)
                {
                    MessageBox.Show("Для смены DNS нужны права администратора. Запустите программу и подтвердите запрос Windows.",
                        "Roblox Images Fix");
                }
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class AdapterInfo
    {
        public string Guid;
        public string Name;
        public int Index;
    }

    class CheckResult
    {
        public bool DnsOk;
        public bool NetOk;
        public bool HostsOk;
        public bool DohChecked;
        public bool DohOk;
        public string NetDetail;
        public string DnsText;
        public string NetText;
        public string HostsText;
    }

    // Вся логика без интерфейса: DNS, проверки, резервная копия.
    static class Core
    {
        public static readonly string[] TestHosts = { "tr.rbxcdn.com", "t0.rbxcdn.com", "thumbnails.roblox.com" };

        public static readonly string[] ProviderNames =
        {
            "OpenDNS (208.67.222.222)",
            "Cloudflare (1.1.1.1)",
            "Google (8.8.8.8)"
        };

        public static readonly string[][] ProviderServers =
        {
            new string[] { "208.67.222.222", "208.67.220.220" },
            new string[] { "1.1.1.1", "1.0.0.1" },
            new string[] { "8.8.8.8", "8.8.4.4" }
        };

        static string BackupDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RobloxImagesFix"); }
        }

        static string BackupFile
        {
            get { return Path.Combine(BackupDir, "dns-backup.txt"); }
        }

        public static bool HasBackup()
        {
            return File.Exists(BackupFile);
        }

        // Физические адаптеры с включенным IP (Wi-Fi, Ethernet).
        public static List<AdapterInfo> GetAdapters()
        {
            List<AdapterInfo> list = new List<AdapterInfo>();
            using (ManagementObjectSearcher cfgSearcher = new ManagementObjectSearcher(
                "SELECT SettingID, InterfaceIndex FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE"))
            {
                foreach (ManagementObject cfg in cfgSearcher.Get())
                {
                    string guid = Convert.ToString(cfg["SettingID"]);
                    int index = Convert.ToInt32(cfg["InterfaceIndex"]);
                    using (ManagementObjectSearcher adSearcher = new ManagementObjectSearcher(
                        "SELECT NetConnectionID, PhysicalAdapter FROM Win32_NetworkAdapter WHERE InterfaceIndex = " + index))
                    {
                        foreach (ManagementObject ad in adSearcher.Get())
                        {
                            object phys = ad["PhysicalAdapter"];
                            if (phys == null || !Convert.ToBoolean(phys)) continue;
                            AdapterInfo info = new AdapterInfo();
                            info.Guid = guid;
                            info.Index = index;
                            info.Name = Convert.ToString(ad["NetConnectionID"]);
                            if (string.IsNullOrEmpty(info.Name)) info.Name = guid;
                            list.Add(info);
                            break;
                        }
                    }
                }
            }
            return list;
        }

        // Статически заданные DNS адаптера (пусто, если DNS получается автоматически).
        static string ReadStaticDns(string guid)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + guid))
            {
                if (key == null) return "";
                object v = key.GetValue("NameServer");
                if (v == null) return "";
                string[] parts = v.ToString().Split(new char[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return string.Join(",", parts);
            }
        }

        static string RunNetsh(string args)
        {
            ProcessStartInfo psi = new ProcessStartInfo("netsh.exe", args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Encoding enc = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
            using (Process p = Process.Start(psi))
            {
                string outText = p.StandardOutput.ReadToEnd();
                string errText = p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                if (p.ExitCode != 0)
                    throw new Exception("netsh: " + (outText + " " + errText).Trim());
                return outText;
            }
        }

        // servers == null означает "получать DNS автоматически". Адаптер задается индексом интерфейса.
        static void SetDns(int index, string[] servers)
        {
            string idx = index.ToString(CultureInfo.InvariantCulture);
            if (servers == null || servers.Length == 0)
            {
                RunNetsh("interface ipv4 set dnsservers name=" + idx + " source=dhcp");
                return;
            }
            RunNetsh("interface ipv4 set dnsservers name=" + idx + " source=static address=" + servers[0] + " validate=no");
            for (int i = 1; i < servers.Length; i++)
            {
                RunNetsh("interface ipv4 add dnsservers name=" + idx + " address=" + servers[i] +
                    " index=" + (i + 1).ToString(CultureInfo.InvariantCulture) + " validate=no");
            }
        }

        // Адреса шифрованного DNS (DoH) в том же порядке, что и ProviderNames.
        static readonly string[] DohTemplates =
        {
            "https://doh.opendns.com/dns-query",
            "https://cloudflare-dns.com/dns-query",
            "https://dns.google/dns-query"
        };

        // Встроенная поддержка DoH есть в Windows 11 (команда netsh dns).
        public static void EnableDoh(int provider, Action<string> log)
        {
            foreach (string ip in ProviderServers[provider])
            {
                try
                {
                    RunNetsh("dns add encryption server=" + ip + " dohtemplate=" + DohTemplates[provider] +
                        " autoupgrade=yes udpfallback=no");
                    log("Шифрованный DNS включён для " + ip);
                }
                catch (Exception ex)
                {
                    log("Шифрованный DNS для " + ip + " не включён: " + ex.Message);
                }
            }
            FlushDns();
        }

        static void RemoveDoh()
        {
            foreach (string[] list in ProviderServers)
            {
                foreach (string ip in list)
                {
                    try { RunNetsh("dns delete encryption server=" + ip); } catch (Exception) { }
                }
            }
        }

        // Ищет адрес через DoH напрямую, мимо системного DNS. Возвращает IPv4 или null.
        public static string DohResolve(string host, out bool reachable)
        {
            reachable = false;
            EnableModernTls();
            string[] urls =
            {
                "https://1.1.1.1/dns-query?name={0}&type=A",
                "https://8.8.8.8/resolve?name={0}&type=A"
            };
            foreach (string fmt in urls)
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(string.Format(fmt, host));
                    req.Accept = "application/dns-json";
                    req.Timeout = 6000;
                    req.UserAgent = "RobloxImagesFix";
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    {
                        string body = sr.ReadToEnd();
                        reachable = true;
                        Match m = Regex.Match(body, "\"type\"\\s*:\\s*1\\s*,\\s*\"TTL\"\\s*:\\s*\\d+\\s*,\\s*\"data\"\\s*:\\s*\"([0-9.]+)\"");
                        if (m.Success) return m.Groups[1].Value;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        public static void FlushDns()
        {
            ProcessStartInfo psi = new ProcessStartInfo("ipconfig.exe", "/flushdns");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(10000);
            }
        }

        public static void Apply(int provider, Action<string> log)
        {
            List<AdapterInfo> adapters = GetAdapters();
            if (adapters.Count == 0)
                throw new Exception("Не найдено активных сетевых адаптеров (Wi-Fi или Ethernet). Проверьте подключение к сети.");

            if (!File.Exists(BackupFile))
            {
                Directory.CreateDirectory(BackupDir);
                StringBuilder sb = new StringBuilder();
                foreach (AdapterInfo a in adapters)
                    sb.AppendLine(a.Guid + "|" + a.Name + "|" + ReadStaticDns(a.Guid));
                File.WriteAllText(BackupFile, sb.ToString(), Encoding.UTF8);
                log("Прежние настройки DNS сохранены.");
            }

            string[] servers = ProviderServers[provider];
            foreach (AdapterInfo a in adapters)
            {
                SetDns(a.Index, servers);
                log(a.Name + ": DNS " + string.Join(", ", servers));
            }

            FlushDns();
            log("Кэш DNS очищен.");
        }

        public static void Restore(Action<string> log)
        {
            if (!File.Exists(BackupFile))
                throw new Exception("Резервной копии нет, откатывать нечего.");

            Dictionary<string, int> indexByGuid = new Dictionary<string, int>();
            foreach (AdapterInfo a in GetAdapters()) indexByGuid[a.Guid] = a.Index;

            foreach (string line in File.ReadAllLines(BackupFile, Encoding.UTF8))
            {
                string[] parts = line.Split('|');
                if (parts.Length < 3) continue;
                int idx;
                if (!indexByGuid.TryGetValue(parts[0], out idx))
                {
                    log(parts[1] + ": адаптер сейчас не активен, пропускаю");
                    continue;
                }
                string[] servers = null;
                if (parts[2].Trim().Length > 0)
                    servers = parts[2].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                SetDns(idx, servers);
                if (servers == null) log(parts[1] + ": DNS снова автоматический");
                else log(parts[1] + ": DNS " + string.Join(", ", servers));
            }

            RemoveDoh();
            try { RemoveHostsEntry(null); log("Записи программы в файле hosts удалены."); }
            catch (Exception ex) { log("Файл hosts не изменён: " + ex.Message); }
            FlushDns();
            File.Delete(BackupFile);
            log("Откат выполнен, кэш DNS очищен.");
        }

        static void EnableModernTls()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); } // TLS 1.2 и 1.3
            catch (Exception)
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } // только TLS 1.2
                catch (Exception) { }
            }
        }

        static string TestConnection(string host, IPAddress ip)
        {
            EnableModernTls();
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(ip, 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000))
                        return "порт 443 не отвечает (таймаут)";
                    c.EndConnect(ar);
                }
            }
            catch (Exception ex)
            {
                return "порт 443 недоступен (" + ex.Message + ")";
            }

            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://" + host + "/");
                req.Timeout = 6000;
                req.UserAgent = "RobloxImagesFix";
                using (req.GetResponse()) { }
            }
            catch (WebException ex)
            {
                // Любой ответ сервера, даже 403 или 404, означает, что соединение работает.
                if (ex.Response != null)
                {
                    ex.Response.Close();
                    return null;
                }
                return "HTTPS не работает (" + ex.Status + ")";
            }
            catch (Exception ex)
            {
                return "HTTPS не работает (" + ex.Message + ")";
            }
            return null;
        }

        class HostResult
        {
            public IPAddress Ip;
            public string DnsError;
            public string NetProblem;
        }

        static IPAddress ResolveV4(string host, out string error)
        {
            error = null;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(host))
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork) return a;
                }
                error = "нет адреса IPv4";
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
            return null;
        }

        // Адреса из списка проверки, которые система сейчас не находит.
        public static List<string> FailedHosts()
        {
            bool[] ok = new bool[TestHosts.Length];
            Thread[] threads = new Thread[TestHosts.Length];
            for (int i = 0; i < TestHosts.Length; i++)
            {
                int k = i;
                threads[k] = new Thread(delegate ()
                {
                    string err;
                    ok[k] = ResolveV4(TestHosts[k], out err) != null;
                });
                threads[k].IsBackground = true;
                threads[k].Start();
            }
            foreach (Thread t in threads) t.Join(12000);
            List<string> failed = new List<string>();
            for (int i = 0; i < TestHosts.Length; i++)
            {
                if (!ok[i]) failed.Add(TestHosts[i]);
            }
            return failed;
        }

        public static bool DnsResolves()
        {
            return FailedHosts().Count == 0;
        }

        // ---------- Обход через файл hosts ----------

        const string HostsTag = "# RobloxImagesFix";

        // Известные адреса CloudFront, которые обслуживают rbxcdn.com.
        static readonly string[] CdnCandidates = { "18.165.140.59", "52.84.50.6", "52.84.50.45", "52.84.50.56", "52.84.50.69" };

        public static string HostsFile = null; // для тестов; обычно null

        static string HostsPath
        {
            get { return HostsFile ?? Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts"); }
        }

        // Отвечает ли адрес как сервер для этого имени: TLS с проверкой сертификата и HTTP-ответ.
        static bool ServerAnswersFor(string host, string ip)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect(IPAddress.Parse(ip), 443, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                    c.EndConnect(ar);
                    using (SslStream ssl = new SslStream(c.GetStream(), false))
                    {
                        ssl.ReadTimeout = 5000;
                        ssl.WriteTimeout = 5000;
                        ssl.AuthenticateAsClient(host, null, (SslProtocols)3072, false);
                        byte[] req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + host +
                            "\r\nUser-Agent: RobloxImagesFix\r\nConnection: close\r\n\r\n");
                        ssl.Write(req, 0, req.Length);
                        ssl.Flush();
                        byte[] buf = new byte[16];
                        int n = ssl.Read(buf, 0, buf.Length);
                        return n >= 5 && Encoding.ASCII.GetString(buf, 0, 5) == "HTTP/";
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string FindWorkingIp(string host, Action<string> log)
        {
            bool[] ok = new bool[CdnCandidates.Length];
            Thread[] threads = new Thread[CdnCandidates.Length];
            for (int i = 0; i < CdnCandidates.Length; i++)
            {
                int k = i;
                threads[k] = new Thread(delegate () { ok[k] = ServerAnswersFor(host, CdnCandidates[k]); });
                threads[k].IsBackground = true;
                threads[k].Start();
            }
            foreach (Thread t in threads) t.Join(15000);
            string found = null;
            for (int i = 0; i < CdnCandidates.Length; i++)
            {
                log(host + " через " + CdnCandidates[i] + ": " + (ok[i] ? "отвечает" : "нет ответа"));
                if (ok[i] && found == null) found = CdnCandidates[i];
            }
            return found;
        }

        public static void AddHostsEntry(string host, string ip)
        {
            RemoveHostsEntry(host);
            string text = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
            if (text.Length > 0 && !text.EndsWith("\n")) text += "\r\n";
            text += ip + " " + host + " " + HostsTag + "\r\n";
            File.WriteAllText(HostsPath, text, new UTF8Encoding(false));
        }

        // host == null: убрать все записи программы.
        public static void RemoveHostsEntry(string host)
        {
            if (!File.Exists(HostsPath)) return;
            List<string> keep = new List<string>();
            bool changed = false;
            foreach (string line in File.ReadAllLines(HostsPath))
            {
                if (line.Contains(HostsTag) && (host == null || line.Contains(host)))
                {
                    changed = true;
                    continue;
                }
                keep.Add(line);
            }
            if (changed) File.WriteAllLines(HostsPath, keep.ToArray(), new UTF8Encoding(false));
        }

        static HostResult CheckHost(string host)
        {
            HostResult r = new HostResult();
            string err;
            r.Ip = ResolveV4(host, out err);
            r.DnsError = err;
            if (r.Ip != null) r.NetProblem = TestConnection(host, r.Ip);
            return r;
        }

        public static CheckResult Check(Action<string> log)
        {
            CheckResult res = new CheckResult();

            // Все серверы проверяются одновременно, чтобы проверка не растягивалась.
            HostResult[] results = new HostResult[TestHosts.Length];
            Thread[] threads = new Thread[TestHosts.Length];
            for (int i = 0; i < TestHosts.Length; i++)
            {
                int k = i;
                threads[k] = new Thread(delegate () { results[k] = CheckHost(TestHosts[k]); });
                threads[k].IsBackground = true;
                threads[k].Start();
            }
            foreach (Thread t in threads) t.Join(25000);

            int dnsFailed = 0;
            int netFailed = 0;
            int resolvedCount = 0;
            for (int i = 0; i < TestHosts.Length; i++)
            {
                string h = TestHosts[i];
                HostResult r = results[i];
                if (r == null)
                {
                    dnsFailed++;
                    log(h + ": нет ответа за 25 секунд");
                    continue;
                }
                if (r.Ip == null)
                {
                    dnsFailed++;
                    log(h + ": адрес не найден (" + r.DnsError + ")");
                    continue;
                }
                resolvedCount++;
                log(h + ": адрес " + r.Ip);
                if (r.NetProblem == null)
                {
                    log(h + ": соединение работает");
                }
                else
                {
                    netFailed++;
                    log(h + ": " + r.NetProblem);
                    if (res.NetDetail == null) res.NetDetail = h + ": " + r.NetProblem;
                }
            }

            res.DnsOk = dnsFailed == 0;

            if (dnsFailed > 0)
            {
                string failedHost = null;
                for (int i = 0; i < TestHosts.Length; i++)
                {
                    if (results[i] == null || results[i].Ip == null) { failedHost = TestHosts[i]; break; }
                }
                res.DohChecked = true;
                bool dohReach = false;
                string dohIp = failedHost == null ? null : DohResolve(failedHost, out dohReach);
                res.DohOk = dohIp != null;
                if (dohIp != null)
                    log(failedHost + ": через шифрованный DNS адрес находится (" + dohIp + "), значит системный DNS подменяется");
                else if (dohReach)
                    log(failedHost + ": в шифрованном DNS у адреса тоже нет записи");
                else
                    log("Шифрованный DNS недоступен: его запросы, видимо, тоже блокируются");
            }
            res.DnsText = res.DnsOk ? "Все адреса находятся" : "Не находятся: " + dnsFailed + " из " + TestHosts.Length;

            if (resolvedCount == 0)
            {
                res.NetOk = false;
                res.NetText = "Не проверялось: адреса не найдены";
            }
            else
            {
                res.NetOk = netFailed == 0;
                res.NetText = res.NetOk ? "Соединение с серверами работает" : "Нет связи: " + netFailed + " из " + resolvedCount;
            }

            List<string> bad = new List<string>();
            try
            {
                string hostsPath = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
                foreach (string line in File.ReadAllLines(hostsPath))
                {
                    string t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    string l = t.ToLowerInvariant();
                    if (t.Contains(HostsTag)) continue;
                    if (l.Contains("roblox") || l.Contains("rbxcdn")) bad.Add(t);
                }
            }
            catch (Exception ex)
            {
                log("Файл hosts не прочитан: " + ex.Message);
            }
            res.HostsOk = bad.Count == 0;
            res.HostsText = res.HostsOk ? "Лишних записей нет" : "Найдены записи про Roblox";
            foreach (string b in bad) log("hosts: " + b);

            return res;
        }
    }

    enum IconMode { Idle, Checking, Ok, Warn, Bad }

    enum Stage { Checking, Ok, NeedFix, Blocked, HostsIssue, Fixing, Fixed, Error }

    static class Ui
    {
        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            float d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Большая скруглённая кнопка.
    class RoundButton : Control
    {
        public Color Surface = Color.FromArgb(18, 20, 26);
        Color back = Color.FromArgb(59, 130, 246);
        Color hoverBack = Color.FromArgb(96, 165, 250);
        Color pressBack = Color.FromArgb(37, 99, 235);
        bool hover;
        bool pressed;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                ControlStyles.Selectable | ControlStyles.StandardClick, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public void SetColors(Color normal, Color over, Color down)
        {
            back = normal;
            hoverBack = over;
            pressBack = down;
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; pressed = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            pressed = false;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Enabled && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space))
                OnClick(EventArgs.Empty);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color c = !Enabled ? Color.FromArgb(46, 50, 62) : (pressed ? pressBack : (hover ? hoverBack : back));
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            using (GraphicsPath path = Ui.RoundRect(r, 14))
            using (SolidBrush b = new SolidBrush(c))
            {
                g.FillPath(b, path);
            }

            if (Focused && Enabled)
            {
                RectangleF fr = new RectangleF(3.5f, 3.5f, Width - 8, Height - 8);
                using (GraphicsPath fp = Ui.RoundRect(fr, 11))
                using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 1.5f))
                {
                    g.DrawPath(pen, fp);
                }
            }

            Color fore = Enabled ? Color.White : Color.FromArgb(120, 126, 142);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    // Большой значок состояния: вращающееся кольцо, галочка, восклицательный знак или крестик.
    class StatusIcon : Control
    {
        public Color Surface = Color.FromArgb(18, 20, 26);
        IconMode mode = IconMode.Idle;
        float angle;

        public StatusIcon()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetMode(IconMode m)
        {
            mode = m;
            Invalidate();
        }

        public void Tick()
        {
            angle = (angle + 9f) % 360f;
            if (mode == IconMode.Checking) Invalidate();
        }

        static Color ModeColor(IconMode m)
        {
            switch (m)
            {
                case IconMode.Ok: return Color.FromArgb(34, 197, 94);
                case IconMode.Warn: return Color.FromArgb(245, 158, 11);
                case IconMode.Bad: return Color.FromArgb(239, 68, 68);
                case IconMode.Checking: return Color.FromArgb(59, 130, 246);
                default: return Color.FromArgb(146, 152, 168);
            }
        }

        static PointF P(float x, float y, float s)
        {
            return new PointF(x * s, y * s);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            float s = Math.Min(Width, Height);
            Color c = ModeColor(mode);
            RectangleF circle = new RectangleF(5, 5, s - 10, s - 10);

            using (SolidBrush fill = new SolidBrush(Color.FromArgb(34, c)))
            {
                g.FillEllipse(fill, circle);
            }

            if (mode == IconMode.Checking)
            {
                using (Pen track = new Pen(Color.FromArgb(55, c), s * 0.05f))
                {
                    g.DrawEllipse(track, circle);
                }
                using (Pen arc = new Pen(c, s * 0.05f))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, circle, angle, 100f);
                }
                return;
            }

            using (Pen ring = new Pen(Color.FromArgb(190, c), s * 0.03f))
            {
                g.DrawEllipse(ring, circle);
            }

            using (Pen pen = new Pen(c, s * 0.075f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (mode == IconMode.Ok)
                {
                    g.DrawLines(pen, new PointF[] { P(0.30f, 0.52f, s), P(0.44f, 0.66f, s), P(0.71f, 0.37f, s) });
                }
                else if (mode == IconMode.Bad)
                {
                    g.DrawLine(pen, P(0.36f, 0.36f, s), P(0.64f, 0.64f, s));
                    g.DrawLine(pen, P(0.64f, 0.36f, s), P(0.36f, 0.64f, s));
                }
                else if (mode == IconMode.Warn)
                {
                    g.DrawLine(pen, P(0.5f, 0.28f, s), P(0.5f, 0.56f, s));
                    float r = s * 0.04f;
                    using (SolidBrush b = new SolidBrush(c))
                    {
                        g.FillEllipse(b, s * 0.5f - r, s * 0.71f - r, r * 2, r * 2);
                    }
                }
            }
        }
    }

    // Текстовая ссылка с подчёркиванием при наведении.
    class TextLink : Label
    {
        readonly Font normalFont;
        readonly Font hoverFont;
        readonly Color normalColor;
        readonly Color hoverColor;

        public TextLink(string text, Font font, Color normal, Color hover)
        {
            normalFont = font;
            hoverFont = new Font(font, FontStyle.Underline);
            normalColor = normal;
            hoverColor = hover;
            Text = text;
            Font = normalFont;
            ForeColor = normal;
            BackColor = Color.Transparent;
            TextAlign = ContentAlignment.MiddleCenter;
            Cursor = Cursors.Hand;
            AutoSize = false;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            ForeColor = hoverColor;
            Font = hoverFont;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            ForeColor = normalColor;
            Font = normalFont;
        }
    }

    class MainForm : Form
    {
        static readonly Color BgColor = Color.FromArgb(18, 20, 26);
        static readonly Color CardColor = Color.FromArgb(28, 31, 40);
        static readonly Color TextColor = Color.FromArgb(236, 238, 244);
        static readonly Color MutedColor = Color.FromArgb(140, 146, 163);
        static readonly Color GoodColor = Color.FromArgb(34, 197, 94);
        static readonly Color WarnColor = Color.FromArgb(245, 158, 11);
        static readonly Color BadColor = Color.FromArgb(239, 68, 68);

        const int WinW = 460;
        const int CollapsedH = 430;
        const int DetailsH = 240;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        StatusIcon icon;
        Label headline;
        Label desc;
        RoundButton primary;
        TextLink recheck;
        TextLink toggle;
        TextLink restore;
        TextLink copyLog;
        Panel details;
        Panel[] dots = new Panel[3];
        Label[] rowText = new Label[3];
        ComboBox provider;
        TextBox logBox;
        System.Windows.Forms.Timer spin;
        Stage stage;
        bool busy;
        bool detailsOpen;

        public MainForm()
        {
            Text = "Roblox Images Fix";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(WinW, CollapsedH);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgColor;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9F);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            icon = new StatusIcon();
            icon.Surface = BgColor;
            icon.SetBounds((WinW - 96) / 2, 28, 96, 96);
            Controls.Add(icon);

            headline = MakeLabel(new Font("Segoe UI Semibold", 17F), TextColor, 30, 142, WinW - 60, 36);
            headline.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(headline);

            desc = MakeLabel(new Font("Segoe UI", 10F), MutedColor, 40, 184, WinW - 80, 92);
            desc.TextAlign = ContentAlignment.TopCenter;
            Controls.Add(desc);

            primary = new RoundButton();
            primary.Surface = BgColor;
            primary.Font = new Font("Segoe UI Semibold", 12F);
            primary.SetBounds(40, 288, WinW - 80, 54);
            primary.Click += delegate { OnPrimary(); };
            Controls.Add(primary);

            recheck = new TextLink("Проверить ещё раз", new Font("Segoe UI", 9.5F), MutedColor, TextColor);
            recheck.SetBounds(40, 350, WinW - 80, 26);
            recheck.Click += delegate { StartCheck(); };
            Controls.Add(recheck);

            toggle = new TextLink("Показать подробности", new Font("Segoe UI", 9.5F), MutedColor, TextColor);
            toggle.SetBounds(40, 388, WinW - 80, 26);
            toggle.Click += delegate { ToggleDetails(); };
            Controls.Add(toggle);

            BuildDetails();

            spin = new System.Windows.Forms.Timer();
            spin.Interval = 25;
            spin.Tick += delegate { icon.Tick(); };

            SetStage(Stage.Checking, null);
            UpdateRestore();

            Shown += delegate
            {
                busy = false;
                StartCheck();
            };
        }

        void BuildDetails()
        {
            details = new Panel();
            details.BackColor = CardColor;
            details.SetBounds(0, CollapsedH, WinW, DetailsH);
            details.Visible = false;
            Controls.Add(details);

            string[] titles = { "Поиск серверов", "Соединение", "Файл hosts" };
            for (int i = 0; i < 3; i++)
            {
                int y = 14 + i * 30;
                dots[i] = new Panel();
                dots[i].SetBounds(22, y + 6, 12, 12);
                dots[i].BackColor = MutedColor;
                GraphicsPath gp = new GraphicsPath();
                gp.AddEllipse(0, 0, 12, 12);
                dots[i].Region = new Region(gp);
                details.Controls.Add(dots[i]);

                Label t = MakeLabel(new Font("Segoe UI Semibold", 9.5F), TextColor, 44, y, 130, 24);
                t.Text = titles[i];
                t.TextAlign = ContentAlignment.MiddleLeft;
                details.Controls.Add(t);

                rowText[i] = MakeLabel(new Font("Segoe UI", 9.5F), MutedColor, 180, y, 264, 24);
                rowText[i].Text = "Ожидание";
                rowText[i].TextAlign = ContentAlignment.MiddleLeft;
                rowText[i].AutoEllipsis = true;
                details.Controls.Add(rowText[i]);
            }

            Label pl = MakeLabel(new Font("Segoe UI", 9.5F), MutedColor, 22, 110, 90, 26);
            pl.Text = "DNS-сервер";
            pl.TextAlign = ContentAlignment.MiddleLeft;
            details.Controls.Add(pl);

            provider = new ComboBox();
            provider.DropDownStyle = ComboBoxStyle.DropDownList;
            provider.FlatStyle = FlatStyle.Flat;
            provider.BackColor = BgColor;
            provider.ForeColor = TextColor;
            provider.Font = new Font("Segoe UI", 9.5F);
            provider.Items.AddRange(Core.ProviderNames);
            provider.SelectedIndex = 0;
            provider.SetBounds(112, 110, 200, 26);
            details.Controls.Add(provider);

            restore = new TextLink("Вернуть как было", new Font("Segoe UI", 9.5F), MutedColor, TextColor);
            restore.TextAlign = ContentAlignment.MiddleRight;
            restore.SetBounds(318, 110, 124, 26);
            restore.Click += delegate { OnRestore(); };
            details.Controls.Add(restore);

            logBox = new TextBox();
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.BorderStyle = BorderStyle.None;
            logBox.BackColor = BgColor;
            logBox.ForeColor = MutedColor;
            logBox.Font = new Font("Consolas", 8.5F);
            logBox.SetBounds(22, 148, 420, 58);
            details.Controls.Add(logBox);

            copyLog = new TextLink("Скопировать журнал", new Font("Segoe UI", 9.5F), MutedColor, TextColor);
            copyLog.SetBounds(22, 210, 420, 24);
            copyLog.Click += delegate
            {
                try
                {
                    if (logBox.Text.Length > 0) Clipboard.SetText(logBox.Text);
                    copyLog.Text = "Скопировано, можно вставить в сообщение";
                }
                catch (Exception) { copyLog.Text = "Не удалось скопировать"; }
            };
            details.Controls.Add(copyLog);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, 20, ref on, 4); // тёмная рамка окна (Windows 10 2004+ и 11)
            }
            catch (Exception) { }
        }

        static Label MakeLabel(Font font, Color color, int x, int y, int w, int h)
        {
            Label l = new Label();
            l.Font = font;
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.SetBounds(x, y, w, h);
            return l;
        }

        // ---------- Состояния экрана ----------

        void SetPrimary(string text, bool enabled, int palette)
        {
            primary.Text = text;
            primary.Enabled = enabled;
            if (palette == 0)
                primary.SetColors(Color.FromArgb(59, 130, 246), Color.FromArgb(96, 165, 250), Color.FromArgb(37, 99, 235));
            else if (palette == 1)
                primary.SetColors(Color.FromArgb(51, 56, 70), Color.FromArgb(66, 72, 90), Color.FromArgb(40, 44, 56));
            else
                primary.SetColors(Color.FromArgb(34, 160, 84), Color.FromArgb(46, 184, 100), Color.FromArgb(26, 130, 68));
        }

        void SetStage(Stage st, string extra)
        {
            stage = st;
            busy = (st == Stage.Checking || st == Stage.Fixing);
            spin.Enabled = busy;
            provider.Enabled = !busy;
            recheck.Visible = (st == Stage.NeedFix || st == Stage.HostsIssue || st == Stage.Fixed);
            headline.ForeColor = TextColor;

            switch (st)
            {
                case Stage.Checking:
                    icon.SetMode(IconMode.Checking);
                    headline.Text = "Проверяю подключение";
                    desc.Text = extra ?? "Смотрю, отвечают ли серверы с картинками Roblox. Это займёт несколько секунд.";
                    SetPrimary("Проверяю...", false, 1);
                    break;

                case Stage.Ok:
                    icon.SetMode(IconMode.Ok);
                    headline.Text = "Roblox доступен";
                    headline.ForeColor = GoodColor;
                    desc.Text = "Серверы с картинками отвечают. Если картинок всё равно нет, полностью закройте браузер и откройте заново. Если включён VPN, добавьте roblox.com и rbxcdn.com в его исключения.";
                    SetPrimary("Проверить ещё раз", true, 1);
                    break;

                case Stage.NeedFix:
                    icon.SetMode(IconMode.Warn);
                    headline.Text = "Картинки не загружаются";
                    headline.ForeColor = WarnColor;
                    desc.Text = extra ?? "Компьютер не находит серверы с картинками Roblox. Обычно это исправляется сменой настроек сети. Нажмите кнопку ниже, остальное программа сделает сама.";
                    SetPrimary("Исправить автоматически", true, 0);
                    break;

                case Stage.Blocked:
                    icon.SetMode(IconMode.Bad);
                    headline.Text = "Соединение блокируется";
                    headline.ForeColor = BadColor;
                    desc.Text = extra ?? "Серверы найдены, но не отвечают. Если включён VPN, отключите его или добавьте roblox.com и rbxcdn.com в исключения. Без VPN соединение может блокировать провайдер.";
                    SetPrimary("Проверить ещё раз", true, 1);
                    break;

                case Stage.HostsIssue:
                    icon.SetMode(IconMode.Warn);
                    headline.Text = "Мешает файл hosts";
                    headline.ForeColor = WarnColor;
                    desc.Text = "В системном файле hosts есть записи про Roblox, они могут ломать загрузку. Откройте файл, удалите строки со словами roblox или rbxcdn и сохраните его.";
                    SetPrimary("Открыть файл hosts", true, 0);
                    break;

                case Stage.Fixing:
                    icon.SetMode(IconMode.Checking);
                    headline.Text = "Применяю настройки";
                    desc.Text = extra ?? "Меняю настройки сети и очищаю кэш. Не закрывайте окно.";
                    SetPrimary("Подождите...", false, 1);
                    break;

                case Stage.Fixed:
                    icon.SetMode(IconMode.Ok);
                    headline.Text = "Готово, картинки вернутся";
                    headline.ForeColor = GoodColor;
                    desc.Text = "Полностью закройте браузер и Roblox и откройте заново. Повторять исправление не нужно: настройки сохраняются и после перезагрузки.";
                    SetPrimary("Готово", true, 2);
                    break;

                default:
                    icon.SetMode(IconMode.Bad);
                    headline.Text = "Не получилось";
                    headline.ForeColor = BadColor;
                    string msg = extra ?? "Что-то пошло не так.";
                    if (msg.Length > 150) msg = msg.Substring(0, 150) + "...";
                    desc.Text = msg + " Подробности есть в журнале: нажмите «Показать подробности».";
                    SetPrimary("Попробовать снова", true, 1);
                    break;
            }
        }

        void UpdateRestore()
        {
            restore.Visible = Core.HasBackup();
        }

        void ToggleDetails()
        {
            detailsOpen = !detailsOpen;
            details.Visible = detailsOpen;
            float scale;
            using (Graphics g = CreateGraphics()) { scale = g.DpiX / 96f; }
            int h = CollapsedH + (detailsOpen ? DetailsH : 0);
            ClientSize = new Size(ClientSize.Width, (int)Math.Round(h * scale));
            toggle.Text = detailsOpen ? "Скрыть подробности" : "Показать подробности";
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
        }

        // ---------- Действия ----------

        void OnPrimary()
        {
            switch (stage)
            {
                case Stage.NeedFix: StartFix(); break;
                case Stage.HostsIssue: OpenHosts(); break;
                case Stage.Fixed: Close(); break;
                case Stage.Ok:
                case Stage.Blocked:
                case Stage.Error: StartCheck(); break;
            }
        }

        void OpenHosts()
        {
            try
            {
                string path = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Log("Не удалось открыть hosts: " + ex.Message);
            }
        }

        void OnRestore()
        {
            if (busy) return;
            DialogResult r = MessageBox.Show(this,
                "Вернуть прежние настройки DNS? После этого картинки в Roblox могут снова перестать загружаться.",
                "Roblox Images Fix", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) StartRestore();
        }

        void UiInvoke(Action a)
        {
            if (IsDisposed) return;
            try { BeginInvoke(a); } catch (Exception) { }
        }

        void Log(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(Log), text); } catch (Exception) { }
                return;
            }
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + "\r\n");
        }

        void RunAsync(Action work)
        {
            Thread t = new Thread(delegate ()
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log("Ошибка: " + ex.Message);
                    string m = ex.Message;
                    UiInvoke(delegate { SetStage(Stage.Error, m); UpdateRestore(); });
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void StartCheck()
        {
            if (busy) return;
            SetStage(Stage.Checking, null);
            RunAsync(delegate
            {
                Log("Проверяю серверы Roblox...");
                CheckResult r = Core.Check(Log);
                UiInvoke(delegate { ShowResult(r, false); });
            });
        }

        void StartFix()
        {
            if (busy) return;
            int idx = provider.SelectedIndex;
            SetStage(Stage.Fixing, null);
            RunAsync(delegate
            {
                int n = Core.ProviderNames.Length;
                bool resolved = false;
                for (int step = 0; step < n && !resolved; step++)
                {
                    int pi = (idx + step) % n;
                    Log("Применяю DNS: " + Core.ProviderNames[pi]);
                    Core.Apply(pi, Log);
                    Thread.Sleep(1500);
                    resolved = Core.DnsResolves();
                    if (!resolved)
                    {
                        Log("Адреса не находятся, включаю шифрованный DNS...");
                        Core.EnableDoh(pi, Log);
                        Thread.Sleep(2000);
                        resolved = Core.DnsResolves();
                    }
                    if (!resolved) Log("С этим DNS адреса не находятся, пробую следующий.");
                }
                if (!resolved)
                {
                    Log("Ни один DNS не находит адрес. Пробую обходной путь через файл hosts...");
                    foreach (string h in Core.FailedHosts())
                    {
                        if (!h.EndsWith(".rbxcdn.com")) continue;
                        string ip = Core.FindWorkingIp(h, Log);
                        if (ip == null)
                        {
                            Log(h + ": подходящий сервер не отвечает, в hosts ничего не записываю.");
                            continue;
                        }
                        try
                        {
                            Core.AddHostsEntry(h, ip);
                            Log(h + ": записан в файл hosts (" + ip + ")");
                        }
                        catch (Exception ex)
                        {
                            Log("Файл hosts не изменён: " + ex.Message);
                        }
                    }
                    Core.FlushDns();
                    Thread.Sleep(800);
                }
                Log("Проверяю результат...");
                CheckResult r = Core.Check(Log);
                UiInvoke(delegate { ShowResult(r, true); });
            });
        }

        void StartRestore()
        {
            if (busy) return;
            SetStage(Stage.Fixing, "Возвращаю прежние настройки сети. Не закрывайте окно.");
            RunAsync(delegate
            {
                Log("Возвращаю прежние настройки DNS...");
                Core.Restore(Log);
                Thread.Sleep(1500);
                CheckResult r = Core.Check(Log);
                UiInvoke(delegate { ShowResult(r, false); });
            });
        }

        void ShowResult(CheckResult r, bool afterFix)
        {
            dots[0].BackColor = r.DnsOk ? GoodColor : BadColor;
            dots[1].BackColor = r.NetOk ? GoodColor : BadColor;
            dots[2].BackColor = r.HostsOk ? GoodColor : WarnColor;
            rowText[0].Text = r.DnsText;
            rowText[1].Text = r.NetText;
            rowText[2].Text = r.HostsText;
            UpdateRestore();

            if (r.DnsOk && r.NetOk && r.HostsOk)
                SetStage(afterFix ? Stage.Fixed : Stage.Ok, null);
            else if (!r.DnsOk)
            {
                if (afterFix)
                    SetStage(Stage.Blocked, "Не помогли ни смена DNS, ни шифрованный DNS, ни запись в hosts. Скорее всего, этот адрес блокирует провайдер: поможет VPN на весь компьютер или другой интернет, например раздача с телефона.");
                else
                    SetStage(Stage.NeedFix, "Компьютер не находит адрес картинок tr.rbxcdn.com: ответы DNS, похоже, подменяются. Кнопка попробует сменить DNS, включить шифрованный DNS и записать адрес в файл hosts.");
            }
            else if (!r.NetOk)
            {
                string d = r.NetDetail ?? "соединение не устанавливается";
                if (d.Length > 95) d = d.Substring(0, 95) + "...";
                SetStage(Stage.Blocked, "Адреса находятся, но соединение не устанавливается. " + d +
                    ". Это похоже на блокировку на стороне провайдера. Если включён VPN, отключите его.");
            }
            else
                SetStage(Stage.HostsIssue, null);
        }
    }
}
