// ============================================================
// 番茄钟 TomatoPomo v2 —— B站白噪音番茄钟桌面小插件
// 新增:
//   · 本地播放列表管理(增/删/上移/下移/批量编辑),保存到本地,可直接播放
//   · 收藏夹一键导入(含标题/时长),支持 Cookie 导入私有收藏夹
//   · 播放器最小化静默启动,不再弹窗打扰
//   · 阶段切换模式:默认到点停止;可选"音乐连续播放,下阶段开始时换下一支"
//   · 顺序/随机播放、避免连续重复
// ============================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("番茄钟")]
[assembly: System.Reflection.AssemblyProduct("番茄钟 TomatoPomo")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace TomatoPomo
{
    // ---------------- 配置与统计 ----------------
    public class Config
    {
        public int workMin { get; set; }
        public int restMin { get; set; }
        public bool autoRest { get; set; }        // 专注结束自动休息
        public bool autoNext { get; set; }        // 休息结束自动开始下一轮
        public bool playDuringBreak { get; set; } // 休息时播放音乐
        public bool continuous { get; set; }      // 到点不停止音乐,下阶段开始时切换下一支
        public bool sequential { get; set; }      // 顺序播放(不随机)
        public bool sound { get; set; }           // 到点提示音(默认关)
        public bool topMost { get; set; }
        public string favInput { get; set; }      // 收藏夹链接/ID
        public string sessdata { get; set; }      // Cookie(可选,私有收藏夹用)
        public string lastToken { get; set; }
        public List<string> recent { get; set; }  // 最近播过的token,避免重复
        public int listIndex { get; set; }        // 顺序播放位置
        public int x { get; set; }
        public int y { get; set; }

        public Config()
        {
            workMin = 25; restMin = 5;
            autoRest = true; autoNext = true; playDuringBreak = false;
            continuous = false; sequential = false; sound = false; topMost = true;
            favInput = ""; sessdata = ""; lastToken = "";
            recent = new List<string>();
            listIndex = 0;
            x = -1; y = -1;
        }
    }

    public class Stats
    {
        public Dictionary<string, int> Days { get; set; }
        public Stats() { Days = new Dictionary<string, int>(); }
    }

    public class ListEntry
    {
        public string Token { get; set; }
        public string Title { get; set; }
        public int Duration { get; set; }
    }

    public class VideoList
    {
        public List<ListEntry> Items { get; set; }
        public VideoList() { Items = new List<ListEntry>(); }
    }

    public static class Store
    {
        public static string Dir()
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TomatoPomo");
            Directory.CreateDirectory(d);
            return d;
        }
        static JavaScriptSerializer J()
        {
            var s = new JavaScriptSerializer();
            s.MaxJsonLength = int.MaxValue;
            return s;
        }
        public static Config LoadConfig()
        {
            try
            {
                string p = Path.Combine(Dir(), "config.json");
                if (File.Exists(p))
                {
                    var c = J().Deserialize<Config>(File.ReadAllText(p, Encoding.UTF8));
                    if (c != null)
                    {
                        if (c.recent == null) c.recent = new List<string>();
                        return c;
                    }
                }
            }
            catch { }
            return new Config();
        }
        public static void SaveConfig(Config c)
        {
            try { File.WriteAllText(Path.Combine(Dir(), "config.json"), J().Serialize(c), Encoding.UTF8); } catch { }
        }
        public static Stats LoadStats()
        {
            var s = new Stats();
            try
            {
                string p = Path.Combine(Dir(), "stats.json");
                if (File.Exists(p))
                {
                    var d = J().Deserialize<Stats>(File.ReadAllText(p, Encoding.UTF8));
                    if (d != null && d.Days != null) return d;
                }
            }
            catch { }
            return s;
        }
        public static void SaveStats(Stats s)
        {
            try { File.WriteAllText(Path.Combine(Dir(), "stats.json"), J().Serialize(s), Encoding.UTF8); } catch { }
        }
        public static VideoList LoadList()
        {
            try
            {
                string p = Path.Combine(Dir(), "list.json");
                if (File.Exists(p))
                {
                    var d = J().Deserialize<VideoList>(File.ReadAllText(p, Encoding.UTF8));
                    if (d != null && d.Items != null) return d;
                }
            }
            catch { }
            return null;
        }
        public static void SaveList(VideoList l)
        {
            try { File.WriteAllText(Path.Combine(Dir(), "list.json"), J().Serialize(l), Encoding.UTF8); } catch { }
        }
    }

    // ---------------- B站接口 ----------------
    public class BiliException : Exception
    {
        public BiliException(string m) : base(m) { }
    }

    public class FavItem { public string Token; public string Title; public int Duration; }

    public class PickResult { public string Url; public string Title; public string Warn; }

    public class VidInfo { public bool Ok; public string Title; public int Pages; public string Bvid; public long Aid; public int Duration; }

    public static class Bili
    {
        // 注意:短UA可通过B站风控;完整ChromeUA会触发HTTP 412
        public const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126";

        // 内置默认列表:全部为纯环境音(雨声/篝火/海浪),无人声无音乐
        public static List<ListEntry> DefaultList()
        {
            var l = new List<ListEntry>();
            string[] defs = new string[] {
                "BV1LV411o7Cx", "BV1ad4y1c7ki", "BV1oHBsBZEDQ", "BV1D7411n7TQ",
                "BV1eDgW6QEMo", "BV1C1s1z1Ea5", "BV1Xv411a7jZ", "BV1WzW2z1EAC",
                "BV1mtxp6kEpr", "BV11ysszdETe", "BV1nTnrzHEmE"
            };
            foreach (string t in defs)
                l.Add(new ListEntry { Token = t, Title = "", Duration = 0 });
            return l;
        }

        static Random rnd = new Random();
        static Dictionary<string, object[]> favCache = new Dictionary<string, object[]>();
        static string wbiKey = null;
        static DateTime wbiKeyTime = DateTime.MinValue;
        // 整个会话固定一个 buvid3(模拟同一浏览器会话,避免风控)
        static readonly string buvid = Guid.NewGuid().ToString("D").ToUpper() + "infoc";

        static int[] WBI_TAB = new int[] {
            46,47,18,2,53,8,23,32,15,50,10,31,49,19,26,35,
            40,10,34,29,24,26,13,38,20,17,7,45,36,11,48,9,
            22,4,24,44,27,37,10,4,6,36,37,25,29,11,26,7,
            37,28,34,3,16,4,25,46,11,2,17,2,42,3,21,15 };

        // ---------- 基础 ----------
        static void Tls()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)0xC00; } catch { }
        }
        static string UnixSec()
        {
            return ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString();
        }
        public static string NormSess(string s)
        {
            if (s == null) return "";
            s = s.Trim();
            if (s.Length == 0) return "";
            if (s.IndexOf("SESSDATA=", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int i = s.IndexOf("SESSDATA=", StringComparison.OrdinalIgnoreCase);
                s = s.Substring(i + 9);
                int j = s.IndexOf(';');
                if (j >= 0) s = s.Substring(0, j);
                return "SESSDATA=" + s.Trim();
            }
            return "SESSDATA=" + s;
        }
        public static string HttpGet(string url, string referer)
        {
            return HttpGet(url, referer, "");
        }
        public static string HttpGet(string url, string referer, string sessdata)
        {
            Tls();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UA;
            req.Accept = "application/json, text/plain, */*";
            req.Referer = string.IsNullOrEmpty(referer) ? "https://www.bilibili.com/" : referer;
            string ck = "buvid3=" + buvid + "; b_nut=" + UnixSec();
            string sd = NormSess(sessdata);
            if (sd.Length > 0) ck += "; " + sd;
            req.Headers.Add(HttpRequestHeader.Cookie, ck);
            req.Timeout = 8000;
            req.ReadWriteTimeout = 8000;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }
        public static Dictionary<string, object> Json(string url, string referer)
        {
            string s = HttpGet(url, referer);
            var jss = new JavaScriptSerializer();
            jss.MaxJsonLength = int.MaxValue;
            return jss.Deserialize<Dictionary<string, object>>(s);
        }
        public static Dictionary<string, object> ParseJson(string s)
        {
            var jss = new JavaScriptSerializer();
            jss.MaxJsonLength = int.MaxValue;
            return jss.Deserialize<Dictionary<string, object>>(s);
        }
        public static int ToInt(object o) { try { return Convert.ToInt32(o == null ? 0 : o); } catch { return 0; } }
        public static long ToLong(object o) { try { return Convert.ToInt64(o == null ? 0 : o); } catch { return 0; } }
        public static string ToStr(object o) { return o == null ? "" : o.ToString(); }
        public static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }
        public static object[] Arr(object o)
        {
            var a = o as object[];
            if (a != null) return a;
            var al = o as System.Collections.ArrayList;
            if (al != null) { var r = new object[al.Count]; al.CopyTo(r); return r; }
            var l = o as List<object>;
            if (l != null) return l.ToArray();
            return null;
        }
        // 字典安全取值(缺失返回 null,绝不抛异常)
        public static object Get(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        static string Err(int c)
        {
            if (c == -403) return "收藏夹不存在或未公开(私有收藏夹请填Cookie)";
            if (c == -404) return "收藏夹不存在";
            if (c == -412 || c == -352) return "被B站风控拦截,请稍后再试";
            if (c == -400) return "参数不正确(请检查收藏夹链接)";
            if (c == -101) return "需要登录才能访问(请填Cookie)";
            return "B站接口错误 (code " + c + ")";
        }

        public static void DebugDump(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "tomato_autotest.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n");
            }
            catch { }
        }

        // ---------- wbi 签名 ----------
        static string GetWbiKey(bool force)
        {
            if (!force && wbiKey != null && (DateTime.UtcNow - wbiKeyTime).TotalMinutes < 60) return wbiKey;
            for (int t = 0; t < 2; t++)
            {
                try
                {
                    string raw = HttpGet("https://api.bilibili.com/x/web-interface/nav", "https://space.bilibili.com/");
                    var jss = new JavaScriptSerializer();
                    jss.MaxJsonLength = int.MaxValue;
                    var d = jss.Deserialize<Dictionary<string, object>>(raw);
                    var data = Dict(Get(d, "data"));
                    var wbi = data == null ? null : Dict(Get(data, "wbi_img"));
                    // 注意:未登录时 nav 返回 code=-101,但 wbi_img 依然完整,不能检查 code
                    if (wbi != null && ToStr(Get(wbi, "img_url")).Length > 0)
                    {
                        string both = FileBase(ToStr(Get(wbi, "img_url"))) + FileBase(ToStr(Get(wbi, "sub_url")));
                        var sb = new StringBuilder();
                        for (int i = 0; i < 32 && i < both.Length; i++)
                        {
                            int idx = WBI_TAB[i];
                            if (idx >= 0 && idx < both.Length) sb.Append(both[idx]);
                        }
                        wbiKey = sb.ToString();
                        wbiKeyTime = DateTime.UtcNow;
                        return wbiKey;
                    }
                    DebugDump("nav bad body=" + raw.Substring(0, Math.Min(160, raw.Length)));
                }
                catch (Exception ex)
                {
                    DebugDump("nav ex=" + ex.Message);
                }
                System.Threading.Thread.Sleep(700);
            }
            throw new BiliException("无法获取签名参数");
        }
        static string FileBase(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int p = url.LastIndexOf('/');
            if (p >= 0) url = url.Substring(p + 1);
            int q = url.IndexOf('.');
            if (q > 0) url = url.Substring(0, q);
            return url;
        }
        static string SignWbi(Dictionary<string, string> p, bool forceKey)
        {
            p["wts"] = UnixSec();
            string key = GetWbiKey(forceKey);
            var sb = new StringBuilder();
            bool first = true;
            foreach (var k in p.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                string v = p[k] == null ? "" : p[k];
                var vb = new StringBuilder();
                foreach (char ch in v) if (!"!'()*".Contains(ch.ToString())) vb.Append(ch);
                if (!first) sb.Append('&');
                first = false;
                sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(vb.ToString()));
            }
            string q = sb.ToString();
            string md5 = HexMd5(q + key);
            return q + "&w_rid=" + md5;
        }
        static string HexMd5(string s)
        {
            using (var md5 = new MD5CryptoServiceProvider())
            {
                byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(32);
                foreach (byte x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        // ---------- 解析输入 ----------
        static Regex reBV = new Regex("BV[0-9A-Za-z]{10}", RegexOptions.IgnoreCase);
        static Regex reAV = new Regex("av(\\d+)", RegexOptions.IgnoreCase);

        public static string ResolveUrl(string url)
        {
            for (int i = 0; i < 5 && url.IndexOf("b23.tv", StringComparison.OrdinalIgnoreCase) >= 0; i++)
            {
                try
                {
                    Tls();
                    var req = (HttpWebRequest)WebRequest.Create(url);
                    req.AllowAutoRedirect = false;
                    req.UserAgent = UA;
                    req.Timeout = 8000;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        string loc = resp.Headers["Location"];
                        if (string.IsNullOrEmpty(loc)) break;
                        url = loc;
                    }
                }
                catch { break; }
            }
            return url;
        }

        public static void AddUnique(List<string> list, string s)
        {
            if (!list.Contains(s)) list.Add(s);
        }

        public static List<string> ParseTokens(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;
            foreach (string raw in text.Split('\n', '\r'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                if (line.IndexOf("b23.tv", StringComparison.OrdinalIgnoreCase) >= 0) line = ResolveUrl(line);
                var bvs = reBV.Matches(line);
                if (bvs.Count > 0)
                {
                    foreach (Match m in bvs) AddUnique(res, m.Value);
                    continue;
                }
                var avs = reAV.Matches(line);
                if (avs.Count > 0)
                {
                    foreach (Match m in avs) AddUnique(res, "av" + m.Groups[1].Value);
                    continue;
                }
                if (Regex.IsMatch(line, "^\\d+$")) AddUnique(res, "av" + line);
            }
            return res;
        }

        // 解析收藏夹输入:返回 fid/media_id;若只找到UID则 uidOut 返回 uid
        static Regex reUid = new Regex("bilibili\\.com/(\\d+)/favlist", RegexOptions.IgnoreCase);
        public static string ExtractFavSpec(string text, out string uid)
        {
            uid = null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            string s = text.Trim();
            if (s.IndexOf("b23.tv", StringComparison.OrdinalIgnoreCase) >= 0) s = ResolveUrl(s);
            var m = Regex.Match(s, "(?:media_id|fid)=(\\d+)");
            if (m.Success) return m.Groups[1].Value;
            m = reUid.Match(s);
            if (m.Success) { uid = m.Groups[1].Value; return null; }
            if (Regex.IsMatch(s, "^\\d+$")) return s;
            return null;
        }

        // 通过UID枚举收藏夹(需要Cookie),返回默认收藏夹(或第一个)的media_id与标题
        public static object[] FolderByUid(string uid, string sessdata)
        {
            var pr = new Dictionary<string, string>();
            pr["up_mid"] = uid;
            pr["web_location"] = "333.1388";
            string url = "https://api.bilibili.com/x/v3/fav/folder/created/list-all?" + SignWbi(pr, false);
            var d = ParseJson(HttpGet(url, "https://space.bilibili.com/", sessdata));
            if (ToInt(Get(d, "code")) != 0) throw new BiliException(Err(ToInt(Get(d, "code"))));
            var arr = Arr(Get(d, "data"));
            if (arr == null || arr.Length == 0)
                throw new BiliException("找不到收藏夹:该用户没有公开收藏夹,或需要填Cookie");
            string pickId = null, pickTitle = null;
            foreach (var it in arr)
            {
                var f = Dict(it);
                if (f == null) continue;
                string id = ToStr(Get(f, "id"));
                string title = ToStr(Get(f, "title"));
                if (pickId == null) { pickId = id; pickTitle = title; }
                if (title == "默认收藏夹") { pickId = id; pickTitle = title; break; }
            }
            return new object[] { pickId, pickTitle };
        }

        // ---------- 收藏夹 ----------
        // 拉取收藏夹全部普通视频(含标题/时长)。返回 [0]=标题 [1]=总数 [2]=List<FavItem>
        public static object[] FavList(string mediaId, string sessdata)
        {
            object[] hit;
            if (favCache.TryGetValue(mediaId, out hit) && (DateTime)hit[0] > DateTime.UtcNow)
                return hit;
            try
            {
                return DoCache(mediaId, sessdata);
            }
            catch (BiliException)
            {
                wbiKey = null;   // 强制重新获取签名密钥
                System.Threading.Thread.Sleep(600);
                return DoCache(mediaId, sessdata);
            }
        }
        static object[] DoCache(string mediaId, string sessdata)
        {
            string infoTitle = "";
            int infoCount = 0;
            var list = new List<FavItem>();
            for (int pn = 1; pn <= 25; pn++)
            {
                var pr = new Dictionary<string, string>();
                pr["media_id"] = mediaId;
                pr["pn"] = pn.ToString();
                pr["ps"] = "20";
                pr["order"] = "mtime";
                pr["type"] = "0";
                pr["tid"] = "0";
                pr["platform"] = "web";
                pr["web_location"] = "1540254";
                pr["order_avoided"] = "true";
                string raw = HttpGet("https://api.bilibili.com/x/v3/fav/resource/list?" + SignWbi(pr, false),
                    "https://space.bilibili.com/", sessdata);
                var jss = new JavaScriptSerializer();
                jss.MaxJsonLength = int.MaxValue;
                var d = jss.Deserialize<Dictionary<string, object>>(raw);
                if (d == null) throw new BiliException("返回数据异常");
                int code = ToInt(Get(d, "code"));
                if (code != 0) throw new BiliException(Err(code));
                var data = Dict(Get(d, "data"));
                if (data == null) throw new BiliException("返回数据异常");
                var info = Dict(Get(data, "info"));
                if (info != null)
                {
                    infoTitle = ToStr(Get(info, "title"));
                    infoCount = ToInt(Get(info, "media_count"));
                }
                var medias = Arr(Get(data, "medias"));
                if (medias == null || medias.Length == 0) break;
                foreach (var it in medias)
                {
                    var m = Dict(it);
                    if (m == null) continue;
                    string bv = ToStr(Get(m, "bvid"));
                    long aid = ToLong(Get(m, "aid"));
                    string title = ToStr(Get(m, "title"));
                    // 过滤失效/私密条目
                    if (title == "已失效视频" || title == "私密视频") continue;
                    var fi = new FavItem();
                    fi.Token = bv;
                    if (bv.Length == 0 && aid > 0) fi.Token = "av" + aid;
                    if (string.IsNullOrEmpty(fi.Token)) continue;
                    fi.Title = title;
                    fi.Duration = ToInt(Get(m, "duration"));
                    list.Add(fi);
                }
                if (medias.Length < 20 || list.Count >= 500) break;
            }
            object[] result = new object[] { infoTitle, infoCount, list };
            favCache[mediaId] = new object[] { DateTime.UtcNow.AddMinutes(10), result };
            return result;
        }

        // ---------- 视频信息 ----------
        public static VidInfo View(string token)
        {
            try
            {
                string url = token.StartsWith("av", StringComparison.OrdinalIgnoreCase)
                    ? "https://api.bilibili.com/x/web-interface/view?aid=" + token.Substring(2)
                    : "https://api.bilibili.com/x/web-interface/view?bvid=" + token;
                var d = Json(url, "https://www.bilibili.com/");
                if (ToInt(Get(d, "code")) != 0) return null;
                var data = Dict(Get(d, "data"));
                if (data == null) return null;
                var pages = Arr(Get(data, "pages"));
                var vi = new VidInfo();
                vi.Ok = true;
                vi.Title = ToStr(Get(data, "title"));
                vi.Pages = pages == null ? 1 : pages.Length;
                vi.Bvid = ToStr(Get(data, "bvid"));
                vi.Aid = ToLong(Get(data, "aid"));
                vi.Duration = ToInt(Get(data, "duration"));
                return vi;
            }
            catch { return null; }
        }

        static int RndNext(int max) { lock (rnd) { return rnd.Next(max); } }

        // 播放地址构造(不依赖View,容忍风控)
        public static string PlayUrl(string token, int page)
        {
            string url = token.StartsWith("av", StringComparison.OrdinalIgnoreCase)
                ? "https://player.bilibili.com/player.html?aid=" + token.Substring(2)
                : "https://player.bilibili.com/player.html?bvid=" + token;
            return url + "&page=" + page + "&high_quality=1&danmaku=0&autoplay=1";
        }
    }

    // 扩展辅助(占位)
    internal static class Ext { }

    // ---------------- 播放器(Edge/Chrome 独立小窗,最小化静默) ----------------
    public class Player
    {
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        System.Diagnostics.Process proc;
        string profileDir;

        public Player()
        {
            profileDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TomatoPomo", "player_profile");
        }

        static string FindBrowser()
        {
            string[] bases = {
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            };
            string[] rels = { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" };
            foreach (var b in bases)
            {
                if (string.IsNullOrEmpty(b)) continue;
                foreach (var r in rels)
                {
                    string p = Path.Combine(b, r);
                    if (File.Exists(p)) return p;
                }
            }
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe", null, null);
                string s = v as string;
                if (s != null)
                {
                    s = s.Trim('"');
                    if (File.Exists(s)) return s;
                }
            }
            catch { }
            return null;
        }

        IntPtr FindWndByPid(uint pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == pid && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(256);
                    GetWindowText(h, sb, 256);
                    if (sb.Length > 0) { found = h; return false; }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public bool SuppressMinimize { get; set; }   // 自动测试用:不最小化

        public void Play(string url)
        {
            Stop();
            string exe = FindBrowser();
            if (exe == null)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch { }
                throw new BiliException("未找到 Edge/Chrome,已在默认浏览器打开,到点后请手动关闭");
            }
            try { Directory.CreateDirectory(profileDir); } catch { }
            var psi = new System.Diagnostics.ProcessStartInfo();
            psi.FileName = exe;
            // 关键:禁用"被遮挡/后台窗口节流",保证最小化后自动播放与音频不被冻结
            psi.Arguments = string.Format(
                "--app=\"{0}\" --user-data-dir=\"{1}\" --no-first-run --no-default-browser-check " +
                "--disable-breakpad --autoplay-policy=no-user-gesture-required " +
                "--disable-features=CalculateNativeWinOcclusion,Translate,TranslateUI " +
                "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                "--disable-background-timer-throttling " +
                "--window-size=820,560", url, profileDir);
            psi.UseShellExecute = false;
            proc = System.Diagnostics.Process.Start(psi);
            MinimizeAsync();
        }

        // 异步把播放器窗口最小化(静默后台播放,不抢焦点)。
        // 延迟3秒:给B站播放器时间自动开播(已开始播放的媒体在最小化后不会停止)。
        public void MinimizeAsync()
        {
            if (proc == null) return;
            if (SuppressMinimize) return;
            var th = new System.Threading.Thread(delegate()
            {
                uint pid = 0;
                try { pid = (uint)proc.Id; } catch { return; }
                System.Threading.Thread.Sleep(3000);   // 先让自动播放启动
                for (int i = 0; i < 30; i++)
                {
                    System.Threading.Thread.Sleep(200);
                    try { if (proc.HasExited) return; } catch { return; }
                    IntPtr h = FindWndByPid(pid);
                    if (h != IntPtr.Zero)
                    {
                        bool sw = ShowWindow(h, 6);   // SW_MINIMIZE
                        bool ic = IsIconic(h);
                        Bili.DebugDump("minimize try" + i + " hwnd=" + h.ToInt64() + " sw=" + sw + " iconic=" + ic);
                        if (ic) return;
                        ShowWindow(h, 11);            // SW_FORCEMINIMIZE
                        return;
                    }
                }
                Bili.DebugDump("minimize: window not found in 6s");
            });
            th.IsBackground = true;
            th.Start();
        }

        public bool Running()
        {
            if (proc == null) return false;
            try { return !proc.HasExited; } catch { return false; }
        }

        public bool IsMinimized()
        {
            if (!Running()) return false;
            try
            {
                IntPtr h = FindWndByPid((uint)proc.Id);
                return h != IntPtr.Zero && IsIconic(h);
            }
            catch { return false; }
        }

        public void Stop()
        {
            if (proc == null) return;
            try
            {
                bool exited = false;
                try { exited = proc.HasExited; } catch { exited = true; }
                if (!exited)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo();
                    psi.FileName = "taskkill";
                    psi.Arguments = "/F /T /PID " + proc.Id;
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (var k = System.Diagnostics.Process.Start(psi)) { k.WaitForExit(3000); }
                }
            }
            catch { }
            try { proc.Dispose(); } catch { }
            proc = null;
        }
    }

    // ---------------- 主窗体 ----------------
    public class MainForm : Form
    {
        const int ST_IDLE = 0, ST_WORK = 1, ST_REST = 2, ST_PAUSE = 3;
        int state = ST_IDLE;
        int phase = 1;
        DateTime endAt;
        TimeSpan pausedLeft;
        string lastUrl = "";

        Config cfg;
        Stats stats;
        VideoList vlist;
        Player player;
        bool autotest;
        bool realExit = false, trayHintShown = false, settingsOn = false, dragging = false;
        Point dragP;

        internal static int TestWorkSec = -1;
        internal static int TestRestSec = -1;
        internal static bool autotestLongTest = false;

        System.Windows.Forms.Timer tick;
        NotifyIcon tray;

        static readonly Color BG = Color.FromArgb(250, 246, 241);
        static readonly Color BG2 = Color.FromArgb(243, 236, 229);
        static readonly Color Accent = Color.FromArgb(226, 74, 56);
        static readonly Color AccentD = Color.FromArgb(196, 58, 42);
        static readonly Color Teal = Color.FromArgb(42, 157, 143);
        static readonly Color TealD = Color.FromArgb(30, 130, 117);
        static readonly Color Gray = Color.FromArgb(140, 134, 128);
        static readonly Color Ink = Color.FromArgb(58, 55, 52);

        Label lblTitle, lblMode, lblTime, lblInfo, lblStats, lblPin, lblGear, lblClose;
        Button btnMain, btnSkip, btnReset;
        Panel pnlSet;
        NumericUpDown nudWork, nudRest;
        ListBox lstVideos;
        TextBox txtFav, txtSess;
        Button btnAdd, btnDel, btnUp, btnDown, btnEdit, btnImport, btnFavTest, btnSave, btnTry;
        Label lblListCount, lblFav;
        CheckBox chkBreak, chkAutoNext, chkAutoRest, chkCont, chkSeq, chkSound;

        Font fTitle, fMd2, fMode, fTime, fSmall, fBtn, fBtnS, fCtl;

        public MainForm(string[] args)
        {
            autotest = args != null && args.Any(a => a == "--autotest");
            autotestLongTest = args != null && args.Any(a => a == "--autotest-long");
            cfg = Store.LoadConfig();
            stats = Store.LoadStats();
            if (stats.Days == null) stats.Days = new Dictionary<string, int>();
            vlist = Store.LoadList();
            if (vlist == null) vlist = new VideoList { Items = Bili.DefaultList() };
            if (vlist.Items == null) vlist.Items = new List<ListEntry>();
            Store.SaveList(vlist);
            player = new Player();
            player.SuppressMinimize = args != null && args.Any(a => a == "--autotest-nomin");

            InitUi();
            InitTray();

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 250;
            tick.Tick += OnTick;
            tick.Start();

            TopMost = cfg.topMost;
            UpdatePinChar();
            UpdateUi();

            if (autotest) RunAutotest();
        }

        // ---------- UI 构建 ----------
        static Font PickFont(float size, FontStyle style)
        {
            string[] names = { "Microsoft YaHei UI", "Microsoft YaHei", "Microsoft JhengHei", "Segoe UI" };
            foreach (var n in names)
            {
                try { using (var t = new Font(n, 10f)) { if (t.Name == n) return new Font(n, size, style); } }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        void InitUi()
        {
            fTitle = PickFont(10.5f, FontStyle.Bold);
            fMd2 = new Font("Segoe MDL2 Assets", 10f);
            fMode = PickFont(10.5f, FontStyle.Bold);
            fTime = new Font("Segoe UI", 34f, FontStyle.Bold);
            fSmall = PickFont(8f, FontStyle.Regular);
            fBtn = PickFont(11f, FontStyle.Bold);
            fBtnS = PickFont(9.5f, FontStyle.Regular);
            fCtl = PickFont(9f, FontStyle.Regular);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = BG;
            Text = "番茄钟";
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;   // 关键:阻止WinForms二次缩放
            Icon = SafeIcon();
            ClientSize = new Size(300, 294);

            lblTitle = MkLabel("番 茄 钟", 34, 0, 110, 36, fTitle, Ink, ContentAlignment.MiddleLeft);
            lblPin = MkTitleBtn("\uE718", 204);
            lblGear = MkTitleBtn("\uE713", 234);
            lblClose = MkTitleBtn("\uE8BB", 264);

            lblMode = MkLabel("准备专注", 0, 40, 300, 24, fMode, Gray, ContentAlignment.MiddleCenter);
            lblTime = MkLabel("25:00", 0, 62, 300, 82, fTime, Ink, ContentAlignment.MiddleCenter);
            lblInfo = MkLabel("", 0, 146, 300, 20, fSmall, Gray, ContentAlignment.MiddleCenter);

            btnMain = MkBtn("开始专注", 20, 172, 260, 46, fBtn, Accent, AccentD, Color.White);
            btnMain.Click += delegate { BtnMainClick(); };

            btnSkip = MkBtn("跳过", 20, 226, 126, 34, fBtnS, BG2, Color.FromArgb(226, 218, 210), Ink);
            btnSkip.Click += delegate { SkipPhase(); };
            btnReset = MkBtn("重置", 154, 226, 126, 34, fBtnS, BG2, Color.FromArgb(226, 218, 210), Ink);
            btnReset.Click += delegate { ResetAll(); };

            lblStats = MkLabel("", 0, 266, 300, 20, fSmall, Gray, ContentAlignment.MiddleCenter);

            BuildSettings();
            ApplyRegion();
        }

        Icon SafeIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { return SystemIcons.Application; }
        }

        Label MkLabel(string text, int x, int y, int w, int h, Font f, Color c, ContentAlignment a)
        {
            var l = new Label();
            l.Text = text;
            l.Bounds = new Rectangle(x, y, w, h);
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = BG;
            l.TextAlign = a;
            l.AutoSize = false;
            Controls.Add(l);
            return l;
        }

        Label MkTitleBtn(string glyph, int x)
        {
            var l = new Label();
            l.Text = glyph;
            l.Bounds = new Rectangle(x, 7, 26, 22);
            l.Font = fMd2;
            l.ForeColor = Gray;
            l.BackColor = BG;
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Cursor = Cursors.Hand;
            l.MouseEnter += delegate { l.BackColor = Color.FromArgb(230, 221, 212); };
            l.MouseLeave += delegate { l.BackColor = BG; };
            Controls.Add(l);
            return l;
        }

        Button MkBtn(string text, int x, int y, int w, int h, Font f, Color bg, Color bgHover, Color fg)
        {
            var b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, h);
            b.Font = f;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = bg;
            b.ForeColor = fg;
            b.Cursor = Cursors.Hand;
            Color cNormal = bg, cHover = bgHover;
            b.MouseEnter += delegate { b.BackColor = cHover; };
            b.MouseLeave += delegate { b.BackColor = b.Enabled ? cNormal : ControlPaint.Light(cNormal); };
            b.Region = RoundRegion(new Rectangle(0, 0, w, h), 10);
            b.Tag = 10;
            Controls.Add(b);
            return b;
        }

        Region RoundRegion(Rectangle r, int radius)
        {
            using (var p = new GraphicsPath())
            {
                int d = radius * 2;
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
                p.CloseFigure();
                return new Region(p);
            }
        }

        void ApplyRegion()
        {
            var old = Region;
            Region = RoundRegion(new Rectangle(0, 0, Width, Height), 16);
            if (old != null) old.Dispose();
        }

        // ---------- 设置面板 ----------
        Button MkSetBtn(string text, int x, int y, int w, int h)
        {
            var b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, h);
            b.Font = fCtl;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = Color.FromArgb(223, 213, 203);
            b.ForeColor = Ink;
            b.Cursor = Cursors.Hand;
            b.Region = RoundRegion(new Rectangle(0, 0, w, h), 6);
            b.Tag = 6;
            pnlSet.Controls.Add(b);
            return b;
        }
        Label MkSetLabel(string text, int x, int y, int w, int h, Font f, Color c)
        {
            var l = new Label();
            l.Text = text;
            l.Bounds = new Rectangle(x, y, w, h);
            l.Font = f;
            l.ForeColor = c;
            l.BackColor = BG2;
            l.TextAlign = ContentAlignment.MiddleLeft;
            pnlSet.Controls.Add(l);
            return l;
        }
        CheckBox MkCheck(string text, int x, int y, bool val)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Bounds = new Rectangle(x, y, 262, 20);
            c.Font = fCtl;
            c.ForeColor = Ink;
            c.BackColor = BG2;
            c.Checked = val;
            pnlSet.Controls.Add(c);
            return c;
        }

        void BuildSettings()
        {
            pnlSet = new Panel();
            pnlSet.Location = new Point(6, 300);
            pnlSet.Size = new Size(288, 530);
            pnlSet.BackColor = BG2;
            pnlSet.Visible = false;
            pnlSet.AutoScroll = true;
            pnlSet.Region = RoundRegion(new Rectangle(0, 0, 288, 530), 12);
            pnlSet.Tag = 12;
            Controls.Add(pnlSet);

            MkSetLabel("专注", 14, 14, 36, 22, fCtl, Ink);
            nudWork = new NumericUpDown();
            nudWork.Minimum = 1; nudWork.Maximum = 180; nudWork.Value = Clamp(cfg.workMin, 1, 180);
            nudWork.Bounds = new Rectangle(52, 12, 56, 26); nudWork.Font = fCtl;
            pnlSet.Controls.Add(nudWork);
            MkSetLabel("分", 110, 14, 22, 22, fCtl, Ink);
            MkSetLabel("休息", 136, 14, 42, 22, fCtl, Ink);
            nudRest = new NumericUpDown();
            nudRest.Minimum = 1; nudRest.Maximum = 180; nudRest.Value = Clamp(cfg.restMin, 1, 180);
            nudRest.Bounds = new Rectangle(180, 12, 56, 26); nudRest.Font = fCtl;
            pnlSet.Controls.Add(nudRest);
            MkSetLabel("分", 238, 14, 22, 22, fCtl, Ink);

            lblListCount = MkSetLabel("", 14, 46, 262, 18, fSmall, Gray);

            lstVideos = new ListBox();
            lstVideos.Bounds = new Rectangle(14, 66, 262, 138);
            lstVideos.Font = fCtl;
            lstVideos.SelectionMode = SelectionMode.MultiExtended;
            lstVideos.IntegralHeight = false;
            lstVideos.ItemHeight = 17;
            pnlSet.Controls.Add(lstVideos);

            btnAdd = MkSetBtn("添加", 14, 210, 60, 25);
            btnAdd.Click += delegate { AddVideos(); };
            btnDel = MkSetBtn("删除", 78, 210, 60, 25);
            btnDel.Click += delegate { DeleteSelected(); };
            btnUp = MkSetBtn("上移", 142, 210, 44, 25);
            btnUp.Click += delegate { MoveSelected(-1); };
            btnDown = MkSetBtn("下移", 190, 210, 44, 25);
            btnDown.Click += delegate { MoveSelected(1); };
            btnEdit = MkSetBtn("编辑", 238, 210, 42, 25);
            btnEdit.Click += delegate { EditBulk(); };

            txtFav = new TextBox();
            txtFav.Bounds = new Rectangle(14, 244, 194, 23);
            txtFav.Font = fCtl;
            txtFav.Text = cfg.favInput;
            pnlSet.Controls.Add(txtFav);
            btnImport = MkSetBtn("导入", 214, 243, 62, 25);
            btnImport.Click += delegate { ImportFav(); };

            lblFav = MkSetLabel("收藏夹链接(fid=) 或 仅UID(需Cookie)", 14, 272, 262, 18, fSmall, Gray);

            txtSess = new TextBox();
            txtSess.Bounds = new Rectangle(14, 294, 194, 23);
            txtSess.Font = fCtl;
            txtSess.Text = cfg.sessdata == null ? "" : cfg.sessdata;
            pnlSet.Controls.Add(txtSess);
            btnFavTest = MkSetBtn("检测", 214, 293, 62, 25);
            btnFavTest.Click += delegate { TestFav(); };
            MkSetLabel("Cookie(SESSDATA)可选,用于私有收藏夹,仅存本机", 14, 320, 262, 16, fSmall, Gray);

            chkBreak = MkCheck("休息时继续播放", 14, 344, cfg.playDuringBreak);
            chkAutoNext = MkCheck("休息结束自动开始下一轮", 14, 366, cfg.autoNext);
            chkAutoRest = MkCheck("专注结束自动开始休息", 14, 388, cfg.autoRest);
            chkCont = MkCheck("到点不停止音乐,下阶段开始时换下一支", 14, 410, cfg.continuous);
            chkSeq = MkCheck("按顺序播放(不随机)", 14, 432, cfg.sequential);
            chkSound = MkCheck("到点提示音(嘟嘟声,默认关)", 14, 454, cfg.sound);

            btnSave = MkSetBtn("保存设置", 14, 482, 130, 30);
            btnSave.BackColor = Accent; btnSave.ForeColor = Color.White;
            btnSave.FlatAppearance.MouseOverBackColor = AccentD;
            btnSave.Click += delegate { SaveSettings(true); };

            btnTry = MkSetBtn("试听一支", 152, 482, 124, 30);
            btnTry.Click += delegate { TryPlay(); };

            RefreshListUi();
        }

        // ---------- 列表管理 ----------
        static string DurText(int sec)
        {
            if (sec <= 0) return "";
            return string.Format(" [{0:00}:{1:00}]", sec / 60, sec % 60);
        }
        static string EntryText(int idx, ListEntry e)
        {
            string title = string.IsNullOrEmpty(e.Title) ? e.Token : e.Title;
            if (title.Length > 26) title = title.Substring(0, 26) + "…";
            return string.Format("{0,2}. {1}{2}", idx + 1, title, DurText(e.Duration));
        }

        void RefreshListUi()
        {
            lstVideos.BeginUpdate();
            lstVideos.Items.Clear();
            for (int i = 0; i < vlist.Items.Count; i++)
                lstVideos.Items.Add(EntryText(i, vlist.Items[i]));
            lstVideos.EndUpdate();
            lblListCount.Text = string.Format("播放列表:共 {0} 支 · {1}",
                vlist.Items.Count, cfg.sequential ? "顺序播放" : "随机播放");
        }

        void SaveList()
        {
            Store.SaveList(vlist);
        }

        void ImportFav()
        {
            lblFav.ForeColor = Gray;
            lblFav.Text = "导入中…";
            Application.DoEvents();
            try
            {
                int added, dup;
                string folderTitle = ImportFavCore(txtFav.Text, txtSess.Text, out added, out dup);
                lblFav.ForeColor = TealD;
                lblFav.Text = string.Format("《{0}》 导入{1}支(重复{2}),共{3}支", folderTitle, added, dup, vlist.Items.Count);
                SaveSettings(false);
            }
            catch (Exception ex)
            {
                lblFav.ForeColor = AccentD;
                lblFav.Text = "失败:" + ex.Message;
            }
        }

        // 导入核心(按钮与自动测试共用):解析输入→取收藏夹→并入本地列表
        string ImportFavCore(string input, string sd, out int added, out int dup)
        {
            added = 0; dup = 0;
            string uid;
            string spec = Bili.ExtractFavSpec(input, out uid);
            string mediaId = spec;
            string folderTitle = "";
            if (mediaId == null)
            {
                if (string.IsNullOrEmpty(uid))
                    throw new BiliException("无法识别:请粘贴含 fid= 的收藏夹链接,或纯数字ID");
                if (string.IsNullOrWhiteSpace(sd))
                    throw new BiliException("仅UID需要Cookie(私有),请填SESSDATA或粘贴带fid=的链接");
                object[] fo = Bili.FolderByUid(uid, sd);
                mediaId = (string)fo[0];
                folderTitle = (string)fo[1];
            }
            object[] fl = Bili.FavList(mediaId, sd);
            var items = (List<FavItem>)fl[2];
            if (folderTitle.Length == 0) folderTitle = (string)fl[0];
            foreach (var it in items)
            {
                if (vlist.Items.Count >= 500) break;
                if (vlist.Items.Any(x => x.Token == it.Token)) { dup++; continue; }
                vlist.Items.Add(new ListEntry { Token = it.Token, Title = it.Title, Duration = it.Duration });
                added++;
            }
            SaveList();
            RefreshListUi();
            return folderTitle;
        }

        void TestFav()
        {
            string uid;
            string spec = Bili.ExtractFavSpec(txtFav.Text, out uid);
            string sd = txtSess.Text;
            lblFav.ForeColor = Gray;
            lblFav.Text = "检测中…";
            Application.DoEvents();
            try
            {
                string mediaId = spec;
                string folderTitle = "";
                if (mediaId == null)
                {
                    if (string.IsNullOrEmpty(uid)) { lblFav.ForeColor = AccentD; lblFav.Text = "无法识别,请粘贴收藏夹链接或纯数字ID"; return; }
                    if (string.IsNullOrWhiteSpace(sd)) { lblFav.ForeColor = AccentD; lblFav.Text = "仅UID需要Cookie才能找到收藏夹"; return; }
                    object[] fo = Bili.FolderByUid(uid, sd);
                    mediaId = (string)fo[0];
                    folderTitle = (string)fo[1];
                }
                object[] fl = Bili.FavList(mediaId, sd);
                if (folderTitle.Length == 0) folderTitle = (string)fl[0];
                lblFav.ForeColor = TealD;
                lblFav.Text = string.Format("《{0}》 共 {1} 个视频,可导入 ✓", folderTitle, fl[1]);
            }
            catch (Exception ex)
            {
                lblFav.ForeColor = AccentD;
                lblFav.Text = "失败:" + ex.Message;
            }
        }

        // 添加视频:弹输入框,粘贴BV号/链接(可多行),解析标题时长后加入列表
        void AddVideos()
        {
            string input = ShowInputDialog("添加视频", "每行一个 BV号 / 视频链接:", "");
            if (input == null) return;
            var tokens = Bili.ParseTokens(input);
            if (tokens.Count == 0) { Balloon("提示", "没有识别到视频"); return; }
            Cursor = Cursors.WaitCursor;
            try
            {
                int n = 0;
                foreach (string t in tokens)
                {
                    if (vlist.Items.Count >= 500) break;
                    if (vlist.Items.Any(x => x.Token == t)) continue;
                    var e = new ListEntry { Token = t, Title = "", Duration = 0 };
                    var v = Bili.View(t);
                    if (v != null) { e.Title = v.Title; e.Duration = v.Duration; }
                    vlist.Items.Add(e);
                    n++;
                    lblFav.ForeColor = Gray;
                    lblFav.Text = string.Format("解析中 {0}/{1} …", n, tokens.Count);
                    Application.DoEvents();
                }
                SaveList();
                RefreshListUi();
                lblFav.ForeColor = TealD;
                lblFav.Text = string.Format("已添加 {0} 支视频", n);
            }
            finally { Cursor = Cursors.Default; }
        }

        void DeleteSelected()
        {
            var idxs = lstVideos.SelectedIndices.Cast<int>().OrderByDescending(i => i).ToList();
            if (idxs.Count == 0) return;
            foreach (int i in idxs)
                if (i >= 0 && i < vlist.Items.Count) vlist.Items.RemoveAt(i);
            SaveList();
            RefreshListUi();
        }

        void MoveSelected(int dir)
        {
            if (lstVideos.SelectedIndices.Count != 1) return;
            int i = lstVideos.SelectedIndices[0];
            int j = i + dir;
            if (j < 0 || j >= vlist.Items.Count) return;
            var e = vlist.Items[i];
            vlist.Items.RemoveAt(i);
            vlist.Items.Insert(j, e);
            SaveList();
            RefreshListUi();
            lstVideos.SelectedIndex = j;
        }

        // 批量编辑:整表替换(相同token保留标题时长)
        void EditBulk()
        {
            var sb = new StringBuilder();
            foreach (var e in vlist.Items) sb.AppendLine(e.Token);
            string input = ShowInputDialog("批量编辑", "每行一个 BV号 / 链接(整表替换):", sb.ToString());
            if (input == null) return;
            var tokens = Bili.ParseTokens(input);
            var old = vlist.Items;
            var nl = new List<ListEntry>();
            foreach (string t in tokens)
            {
                var prev = old.FirstOrDefault(x => x.Token == t);
                if (prev != null) nl.Add(prev);
                else nl.Add(new ListEntry { Token = t, Title = "", Duration = 0 });
            }
            vlist.Items = nl;
            SaveList();
            RefreshListUi();
            Balloon("播放列表", "已更新,共 " + nl.Count + " 支");
        }

        // 简易多行文本输入对话框
        static string ShowInputDialog(string title, string tip, string initial)
        {
            var dlg = new Form();
            dlg.Text = title;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.MaximizeBox = false; dlg.MinimizeBox = false;
            dlg.ClientSize = new Size(420, 330);
            dlg.TopMost = true;
            var tipL = new Label { Text = tip, Bounds = new Rectangle(10, 8, 390, 20), Font = PickFont(9f, FontStyle.Regular) };
            var tb = new TextBox { Bounds = new Rectangle(10, 30, 390, 240), Multiline = true, ScrollBars = ScrollBars.Vertical, Text = initial ?? "" };
            var ok = new Button { Text = "确定", Bounds = new Rectangle(240, 280, 75, 30) };
            var cancel = new Button { Text = "取消", Bounds = new Rectangle(325, 280, 75, 30) };
            string result = null;
            ok.Click += delegate { result = tb.Text; dlg.Close(); };
            cancel.Click += delegate { dlg.Close(); };
            dlg.Controls.Add(tipL); dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok; dlg.CancelButton = cancel;
            dlg.ShowDialog();
            return result;
        }

        // ---------- 播放挑选 ----------
        ListEntry PickEntry()
        {
            if (vlist.Items.Count == 0)
                throw new BiliException("播放列表是空的:请在设置里\"添加\"视频或\"导入\"收藏夹");
            // 顺序播放
            if (cfg.sequential)
            {
                for (int n = 0; n < vlist.Items.Count; n++)
                {
                    if (cfg.listIndex < 0 || cfg.listIndex >= vlist.Items.Count) cfg.listIndex = 0;
                    var cand = vlist.Items[cfg.listIndex];
                    cfg.listIndex = (cfg.listIndex + 1) % vlist.Items.Count;
                    if (cand.Title != "已失效视频") return cand;
                }
            }
            // 随机,避免与最近3支重复,跳过失效
            List<ListEntry> pool = vlist.Items.Where(x => x.Title != "已失效视频").ToList();
            if (pool.Count == 0) pool = vlist.Items;
            if (pool.Count > 3 && cfg.recent != null && cfg.recent.Count > 0)
            {
                var filtered = pool.Where(x => !cfg.recent.Contains(x.Token)).ToList();
                if (filtered.Count > 0) pool = filtered;
            }
            var pick = pool[BiliRnd(pool.Count)];
            if (cfg.recent == null) cfg.recent = new List<string>();
            cfg.recent.Add(pick.Token);
            while (cfg.recent.Count > 3) cfg.recent.RemoveAt(0);
            return pick;
        }

        static Random rndMain = new Random();
        static int BiliRnd(int max) { lock (rndMain) { return rndMain.Next(max); } }

        // 构造播放信息(标题缺失时尝试View补全,失败也能播)
        PickResult ResolveEntry(ListEntry e)
        {
            var r = new PickResult();
            if (string.IsNullOrEmpty(e.Title) || e.Duration <= 0)
            {
                var v = Bili.View(e.Token);
                if (v != null)
                {
                    e.Title = v.Title;
                    e.Duration = v.Duration;
                    SaveList();
                    if (settingsOn) RefreshListUi();
                }
            }
            r.Url = Bili.PlayUrl(e.Token, 1);
            r.Title = string.IsNullOrEmpty(e.Title) ? e.Token : e.Title;
            return r;
        }

        // ---------- UI 构建(按钮事件) ----------
        void BtnMainClick()
        {
            if (state == ST_WORK || state == ST_REST)
            {
                pausedLeft = endAt - DateTime.UtcNow;
                if (pausedLeft < TimeSpan.Zero) pausedLeft = TimeSpan.Zero;
                player.Stop();
                state = ST_PAUSE;
            }
            else if (state == ST_PAUSE)
            {
                if ((phase == 1 || cfg.playDuringBreak) && !string.IsNullOrEmpty(lastUrl))
                {
                    try { player.Play(lastUrl); } catch { }
                }
                endAt = DateTime.UtcNow + pausedLeft;
                state = phase;
            }
            else
            {
                if (!BeginPhase()) return;
            }
            UpdateUi();
        }

        // 挑选并播放
        bool PlayMusic(int sec)
        {
            Cursor = Cursors.WaitCursor;
            btnMain.Text = "挑选视频…";
            btnMain.BackColor = Gray;
            Application.DoEvents();
            try
            {
                ListEntry e = PickEntry();
                Store.SaveConfig(cfg);
                PickResult pr = ResolveEntry(e);
                lastUrl = pr.Url;
                player.Play(pr.Url);
                Log("play token=" + e.Token + " title=" + pr.Title);
                return true;
            }
            catch (Exception ex)
            {
                Log("playfail " + ex.Message);
                if (autotest) return true;   // 测试模式不打扰
                if (phase == 1)
                {
                    var r = MessageBox.Show(this,
                        "打开视频失败:\n" + ex.Message + "\n\n是否不播放视频、仅开始计时?",
                        "番茄钟", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    return r == DialogResult.Yes;
                }
                Balloon("提示", "音乐打开失败:" + ex.Message);
                return true;
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        bool BeginPhase()
        {
            int sec = phase == 1 ? WorkSec() : RestSec();
            bool needMusic;
            if (phase == 1)
                needMusic = true;   // 专注开始:总是切换到下一支
            else
                needMusic = cfg.continuous ? !player.Running() : cfg.playDuringBreak;

            if (needMusic)
            {
                if (phase == 1 || cfg.continuous) player.Stop();  // 换下一支:先停旧窗
                if (!PlayMusic(sec)) return false;
            }
            endAt = DateTime.UtcNow.AddSeconds(sec);
            state = phase;
            UpdateUi();
            return true;
        }

        void SkipPhase()
        {
            if (state == ST_IDLE) return;
            player.Stop();
            phase = phase == 1 ? 2 : 1;
            state = ST_IDLE;
            bool ok = true;
            if ((phase == 2 && cfg.autoRest) || (phase == 1 && cfg.autoNext)) ok = BeginPhase();
            if (ok) UpdateUi();
        }

        void ResetAll()
        {
            player.Stop();
            state = ST_IDLE;
            phase = 1;
            lastUrl = "";
            UpdateUi();
        }

        void OnTick(object s, EventArgs e)
        {
            if (cfg.topMost && !TopMost) TopMost = true;
            if (state == ST_WORK || state == ST_REST)
            {
                TimeSpan left = endAt - DateTime.UtcNow;
                if (left.TotalMilliseconds <= 0) CompletePhase();
                else lblTime.Text = Fmt(left);
            }
        }

        void CompletePhase()
        {
            bool keepMusic = cfg.continuous && player.Running();
            if (!keepMusic) player.Stop();
            if (state == ST_WORK)
            {
                string day = DateTime.Now.ToString("yyyy-MM-dd");
                if (!stats.Days.ContainsKey(day)) stats.Days[day] = 0;
                stats.Days[day]++;
                Store.SaveStats(stats);
                Jingle(true);
                RefreshCounts();
                Balloon("完成第 " + stats.Days[day] + " 个番茄!", keepMusic ? "休息一下,音乐继续~" : "休息 " + cfg.restMin + " 分钟吧~");
                Log("workdone today=" + stats.Days[day]);
                phase = 2;
                if (cfg.autoRest) { BeginPhase(); return; }
                state = ST_IDLE;
            }
            else
            {
                Jingle(false);
                Balloon("休息结束", "开始下一个番茄吧!");
                Log("restdone");
                phase = 1;
                if (cfg.autoNext) { BeginPhase(); return; }
                state = ST_IDLE;
            }
            if (autotest && state == ST_IDLE)
            {
                Log("playermin=" + player.IsMinimized());
                Log("done");
                realExit = true;
                Close();
                return;
            }
            UpdateUi();
        }

        void Jingle(bool workDone)
        {
            if (!cfg.sound) return;   // 提示音可在设置里开启
            try
            {
                if (workDone) { Console.Beep(784, 180); Console.Beep(988, 180); Console.Beep(1319, 320); }
                else { Console.Beep(988, 160); Console.Beep(784, 160); Console.Beep(659, 280); }
            }
            catch { }
        }

        void Balloon(string title, string text)
        {
            try
            {
                tray.BalloonTipTitle = title;
                tray.BalloonTipText = text;
                tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        // ---------- 托盘 ----------
        void InitTray()
        {
            tray = new NotifyIcon();
            tray.Icon = SafeIcon();
            tray.Text = "番茄钟";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowUp(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示主窗口", null, delegate { ShowUp(); });
            menu.Items.Add("退出", null, delegate { ExitReal(); });
            tray.ContextMenuStrip = menu;
        }

        void ShowUp()
        {
            Show();
            WindowState = FormWindowState.Normal;
            TopMost = true;
            Activate();
            var t = new System.Windows.Forms.Timer();
            t.Interval = 600;
            t.Tick += delegate { TopMost = cfg.topMost; t.Stop(); t.Dispose(); };
            t.Start();
        }

        void ExitReal()
        {
            realExit = true;
            SaveAll();
            Close();
        }

        // ---------- 绘制 ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = 21f, cy = 19f;
            using (var b = new SolidBrush(Accent)) g.FillEllipse(b, cx - 9f, cy - 6f, 18f, 15f);
            using (var b = new SolidBrush(Color.FromArgb(110, 168, 70)))
            {
                g.FillEllipse(b, cx - 7f, cy - 11f, 7f, 5f);
                g.FillEllipse(b, cx + 1f, cy - 11f, 7f, 5f);
            }
            using (var b = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
                g.FillEllipse(b, cx - 6f, cy - 4f, 5f, 4f);
        }

        // ---------- 拖动 ----------
        void MakeDrag(Control c)
        {
            c.MouseDown += (object s, MouseEventArgs e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    dragging = true;
                    dragP = new Point(Control.MousePosition.X - Left, Control.MousePosition.Y - Top);
                }
            };
            c.MouseMove += (object s, MouseEventArgs e) =>
            {
                if (dragging) Location = new Point(Control.MousePosition.X - dragP.X, Control.MousePosition.Y - dragP.Y);
            };
            c.MouseUp += (object s, MouseEventArgs e) => { dragging = false; };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            MakeDrag(this);
            MakeDrag(lblTitle); MakeDrag(lblMode); MakeDrag(lblTime);
            MakeDrag(lblInfo); MakeDrag(lblStats);
            lblPin.Click += delegate
            {
                cfg.topMost = !cfg.topMost;
                TopMost = cfg.topMost;
                UpdatePinChar();
            };
            lblGear.Click += delegate { ToggleSettings(); };
            lblClose.Click += delegate { SaveAll(); Close(); };
            ApplyRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        void UpdatePinChar()
        {
            lblPin.Text = cfg.topMost ? "\uE841" : "\uE718";
            lblPin.ForeColor = cfg.topMost ? Accent : Gray;
        }

        void ToggleSettings()
        {
            settingsOn = !settingsOn;
            pnlSet.Visible = settingsOn;
            int h = sizeExpanded.Height;
            var wa = Screen.PrimaryScreen.WorkingArea;
            if (wa.Height - 70 < h) h = wa.Height - 70;   // 小屏自适应+面板内滚动
            if (h < sizeCollapsed.Height + 250) h = sizeCollapsed.Height + 250;
            ClientSize = settingsOn ? new Size(sizeExpanded.Width, h) : sizeCollapsed;
            pnlSet.Size = new Size(sizePanelW, Math.Min(sizePanelH, h - sizePanelTop - 10));
            pnlSet.Region = RoundRegion(new Rectangle(0, 0, pnlSet.Width, pnlSet.Height), 12);
            lblGear.ForeColor = settingsOn ? Accent : Gray;
            // 关键:窗口变高后若超出屏幕底部/右侧,自动移回可视范围(否则面板只显示一半)
            Point p = Location;
            if (p.Y + Height > wa.Bottom - 8) p.Y = wa.Bottom - Height - 8;
            if (p.Y < wa.Top + 4) p.Y = wa.Top + 4;
            if (p.X + Width > wa.Right - 8) p.X = wa.Right - Width - 8;
            if (p.X < wa.Left + 4) p.X = wa.Left + 4;
            Location = p;
            ApplyRegion();
            if (settingsOn) RefreshListUi();
        }

        // ---------- 设置 ----------
        void ApplyUiToConfig()
        {
            cfg.workMin = (int)nudWork.Value;
            cfg.restMin = (int)nudRest.Value;
            cfg.favInput = txtFav.Text.Trim();
            cfg.sessdata = txtSess.Text.Trim();
            cfg.playDuringBreak = chkBreak.Checked;
            cfg.autoNext = chkAutoNext.Checked;
            cfg.autoRest = chkAutoRest.Checked;
            cfg.continuous = chkCont.Checked;
            cfg.sequential = chkSeq.Checked;
            cfg.sound = chkSound.Checked;
        }

        void SaveSettings(bool notify)
        {
            ApplyUiToConfig();
            Store.SaveConfig(cfg);
            SaveList();
            if (notify)
            {
                lblFav.ForeColor = TealD;
                lblFav.Text = "已保存 ✓(" + cfg.workMin + "/" + cfg.restMin + "分钟 · " + (cfg.sequential ? "顺序" : "随机") + ")";
                RefreshListUi();
                UpdateUi();
            }
        }

        void TryPlay()
        {
            try
            {
                SaveSettings(false);
                Cursor = Cursors.WaitCursor;
                btnTry.Text = "挑选中…";
                Application.DoEvents();
                ListEntry e = PickEntry();
                PickResult pr = ResolveEntry(e);
                lastUrl = pr.Url;
                player.Play(pr.Url);
                Balloon("试听", pr.Title);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开失败:\n" + ex.Message, "番茄钟",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
                btnTry.Text = "试听一支";
            }
        }

        // ---------- 计时核心 ----------
        int WorkSec() { return TestWorkSec > 0 ? TestWorkSec : cfg.workMin * 60; }
        int RestSec() { return TestRestSec > 0 ? TestRestSec : cfg.restMin * 60; }

        static string Fmt(TimeSpan t)
        {
            int total = (int)Math.Ceiling(t.TotalSeconds);
            if (total < 0) total = 0;
            return string.Format("{0:00}:{1:00}", total / 60, total % 60);
        }

        // ---------- UI 刷新 ----------
        int TodayCount()
        {
            int n;
            string day = DateTime.Now.ToString("yyyy-MM-dd");
            if (stats.Days.TryGetValue(day, out n)) return n;
            return 0;
        }
        int TotalCount() { return stats.Days.Values.Sum(); }

        string StatsLine()
        {
            var parts = new List<string>();
            for (int i = 6; i >= 0; i--)
            {
                string day = DateTime.Now.AddDays(-i).ToString("yyyy-MM-dd");
                int n;
                if (stats.Days.TryGetValue(day, out n)) parts.Add(n.ToString());
                else parts.Add("0");
            }
            return "近7天 " + string.Join(" · ", parts.ToArray());
        }

        void RefreshCounts() { UpdateUi(); }

        void UpdateUi()
        {
            bool running = state == ST_WORK || state == ST_REST;
            if (state == ST_WORK) { lblMode.Text = "专注中"; lblMode.ForeColor = Accent; lblTime.ForeColor = Ink; }
            else if (state == ST_REST) { lblMode.Text = "休息中"; lblMode.ForeColor = TealD; lblTime.ForeColor = TealD; }
            else if (state == ST_PAUSE) { lblMode.Text = "已暂停"; lblMode.ForeColor = Gray; lblTime.ForeColor = Gray; }
            else { lblMode.Text = phase == 2 ? "休息就绪" : "准备专注"; lblMode.ForeColor = Gray; lblTime.ForeColor = Ink; }

            TimeSpan t;
            if (state == ST_WORK || state == ST_REST)
            {
                t = endAt - DateTime.UtcNow;
                if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            }
            else if (state == ST_PAUSE) t = pausedLeft;
            else t = TimeSpan.FromSeconds(phase == 2 ? RestSec() : WorkSec());
            lblTime.Text = Fmt(t);

            if (state == ST_WORK || state == ST_REST)
            {
                btnMain.Text = "暂 停";
                btnMain.BackColor = Color.FromArgb(94, 102, 110);
            }
            else if (state == ST_PAUSE)
            {
                btnMain.Text = "继 续";
                btnMain.BackColor = Accent;
            }
            else
            {
                btnMain.Text = phase == 2 ? "开始休息" : "开始专注";
                btnMain.BackColor = Accent;
            }
            btnSkip.Enabled = state != ST_IDLE;
            btnReset.Enabled = state != ST_IDLE;
            btnSkip.BackColor = btnSkip.Enabled ? BG2 : ControlPaint.Light(BG2);
            btnReset.BackColor = btnReset.Enabled ? BG2 : ControlPaint.Light(BG2);

            string info = "今日 × " + TodayCount() + " · 累计 " + TotalCount();
            if (running) info += " · " + endAt.ToLocalTime().ToString("HH:mm") + " 结束";
            lblInfo.Text = info;
            lblStats.Text = StatsLine();
        }

        // ---------- DPI/位置 ----------
        Size sizeCollapsed = new Size(300, 294);
        Size sizeExpanded = new Size(300, 840);
        int sizePanelW = 288, sizePanelH = 530, sizePanelTop = 300;

        void ScaleForDpi()
        {
            float dpi;
            using (var g = CreateGraphics()) dpi = g.DpiX;
            float f = dpi / 96f;
            if (f > 1.01f)
            {
                Scale(new SizeF(f, f));
                sizeCollapsed = new Size((int)(300 * f), (int)(294 * f));
                sizeExpanded = new Size((int)(300 * f), (int)(840 * f));
                sizePanelW = (int)(288 * f);
                sizePanelH = (int)(530 * f);
                sizePanelTop = (int)(300 * f);
                ReapplyRegions();
            }
        }

        void SetPosition()
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            bool ok = cfg.x >= 0 && cfg.y >= 0 &&
                      cfg.x >= wa.Left - 60 && cfg.x <= wa.Right - Width + 60 &&
                      cfg.y >= wa.Top - 20 && cfg.y <= wa.Bottom - Height + 30;
            if (ok) Location = new Point(cfg.x, cfg.y);
            else Location = new Point(wa.Right - Width - 24, wa.Bottom - Height - 40);
        }

        static IEnumerable<Control> IterateAll(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control d in IterateAll(c)) yield return d;
            }
        }

        void ReapplyRegions()
        {
            ApplyRegion();
            foreach (Control c in IterateAll(this))
            {
                int? r = c.Tag as int?;
                if (r.HasValue && c.Width > 0 && c.Height > 0)
                    c.Region = RoundRegion(new Rectangle(0, 0, c.Width, c.Height), r.Value);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ScaleForDpi();
            SetPosition();
            ApplyRegion();
        }

        // ---------- 生命周期 ----------
        void SaveAll()
        {
            cfg.x = Location.X;
            cfg.y = Location.Y;
            Store.SaveConfig(cfg);
            Store.SaveStats(stats);
            Store.SaveList(vlist);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!realExit && !autotest)
            {
                e.Cancel = true;
                SaveAll();
                Hide();
                if (!trayHintShown)
                {
                    trayHintShown = true;
                    Balloon("番茄钟还在后台运行", "双击托盘图标打开;右键托盘图标可退出。");
                }
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            player.Stop();
            SaveAll();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosed(e);
        }

        // ---------- 自动测试 ----------
        static void Log(string s)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "tomato_autotest.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n");
            }
            catch { }
        }

        void RunAutotest()
        {
            Log("begin list=" + vlist.Items.Count + " sequential=" + cfg.sequential + " favInput=" + (cfg.favInput ?? ""));
            TestWorkSec = autotestLongTest ? 40 : 6;
            TestRestSec = 4;
            cfg.playDuringBreak = false;
            cfg.autoRest = true;
            cfg.autoNext = false;
            cfg.continuous = false;
            if (!string.IsNullOrWhiteSpace(cfg.favInput))
            {
                try
                {
                    int added, dup;
                    string ft = ImportFavCore(cfg.favInput, cfg.sessdata, out added, out dup);
                    Log("import ok folder=" + ft + " added=" + added + " dup=" + dup + " total=" + vlist.Items.Count);
                }
                catch (Exception ex)
                {
                    Log("import fail " + ex.Message);
                }
            }
            phase = 1;
            BeginPhase();
        }
    }

    // ---------------- 入口 ----------------
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool autotest = args != null && args.Any(a => a == "--autotest");
            bool created;
            var mutex = new System.Threading.Mutex(true, "TomatoPomo_Single", out created);
            if (!created && !autotest)
            {
                MessageBox.Show("番茄钟已经在运行啦(看看右下角托盘图标)", "番茄钟");
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
            if (created) { try { mutex.ReleaseMutex(); } catch { } }
        }
    }
}
