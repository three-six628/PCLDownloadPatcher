using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace PCLDownloadPatcher
{
    static class Program
    {
        const string SettingsFileName = "PCLDownloadPatcher.Settings.json";

        [STAThread]
        static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                if (name == "Mono.Cecil")
                {
                    using (var st = typeof(Program).Assembly.GetManifestResourceStream("Mono.Cecil.dll"))
                    {
                        if (st == null) return null;
                        var b = new byte[st.Length];
                        st.Read(b, 0, b.Length);
                        return Assembly.Load(b);
                    }
                }
                return null;
            };

            if (args.Length >= 1 && args[0] == "--patch")
            {
                // 命令行修补模式（测试 / 自动化用）：输出 <src>.download_fixed.exe
                string src = args[1].Trim('"');
                if (!File.Exists(src)) return 2;
                string dst = Path.Combine(
                    Path.GetDirectoryName(src),
                    Path.GetFileNameWithoutExtension(src) + ".download_fixed.exe");
                try
                {
                    PatchCore.Patch(src, dst, Assembly.GetExecutingAssembly().Location);
                    return 0;
                }
                catch (Exception ex)
                {
                    try { File.WriteAllText(dst + ".err.txt", ex.ToString()); } catch { }
                    return 3;
                }
            }

            if (args.Length >= 1 && args.Contains("--update"))
            {
                // 更新钩子模式：修补下载后的新版本（--inplace 覆盖源文件）
                string target = null;
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--inplace")
                    {
                        target = args[i + 1].Trim('"');
                        break;
                    }
                }
                if (string.IsNullOrEmpty(target) || !File.Exists(target))
                    return 2;

                bool autoFix = ReadSettings().AutoFix;
                if (!autoFix)
                    return 0; // 未开启自动修复，跳过

                try
                {
                    PatchCore.Patch(target, target, Assembly.GetExecutingAssembly().Location);
                    return 0;
                }
                catch
                {
                    return 3;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        public static string SettingsPath
        {
            get
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                return Path.Combine(dir, SettingsFileName);
            }
        }

        public static Settings ReadSettings()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    string txt = File.ReadAllText(SettingsPath, Encoding.UTF8);
                    var obj = SimpleJson.Parse(txt);
                    bool v;
                    if (obj != null && obj.TryGetValue("autoFix", out v))
                        return new Settings { AutoFix = v };
                }
            }
            catch { }
            return new Settings { AutoFix = false };
        }

        public static void WriteSettings(bool autoFix)
        {
            string txt = "{\"autoFix\":" + (autoFix ? "true" : "false") + "}";
            File.WriteAllText(SettingsPath, txt, new System.Text.UTF8Encoding(false));
        }
    }

    class Settings
    {
        public bool AutoFix;
    }

    static class SimpleJson
    {
        // 极简 JSON 解析（仅本工具自己的设置文件格式）
        public static System.Collections.Generic.Dictionary<string, bool> Parse(string txt)
        {
            var d = new System.Collections.Generic.Dictionary<string, bool>();
            int i = txt.IndexOf('{');
            int j = txt.LastIndexOf('}');
            if (i < 0 || j <= i) return d;
            string body = txt.Substring(i + 1, j - i - 1);
            foreach (var part in body.Split(','))
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;
                string key = kv[0].Trim().Trim('"').Trim('\'');
                string val = kv[1].Trim().Trim('"').Trim('\'');
                bool b;
                if (bool.TryParse(val, out b)) d[key] = b;
            }
            return d;
        }
    }
}
