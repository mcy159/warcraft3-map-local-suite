// =====================================================================
// keybind.cs — War3 改键工具（技能栏/物品栏按键映射直发）
// ---------------------------------------------------------------------
// 原理（对齐 War3娱乐大师 机制，零坐标零注入零内存读取）：
//   SetWindowsHookEx(WH_KEYBOARD_LL) 自进程全局低级键盘钩子捕获按键
//   -> 前台窗口判定（GetForegroundWindow == war3 窗口，同进程兜底）
//   -> IsChat 聊天避让（GetGUIThreadInfo 子窗口焦点 + Enter/Esc 启发式）
//   -> keybd_event 真实按键直发（war3 DirectInput 轮询真实事件）
//   -> 可选 PostMessage(WM_KEYDOWN/UP) 加速路径（配置开关）
//   -> 吞掉源键（钩子返回 1），LLKHF_INJECTED 过滤防回环
// 零坐标：无 GetClientRect/ClientToScreen/SetCursorPos/SendInput/mouse_event
// 不读 war3 内存：无 ReadProcessMemory/OpenProcess
// 不注入：WH_KEYBOARD_LL 是自进程钩子（dwThreadId=0），无 CreateRemoteThread
// 编译：csc（.NET Framework 4.0+），普通权限即可运行
// =====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeybindTool
{
    // =================================================================
    // 1. Native user32/kernel32 P/Invoke
    // =================================================================
    internal static class Native
    {
        // ---- 钩子 ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_KEYUP = 0x0101;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int WM_SYSKEYUP = 0x0105;
        public const uint LLKHF_INJECTED = 0x00000010;
        public const uint LLKHF_UP = 0x00000080;

        public const uint KEYEVENTF_KEYUP = 0x0002;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public IntPtr hwndIME;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        // ---- 发送 ----
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        // ---- 窗口判定 ----
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        public static string GetWindowTextSafe(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClassNameSafe(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            StringBuilder sb = new StringBuilder(256);
            GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    // =================================================================
    // 2. 按键名 <-> VK 码
    // =================================================================
    internal static class Vk
    {
        // 字母 A-Z: 0x41-0x5A；数字 0-9: 0x30-0x39
        public static bool TryParse(string name, out uint vk)
        {
            vk = 0;
            if (name == null) return false;
            string s = name.Trim();
            if (s.Length == 0) return false;

            // 十六进制 0xNN 形式（VK 码直写）
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                {
                    vk = v;
                    return true;
                }
                return false;
            }

            if (s.Length == 1)
            {
                char c = s[0];
                if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
                if (c >= 'a' && c <= 'z') { vk = (uint)(c - 'a' + 'A'); return true; }
                if (c >= '0' && c <= '9') { vk = (uint)c; return true; }
            }

            string t = s.ToLowerInvariant();
            switch (t)
            {
                case "numpad0": vk = 0x60; return true;
                case "numpad1": vk = 0x61; return true;
                case "numpad2": vk = 0x62; return true;
                case "numpad3": vk = 0x63; return true;
                case "numpad4": vk = 0x64; return true;
                case "numpad5": vk = 0x65; return true;
                case "numpad6": vk = 0x66; return true;
                case "numpad7": vk = 0x67; return true;
                case "numpad8": vk = 0x68; return true;
                case "numpad9": vk = 0x69; return true;
                case "f1": vk = 0x70; return true;
                case "f2": vk = 0x71; return true;
                case "f3": vk = 0x72; return true;
                case "f4": vk = 0x73; return true;
                case "f5": vk = 0x74; return true;
                case "f6": vk = 0x75; return true;
                case "f7": vk = 0x76; return true;
                case "f8": vk = 0x77; return true;
                case "f9": vk = 0x78; return true;
                case "f10": vk = 0x79; return true;
                case "f11": vk = 0x7A; return true;
                case "f12": vk = 0x7B; return true;
                case "esc": vk = 0x1B; return true;
                case "tab": vk = 0x09; return true;
                case "space": vk = 0x20; return true;
                case "enter": vk = 0x0D; return true;
                case "back": vk = 0x08; return true;
                case "delete": vk = 0x2E; return true;
                case "insert": vk = 0x2D; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pgup": vk = 0x21; return true;
                case "pgdn": vk = 0x22; return true;
                case "left": vk = 0x25; return true;
                case "right": vk = 0x27; return true;
                case "up": vk = 0x26; return true;
                case "down": vk = 0x28; return true;
                case "capslock": vk = 0x14; return true;
                case "shift": vk = 0x10; return true;
                case "ctrl": vk = 0x11; return true;
                case "alt": vk = 0x12; return true;
            }
            return false;
        }

        public static string Name(uint vk)
        {
            if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
            if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
            if (vk >= 0x60 && vk <= 0x69) return "NumPad" + (vk - 0x60).ToString();
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1).ToString();
            switch (vk)
            {
                case 0x1B: return "Esc";
                case 0x09: return "Tab";
                case 0x20: return "Space";
                case 0x0D: return "Enter";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x25: return "Left";
                case 0x27: return "Right";
                case 0x26: return "Up";
                case 0x28: return "Down";
                case 0x14: return "CapsLock";
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
            }
            return "0x" + vk.ToString("X2");
        }
    }

    // =================================================================
    // 3. 日志（tools\keybind_cal.log，AutoFlush 必须开）
    // =================================================================
    internal static class Logger
    {
        private static readonly object _sync = new object();
        private static StreamWriter _writer;
        private static bool _enabled = true;

        public static string LogDir
        {
            get
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                return Path.Combine(exeDir, "tools");
            }
        }

        public static string LogPath
        {
            get { return Path.Combine(LogDir, "keybind_cal.log"); }
        }

        public static void Init(bool enabled)
        {
            _enabled = enabled;
            if (!_enabled) return;
            try
            {
                Directory.CreateDirectory(LogDir);
                FileStream fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(fs, Encoding.UTF8);
                _writer.AutoFlush = true; // 必须开：崩溃/被杀时日志不丢
            }
            catch (Exception)
            {
                _writer = null;
                _enabled = false;
            }
        }

        public static void Write(string msg)
        {
            if (!_enabled || _writer == null) return;
            lock (_sync)
            {
                try
                {
                    _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg);
                }
                catch
                {
                }
            }
        }

        public static void Write(string fmt, object a0)
        {
            Write(string.Format(fmt, a0));
        }

        public static void Write(string fmt, object a0, object a1)
        {
            Write(string.Format(fmt, a0, a1));
        }
    }

    // =================================================================
    // 4. 配置 keybind.ini（UTF-8，手写解析，零外部依赖）
    // =================================================================
    internal class KeybindConfig
    {
        public bool Enabled = true;          // 热键总开关
        public bool IsChatAvoid = true;      // 聊天避让开关
        public bool UsePostMessage = false;  // PostMessage(WM_KEYDOWN/UP) 加速路径
        public bool ForegroundCheck = true;  // 前台窗口判定开关（false=全局生效，不推荐）
        public bool LogEnabled = true;
        public int PollIntervalMs = 5000;    // war3 窗口扫描间隔

        // 源VK -> 目标VK
        public readonly List<KeyValuePair<uint, uint>> Mappings = new List<KeyValuePair<uint, uint>>();

        public static string ConfigPath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "keybind.ini"); }
        }

        // 默认映射（QWER 组：技能栏 1-4 槽默认快捷键 Q/W/E/R 的快捷触发层；
        // F1-F4 为源键 -> 技能 Q/W/E/R；T/F 槽默认不改，取消注释即启用）
        private static readonly string[] DefaultLines = new string[]
        {
            "[keybind]",
            "Enabled=1",
            "IsChatAvoid=1",
            "UsePostMessage=0",
            "ForegroundCheck=1",
            "LogEnabled=1",
            "PollIntervalMs=5000",
            "",
            "[Mappings]",
            "; 源键=目标键",
            "; 支持: A-Z 0-9 F1-F12 NumPad0-NumPad9 或 0xNN(十六进制VK)",
            "; QWER 组（默认）：按 F1-F4 触发技能栏 1-4 槽默认快捷键 Q/W/E/R",
            "; 注: war3 原生 F1-F3 是英雄选择键，映射后按 F1 将触发技能 Q；",
            "; 需保留选英雄功能时改用下方 NumPad 方案",
            "F1=Q",
            "F2=W",
            "F3=E",
            "F4=R",
            "; 技能栏 5/6 槽（T/F）示例（默认不改，取消注释即启用）:",
            ";F5=T",
            ";F6=F",
            "; 小键盘替代方案（源键改为 NumPad1-4，需同时注释上方 F1-F4）:",
            ";NumPad1=Q",
            ";NumPad2=W",
            ";NumPad3=E",
            ";NumPad4=R",
        };

        public static void EnsureDefaultFile()
        {
            try
            {
                if (File.Exists(ConfigPath)) return;
                File.WriteAllLines(ConfigPath, DefaultLines, new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        public void Load()
        {
            Mappings.Clear();
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string[] lines = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                string section = string.Empty;
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        section = line.Substring(1, line.Length - 2).Trim();
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Length == 0) continue;

                    if (section.Equals("keybind", StringComparison.OrdinalIgnoreCase))
                    {
                        switch (key.ToLowerInvariant())
                        {
                            case "enabled": Enabled = ParseBool(val, Enabled); break;
                            case "ischatavoid": IsChatAvoid = ParseBool(val, IsChatAvoid); break;
                            case "usepostmessage": UsePostMessage = ParseBool(val, UsePostMessage); break;
                            case "foregroundcheck": ForegroundCheck = ParseBool(val, ForegroundCheck); break;
                            case "logenabled": LogEnabled = ParseBool(val, LogEnabled); break;
                            case "pollintervalms": PollIntervalMs = ParseInt(val, PollIntervalMs); break;
                        }
                    }
                    else if (section.Equals("Mappings", StringComparison.OrdinalIgnoreCase))
                    {
                        uint src, dst;
                        if (Vk.TryParse(key, out src) && Vk.TryParse(val, out dst))
                        {
                            Mappings.Add(new KeyValuePair<uint, uint>(src, dst));
                        }
                        else
                        {
                            Logger.Write("配置忽略无效映射行: " + raw);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Write("配置加载异常: " + ex.Message);
            }
        }

        private static bool ParseBool(string s, bool def)
        {
            bool r;
            if (bool.TryParse(s, out r)) return r;
            if (s == "1") return true;
            if (s == "0") return false;
            return def;
        }

        private static int ParseInt(string s, int def)
        {
            int r;
            if (int.TryParse(s, out r)) return r;
            return def;
        }
    }

    // =================================================================
    // 5. War3 窗口定位 + 前台判定 + 聊天避让
    // =================================================================
    internal class War3Window
    {
        private readonly KeybindConfig _cfg;
        public IntPtr HWnd = IntPtr.Zero;
        public bool WasFound = false;   // 状态翻转时才记日志（防刷屏）

        public War3Window(KeybindConfig cfg)
        {
            _cfg = cfg;
        }

        // 免权限进程快照枚举主窗口（同用户进程 MainWindowHandle 无需管理员）
        public void Refresh()
        {
            IntPtr found = FindWar3Window();
            if (found == HWnd) return;
            HWnd = found;
            if (found != IntPtr.Zero)
            {
                Logger.Write(string.Format("war3 窗口已定位 HWnd=0x{0:X} 标题=\"{1}\" 类名=\"{2}\"",
                    found.ToInt64(),
                    Native.GetWindowTextSafe(found),
                    Native.GetClassNameSafe(found)));
                WasFound = true;
            }
            else
            {
                if (WasFound)
                    Logger.Write("war3 窗口消失（游戏退出或句柄失效），等待重新定位");
                WasFound = false;
            }
        }

        private static IntPtr FindWar3Window()
        {
            Process[] procs = null;
            try { procs = Process.GetProcesses(); }
            catch { return IntPtr.Zero; }
            IntPtr best = IntPtr.Zero;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero) continue;
                    if (p.ProcessName.Equals("war3", StringComparison.OrdinalIgnoreCase))
                        return h; // 进程名命中优先（平台也可能改标题）
                    if (p.MainWindowTitle.IndexOf("Warcraft III", StringComparison.OrdinalIgnoreCase) >= 0)
                        best = h;  // 标题兜底
                }
                catch
                {
                }
                finally
                {
                    try { p.Dispose(); }
                    catch { }
                }
            }
            return best;
        }

        // 前台窗口是否 war3（含同进程子窗口兜底）
        public bool IsForeground()
        {
            if (!_cfg.ForegroundCheck) return true;
            if (HWnd == IntPtr.Zero) return false;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (fg == HWnd) return true;
            uint fgPid, pid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            Native.GetWindowThreadProcessId(HWnd, out pid);
            return fgPid != 0 && fgPid == pid; // 聊天/子窗口同进程也算 war3
        }

        // ============ 聊天避让 ============
        // 方案1: GetGUIThreadInfo 子窗口键盘焦点（war3 聊天输入若有 Win32 子控件则命中）
        // 方案2: Enter/Esc 启发式状态机（war3 聊天框多为游戏内自绘，无 Win32 控件时兜底）
        private bool _chatOpen = false;

        public bool IsChat()
        {
            if (!_cfg.IsChatAvoid) return false;

            // 方案1
            if (HWnd != IntPtr.Zero)
            {
                uint pid;
                uint tid = Native.GetWindowThreadProcessId(HWnd, out pid);
                if (tid != 0)
                {
                    Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
                    gti.cbSize = Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                    if (Native.GetGUIThreadInfo(tid, ref gti))
                    {
                        if (gti.hwndFocus != IntPtr.Zero && gti.hwndFocus != HWnd)
                            return true; // 键盘焦点落在子控件（聊天输入框）
                    }
                }
            }

            // 方案2
            return _chatOpen;
        }

        // 钩子回调里跟踪 Enter/Esc（改键映射外的键）
        public void OnEnterKey()
        {
            _chatOpen = !_chatOpen; // 打开聊天 / 发送后关闭
        }

        public void OnEscapeKey()
        {
            _chatOpen = false;
        }
    }

    // =================================================================
    // 6. 改键核心：钩子 + 映射 + 发送
    // =================================================================
    internal class KeybindEngine
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private Native.LowLevelKeyboardProc _proc; // 必须持有引用防 GC
        private IntPtr _hook = IntPtr.Zero;

        public KeybindEngine(KeybindConfig cfg, War3Window war3)
        {
            _cfg = cfg;
            _war3 = war3;
        }

        public bool Install()
        {
            _proc = new Native.LowLevelKeyboardProc(HookCallback);
            using (Process cur = Process.GetCurrentProcess())
            {
                IntPtr hMod = Native.GetModuleHandle(cur.ProcessName);
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, hMod, 0);
            }
            if (_hook == IntPtr.Zero)
            {
                Logger.Write("WH_KEYBOARD_LL 钩子安装失败, Win32Err=" + Marshal.GetLastWin32Error());
                return false;
            }
            Logger.Write("WH_KEYBOARD_LL 钩子已安装, 映射 " + _cfg.Mappings.Count + " 条");
            return true;
        }

        public void Uninstall()
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
                Logger.Write("钩子已卸载");
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _cfg.Enabled && _hook != IntPtr.Zero)
            {
                Native.KBDLLHOOKSTRUCT kbd = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                int msg = wParam.ToInt32();
                bool down = (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN);
                bool up = (msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP);
                if ((down || up) && ((kbd.flags & Native.LLKHF_INJECTED) == 0))
                {
                    // 聊天状态跟踪（仅跟踪 Enter/Esc 本身，不影响已映射键）
                    if (kbd.vkCode == 0x0D && down) _war3.OnEnterKey();
                    if (kbd.vkCode == 0x1B && down) _war3.OnEscapeKey();

                    KeyValuePair<uint, uint> map;
                    if (TryFindMapping(kbd.vkCode, out map))
                    {
                        if (_war3.IsForeground() && !_war3.IsChat())
                        {
                            if (down)
                            {
                                SendKey(map.Value);
                                Logger.Write(string.Format("改键: {0}(0x{1:X2}) -> {2}(0x{3:X2}) DOWN",
                                    Vk.Name(map.Key), map.Key, Vk.Name(map.Value), map.Value));
                            }
                            // down/up 都吞掉源键（return 1），避免原键泄漏到 war3/系统
                            return (IntPtr)1;
                        }
                        else
                        {
                            // 非前台或聊天中：放行原键（不吞）
                            Logger.Write(string.Format("放行: {0} (前台={1} 聊天={2})",
                                Vk.Name(map.Key), _war3.IsForeground(), _war3.IsChat()));
                            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                        }
                    }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private bool TryFindMapping(uint vk, out KeyValuePair<uint, uint> map)
        {
            foreach (KeyValuePair<uint, uint> m in _cfg.Mappings)
            {
                if (m.Key == vk)
                {
                    map = m;
                    return true;
                }
            }
            map = default(KeyValuePair<uint, uint>);
            return false;
        }

        // 主发 keybd_event 真实按键（war3 DirectInput 轮询真实事件最稳）；
        // 可选 PostMessage(WM_KEYDOWN/UP) 加速路径（部分窗口对消息注入敏感时辅助）
        private void SendKey(uint targetVk)
        {
            Native.keybd_event((byte)targetVk, 0, 0, UIntPtr.Zero);
            Native.keybd_event((byte)targetVk, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
            if (_cfg.UsePostMessage && _war3.HWnd != IntPtr.Zero)
            {
                Native.PostMessage(_war3.HWnd, Native.WM_KEYDOWN, (IntPtr)targetVk, IntPtr.Zero);
                Native.PostMessage(_war3.HWnd, Native.WM_KEYUP, (IntPtr)targetVk, IntPtr.Zero);
            }
        }
    }

    // =================================================================
    // 7. 托盘常驻（消息循环驱动低级钩子回调）
    // =================================================================
    internal class TrayContext : ApplicationContext
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private readonly KeybindEngine _engine;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _pollTimer;

        public TrayContext(KeybindConfig cfg, War3Window war3, KeybindEngine engine)
        {
            _cfg = cfg;
            _war3 = war3;
            _engine = engine;

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "War3 改键工具";
            ContextMenuStrip menu = new ContextMenuStrip();
            var re = new ToolStripMenuItem("重新扫描 war3 窗口");
            re.Click += delegate { _war3.Refresh(); };
            menu.Items.Add(re);
            var log = new ToolStripMenuItem("打开日志目录");
            log.Click += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + Logger.LogPath + "\""); }
                catch { }
            };
            menu.Items.Add(log);
            var exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { this.ExitThread(); };
            menu.Items.Add(exit);
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = _cfg.PollIntervalMs;
            _pollTimer.Tick += delegate { _war3.Refresh(); };
            _pollTimer.Start();

            Logger.Write("== War3 改键工具启动 ==");
            Logger.Write(string.Format("Enabled={0} IsChatAvoid={1} UsePostMessage={2} ForegroundCheck={3} 映射数={4}",
                _cfg.Enabled, _cfg.IsChatAvoid, _cfg.UsePostMessage, _cfg.ForegroundCheck, _cfg.Mappings.Count));
            foreach (KeyValuePair<uint, uint> m in _cfg.Mappings)
                Logger.Write(string.Format("  映射: {0} -> {1}", Vk.Name(m.Key), Vk.Name(m.Value)));
            _war3.Refresh();
        }

        protected override void ExitThreadCore()
        {
            _pollTimer.Stop();
            _pollTimer.Dispose();
            _engine.Uninstall();
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            Logger.Write("== 退出 ==");
            base.ExitThreadCore();
        }
    }

    // =================================================================
    // 8. 入口：单实例 -> 配置 -> 钩子 -> 消息循环
    // =================================================================
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 单实例
            bool createdNew;
            using (Mutex m = new Mutex(true, "KeybindTool_KKRE_8F2A1C", out createdNew))
            {
                if (!createdNew)
                {
                    // 已有实例运行（无权限读其日志，直接静默退出）
                    return;
                }

                KeybindConfig cfg = new KeybindConfig();
                KeybindConfig.EnsureDefaultFile();
                cfg.Load();
                Logger.Init(cfg.LogEnabled);
                Logger.Write("启动 PID=" + Process.GetCurrentProcess().Id);

                War3Window war3 = new War3Window(cfg);
                KeybindEngine engine = new KeybindEngine(cfg, war3);
                if (!engine.Install())
                {
                    Logger.Write("钩子安装失败，进程退出");
                    return;
                }

                using (TrayContext ctx = new TrayContext(cfg, war3, engine))
                {
                    Application.Run(ctx);
                }
            }
        }
    }
}
