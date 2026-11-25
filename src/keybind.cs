// keybind.cs — War3 改键工具 v3（单文件 C#, .NET 4.8.1）
// v3 新增（对标 War3 娱乐大师体验）:
//   A. 游戏外直配主路径: 默认 keybind.ini 模板带标准魔兽 12 格布局注释,
//      直接在 [Mappings] 填 源键=目标键, 启动即生效, 无需进游戏录制
//   B. 运行期热重载: FileSystemWatcher 监听 ini 变更(+PollIntervalMs 轮询 mtime 兜底),
//      保存配置即自动 RebuildMappings 即时生效
//   C. 游戏内可改: 托盘菜单[重新加载配置/编辑 keybind.ini(记事本)/切换配置文件...]
//      + 全局热键(默认 Ctrl+Shift+F12=重载, Ctrl+Shift+F11=编辑, ini 可配)
//   D. 日志增强: 每次重载/映射变更写入 keybind_cal.log (AutoFlush 保持)
// 保留 v2 全部功能: WH_KEYBOARD_LL 低级键盘钩子 / IsChat 避让 / LLKHF_INJECTED 防回环 /
//   录制 12 格热键(slotN 兼容: 有 [SlotMap] 用录制表, 没有则直接 VK) / 日志 AutoFlush

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace KeybindTool
{
    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x100;
        public const int WM_KEYUP = 0x101;
        public const int WM_SYSKEYDOWN = 0x104;
        public const int WM_SYSKEYUP = 0x105;
        public const int WM_HOTKEY = 0x0312;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint LLKHF_UP = 0x80;
        public const uint KEYEVENTF_KEYUP = 0x2;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

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
            public uint cbSize;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static string GetWindowTextSafe(IntPtr hWnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetWindowText(hWnd, sb, 256);
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        public static string GetClassNameSafe(IntPtr hWnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                GetClassName(hWnd, sb, 256);
                return sb.ToString();
            }
            catch { return string.Empty; }
        }
    }

    internal static class Vk
    {
        public static bool TryParseSlot(string s, out int n)
        {
            n = 0;
            if (s == null) return false;
            string t = s.Trim().ToLowerInvariant();
            if (!t.StartsWith("slot")) return false;
            int v;
            if (!int.TryParse(t.Substring(4), out v)) return false;
            if (v < 1 || v > 14) return false;
            n = v;
            return true;
        }

        public static bool TryParse(string name, out uint vk)
        {
            vk = 0;
            if (name == null) return false;
            string t = name.Trim();
            if (t.Length == 0) return false;
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                uint v;
                if (uint.TryParse(t.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v))
                {
                    vk = v;
                    return true;
                }
                return false;
            }
            if (t.Length == 1)
            {
                char c = t[0];
                if (c >= 'a' && c <= 'z') { vk = (uint)(c - 'a' + 0x41); return true; }
                if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
                if (c >= '0' && c <= '9') { vk = (uint)c; return true; }
                return false;
            }
            switch (t.ToLowerInvariant())
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
                case "back":
                case "backspace": vk = 0x08; return true;
                case "delete":
                case "del": vk = 0x2E; return true;
                case "insert":
                case "ins": vk = 0x2D; return true;
                case "home": vk = 0x24; return true;
                case "end": vk = 0x23; return true;
                case "pgup":
                case "pageup": vk = 0x21; return true;
                case "pgdn":
                case "pagedown": vk = 0x22; return true;
                case "left": vk = 0x25; return true;
                case "right": vk = 0x27; return true;
                case "up": vk = 0x26; return true;
                case "down": vk = 0x28; return true;
                case "capslock": vk = 0x14; return true;
                case "shift": vk = 0x10; return true;
                case "ctrl":
                case "control": vk = 0x11; return true;
                case "alt": vk = 0x12; return true;
            }
            return false;
        }

        public static string Name(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
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

        // 全局热键解析: "Ctrl+Shift+F12" / "0x7B" / "F12" / "0"=禁用 / "none"=禁用
        public static bool TryParseHotkey(string s, out uint vk, out uint mods)
        {
            vk = 0;
            mods = 0;
            if (s == null) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            if (t == "0" || t.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            string[] parts = t.Split('+');
            if (parts.Length == 0) return false;
            uint kv;
            if (!TryParse(parts[parts.Length - 1].Trim(), out kv)) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim().ToLowerInvariant())
                {
                    case "alt": mods |= Native.MOD_ALT; break;
                    case "ctrl":
                    case "control": mods |= Native.MOD_CONTROL; break;
                    case "shift": mods |= Native.MOD_SHIFT; break;
                    case "win": mods |= Native.MOD_WIN; break;
                    default: return false;
                }
            }
            vk = kv;
            return true;
        }

        public static string FormatHotkey(uint mods, uint vk)
        {
            if (vk == 0) return "禁用";
            StringBuilder sb = new StringBuilder();
            if ((mods & Native.MOD_ALT) != 0) sb.Append("Alt+");
            if ((mods & Native.MOD_CONTROL) != 0) sb.Append("Ctrl+");
            if ((mods & Native.MOD_SHIFT) != 0) sb.Append("Shift+");
            if ((mods & Native.MOD_WIN) != 0) sb.Append("Win+");
            sb.Append(Name(vk));
            return sb.ToString();
        }
    }

    internal static class Logger
    {
        private static readonly object _sync = new object();
        private static StreamWriter _writer;
        private static bool _enabled = true;

        public static string LogDir { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "tools"); } }
        public static string LogPath { get { return Path.Combine(LogDir, "keybind_cal.log"); } }

        public static void Init(bool enabled)
        {
            lock (_sync)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    _writer = new StreamWriter(LogPath, true, new UTF8Encoding(false));
                    _writer.AutoFlush = true;
                    _enabled = enabled;
                }
                catch
                {
                    _writer = null;
                    _enabled = false;
                }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            lock (_sync)
            {
                _enabled = enabled;
            }
        }

        public static void Write(string msg)
        {
            lock (_sync)
            {
                if (!_enabled || _writer == null) return;
                try { _writer.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg); }
                catch { }
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

    internal struct MappingSpec
    {
        public uint Src;
        public bool IsSlot;
        public int SlotN;
        public uint DirectVk;
    }

    internal class KeybindConfig
    {
        // 钩子线程/UI 线程共享配置的同步对象（热重载期间不读半新配置）
        public readonly object SyncRoot = new object();

        public bool Enabled = true;
        public bool IsChatAvoid = true;
        public bool UsePostMessage = false;
        public bool ForegroundCheck = true;
        public bool LogEnabled = true;
        public int PollIntervalMs = 5000;
        public uint RecordHotkey = 0x78;

        // 全局热键（0 = 禁用该热键）; 默认 Ctrl+Shift+F12=重载配置, Ctrl+Shift+F11=编辑配置
        public uint ReloadHotkeyVk = 0x7B;
        public uint ReloadHotkeyMods = 0x6;
        public string ReloadHotkeyDesc = "Ctrl+Shift+F12";
        public uint EditHotkeyVk = 0x7A;
        public uint EditHotkeyMods = 0x6;
        public string EditHotkeyDesc = "Ctrl+Shift+F11";

        // 自身写配置文件期间抑制热重载（避免录制保存触发重载循环）
        public DateTime SuppressReloadUntil = DateTime.MinValue;

        public readonly List<KeyValuePair<uint, uint>> Mappings = new List<KeyValuePair<uint, uint>>();
        public readonly List<string> MappingSource = new List<string>();
        public readonly List<MappingSpec> Specs = new List<MappingSpec>();
        public readonly uint[] SlotMapKey = new uint[15];

        private string _configPath;

        public string ConfigPath
        {
            get
            {
                if (string.IsNullOrEmpty(_configPath))
                    _configPath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "keybind.ini");
                return _configPath;
            }
        }

        public void SetConfigPath(string path)
        {
            _configPath = Path.GetFullPath(path);
        }

        public static readonly string[] DefaultLines = new string[]
        {
            "; War3 改键工具 keybind.ini（v3）",
            "; 主路径 = 游戏外直配: 直接在 [Mappings] 填 源键=目标键, 保存即生效（无需进游戏录制）。",
            "; 保存文件后自动热重载; 也可用托盘菜单[重新加载配置] 或全局热键（默认 Ctrl+Shift+F12）手动重载。",
            "",
            "[keybind]",
            "Enabled=1",
            "IsChatAvoid=1",
            "UsePostMessage=0",
            "ForegroundCheck=1",
            "LogEnabled=1",
            "PollIntervalMs=5000",
            "RecordHotkey=0x78",
            "; 全局热键（游戏内随时可用; 0 或 none 禁用; 语法=修饰键+键名, 如 Ctrl+Shift+F12）:",
            "; reloadhotkey=重新加载配置  edithotkey=用记事本打开本配置",
            "ReloadHotkey=Ctrl+Shift+F12",
            "EditHotkey=Ctrl+Shift+F11",
            "",
            "[Mappings]",
            "; 源键=目标键（默认无映射=直接透传; 取消注释即启用, 保存立即生效）",
            "; 目标键支持三种语法:",
            ";   1) slotN    -> 技能栏第 N 格（普适 12 格, N=1-12; 13/14 预留）",
            ";      触发键 = 该格当前实际热键（各格内容自带 hotkey, 完全动态）,",
            ";      需先录制（见 [SlotMap] 段; 录制后本行立即生效）",
            ";   2) A-Z 0-9 F1-F12 NumPad0-NumPad9（直接键名, 触发该键本身）",
            ";   3) 0xNN（十六进制 VK 码直写）",
            "; ================= 标准魔兽 12 格布局模板 =================",
            "; ---- 技能行（技能栏 1-4 格, 按 Q/W/E/R 触发各格当前实际热键） ----",
            ";Q=slot1",
            ";W=slot2",
            ";E=slot3",
            ";R=slot4",
            "; ---- 物品行（物品栏 1-6 格, 按数字键 1-6 触发各格当前实际热键） ----",
            ";1=slot5",
            ";2=slot6",
            ";3=slot7",
            ";4=slot8",
            ";5=slot9",
            ";6=slot10",
            "; ---- 命令键示例（F5-F8 触发 M=菜单/S=停止/H=驻守/A=攻击） ----",
            ";F5=M",
            ";F6=S",
            ";F7=H",
            ";F8=A",
            "; ---- 直接键名预设示例（按 F1-F4 触发 Q/W/E/R, 立即可用, 无需录制） ----",
            ";F1=Q",
            ";F2=W",
            ";F3=E",
            ";F4=R",
            "",
            "[SlotMap]",
            "; 录制表（模式 A 图内录制: RecordHotkey 进入 -> 依次按 12 格实际热键, 自动保存）:",
            "Slot1=0x00",
            "Slot2=0x00",
            "Slot3=0x00",
            "Slot4=0x00",
            "Slot5=0x00",
            "Slot6=0x00",
            "Slot7=0x00",
            "Slot8=0x00",
            "Slot9=0x00",
            "Slot10=0x00",
            "Slot11=0x00",
            "Slot12=0x00",
        };

        public void EnsureDefaultFile()
        {
            try
            {
                if (File.Exists(ConfigPath)) return;
                File.WriteAllLines(ConfigPath, DefaultLines, new UTF8Encoding(false));
                Logger.Write("已生成默认配置文件: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.Write("生成默认配置文件失败: " + ex.Message);
            }
        }

        public void Load()
        {
            // 解析到局部变量, 最后加锁整体提交, 避免钩子线程读到半新配置
            bool newEnabled = Enabled;
            bool newChat = IsChatAvoid;
            bool newPost = UsePostMessage;
            bool newFg = ForegroundCheck;
            bool newLog = LogEnabled;
            int newPoll = PollIntervalMs;
            uint newRec = RecordHotkey;
            uint newRvk = ReloadHotkeyVk, newRmods = ReloadHotkeyMods;
            string newRdesc = ReloadHotkeyDesc;
            uint newEvk = EditHotkeyVk, newEmods = EditHotkeyMods;
            string newEdesc = EditHotkeyDesc;
            List<MappingSpec> newSpecs = new List<MappingSpec>();
            List<string> newSource = new List<string>();
            uint[] newSlotMap = new uint[15];
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    Logger.Write("配置文件不存在, 保留当前配置: " + ConfigPath);
                    return;
                }
                string[] lines = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                string section = string.Empty;
                foreach (string raw in lines)
                {
                    string trimmed = raw.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed[0] == ';' || trimmed[0] == '#') continue;
                    if (trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                    {
                        section = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                        continue;
                    }
                    int eq = trimmed.IndexOf('=');
                    if (eq < 0) continue;
                    string k = trimmed.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = trimmed.Substring(eq + 1).Trim();
                    if (section == "keybind")
                    {
                        switch (k)
                        {
                            case "enabled": newEnabled = ParseBool(v, Enabled); break;
                            case "ischatavoid": newChat = ParseBool(v, IsChatAvoid); break;
                            case "usepostmessage": newPost = ParseBool(v, UsePostMessage); break;
                            case "foregroundcheck": newFg = ParseBool(v, ForegroundCheck); break;
                            case "logenabled": newLog = ParseBool(v, LogEnabled); break;
                            case "pollintervalms": newPoll = ParseInt(v, PollIntervalMs); break;
                            case "recordhotkey":
                                {
                                    uint rv;
                                    if (Vk.TryParse(v, out rv)) newRec = rv;
                                    break;
                                }
                            case "reloadhotkey":
                                newRdesc = ParseHotkeySetting(v, out newRvk, out newRmods, ReloadHotkeyVk, ReloadHotkeyMods, ReloadHotkeyDesc);
                                break;
                            case "edithotkey":
                                newEdesc = ParseHotkeySetting(v, out newEvk, out newEmods, EditHotkeyVk, EditHotkeyMods, EditHotkeyDesc);
                                break;
                        }
                    }
                    else if (section == "mappings")
                    {
                        MappingSpec spec = new MappingSpec();
                        uint svk;
                        if (!Vk.TryParse(k, out svk))
                        {
                            Logger.Write("配置忽略无效映射行: " + raw);
                            continue;
                        }
                        spec.Src = svk;
                        int slot;
                        if (Vk.TryParseSlot(v, out slot))
                        {
                            spec.IsSlot = true;
                            spec.SlotN = slot;
                        }
                        else
                        {
                            uint dvk;
                            if (!Vk.TryParse(v, out dvk))
                            {
                                Logger.Write("配置忽略无效映射行: " + raw);
                                continue;
                            }
                            spec.DirectVk = dvk;
                        }
                        newSpecs.Add(spec);
                        newSource.Add(raw);
                    }
                    else if (section == "slotmap")
                    {
                        int n;
                        if (Vk.TryParseSlot(k, out n))
                        {
                            uint vk;
                            if (Vk.TryParse(v, out vk)) newSlotMap[n] = vk;
                        }
                    }
                }
                lock (SyncRoot)
                {
                    Enabled = newEnabled;
                    IsChatAvoid = newChat;
                    UsePostMessage = newPost;
                    ForegroundCheck = newFg;
                    LogEnabled = newLog;
                    PollIntervalMs = newPoll;
                    RecordHotkey = newRec;
                    ReloadHotkeyVk = newRvk;
                    ReloadHotkeyMods = newRmods;
                    ReloadHotkeyDesc = newRdesc;
                    EditHotkeyVk = newEvk;
                    EditHotkeyMods = newEmods;
                    EditHotkeyDesc = newEdesc;
                    Specs.Clear();
                    Specs.AddRange(newSpecs);
                    MappingSource.Clear();
                    MappingSource.AddRange(newSource);
                    for (int i = 0; i < SlotMapKey.Length; i++) SlotMapKey[i] = newSlotMap[i];
                    RebuildMappings();
                }
            }
            catch (Exception ex)
            {
                Logger.Write("配置加载异常（保留旧配置）: " + ex.Message);
            }
        }

        public void RebuildMappings()
        {
            lock (SyncRoot)
            {
                Mappings.Clear();
                for (int i = 0; i < Specs.Count; i++)
                {
                    MappingSpec spec = Specs[i];
                    uint dst = spec.IsSlot
                        ? ((spec.SlotN >= 1 && spec.SlotN < SlotMapKey.Length) ? SlotMapKey[spec.SlotN] : 0)
                        : spec.DirectVk;
                    if (dst != 0)
                        Mappings.Add(new KeyValuePair<uint, uint>(spec.Src, dst));
                }
            }
        }

        public void SaveSlotMap()
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(ConfigPath))
                {
                    string[] src = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                    bool inSlot = false;
                    foreach (string raw in src)
                    {
                        string trimmed = raw.Trim();
                        if (trimmed.Length > 0 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                        {
                            string sec = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                            inSlot = (sec == "slotmap");
                            if (inSlot) continue; // 跳过旧段头
                        }
                        if (inSlot) continue;
                        lines.Add(raw);
                    }
                }
                else
                {
                    lines.Add("[keybind]");
                    lines.Add("Enabled=1");
                    lines.Add("");
                }
                lines.Add("");
                lines.Add("[SlotMap]");
                lines.Add("; 录制表（模式 A 图内录制: RecordHotkey 进入 -> 依次按 12 格实际热键, 自动保存）:");
                for (int i = 1; i <= 12; i++)
                    lines.Add("Slot" + i + "=" + ("0x" + SlotMapKey[i].ToString("X2")));
                // 先抑制热重载再写盘（事件异步到达）
                SuppressReloadUntil = DateTime.Now.AddSeconds(1.5);
                File.WriteAllLines(ConfigPath, lines.ToArray(), new UTF8Encoding(false));
                Logger.Write("SlotMap 已写入: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.Write("SlotMap 写入失败: " + ex.Message);
            }
        }

        private static bool ParseBool(string s, bool def)
        {
            bool v;
            if (bool.TryParse(s, out v)) return v;
            int i;
            if (int.TryParse(s, out i)) return i != 0;
            return def;
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            if (int.TryParse(s, out v)) return v;
            return def;
        }

        private static string ParseHotkeySetting(string s, out uint vk, out uint mods, uint defVk, uint defMods, string defDesc)
        {
            uint v, m;
            if (Vk.TryParseHotkey(s, out v, out m))
            {
                vk = v;
                mods = m;
                return (v == 0) ? "禁用" : Vk.FormatHotkey(m, v);
            }
            vk = defVk;
            mods = defMods;
            return defDesc;
        }
    }

    internal class War3Window
    {
        private readonly KeybindConfig _cfg;
        public IntPtr HWnd = IntPtr.Zero;
        public bool WasFound = false;
        private bool _chatOpen = false;

        public War3Window(KeybindConfig cfg)
        {
            _cfg = cfg;
        }

        public void Refresh()
        {
            IntPtr best = IntPtr.Zero;
            int bestScore = 0;
            Process[] procs = null;
            try
            {
                procs = Process.GetProcesses();
                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.MainWindowHandle == IntPtr.Zero) continue;
                        string name = p.ProcessName;
                        string title = p.MainWindowTitle;
                        int score = 0;
                        if (name.Equals("war3", StringComparison.OrdinalIgnoreCase)) score += 2;
                        if (title != null && title.IndexOf("Warcraft III", StringComparison.OrdinalIgnoreCase) >= 0) score += 1;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = p.MainWindowHandle;
                        }
                        if (score >= 3) break;
                    }
                    catch { }
                }
            }
            finally
            {
                if (procs != null)
                {
                    foreach (Process p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            bool changed = (HWnd != best);
            HWnd = best;
            WasFound = (best != IntPtr.Zero);
            if (changed)
            {
                if (WasFound) Logger.Write("检测到 war3 窗口: 0x" + HWnd.ToString("X"));
                else Logger.Write("war3 窗口未找到（等待游戏启动）");
            }
        }

        public bool IsForeground()
        {
            if (!_cfg.ForegroundCheck) return true;
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == HWnd) return true;
                if (HWnd != IntPtr.Zero)
                {
                    uint fgPid, myPid;
                    Native.GetWindowThreadProcessId(fg, out fgPid);
                    Native.GetWindowThreadProcessId(HWnd, out myPid);
                    if (fgPid == myPid) return true;
                }
            }
            catch { }
            return false;
        }

        public void OnEnterKey()
        {
            _chatOpen = !_chatOpen;
        }

        public void OnEscapeKey()
        {
            _chatOpen = false;
        }

        public bool IsChat()
        {
            if (!_cfg.IsChatAvoid) return false;
            if (_chatOpen) return true;
            try
            {
                Native.GUITHREADINFO info = new Native.GUITHREADINFO();
                info.cbSize = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                uint tid;
                Native.GetWindowThreadProcessId(HWnd, out tid);
                if (tid != 0 && Native.GetGUIThreadInfo(tid, ref info))
                {
                    if (info.hwndFocus != HWnd) return true;
                }
            }
            catch { }
            return false;
        }
    }

    internal class KeybindEngine
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private IntPtr _hook;
        private readonly Native.LowLevelKeyboardProc _proc;
        private bool _recording = false;
        private int _recIndex = 0;

        public bool Recording { get { return _recording; } }

        public KeybindEngine(KeybindConfig cfg, War3Window war3)
        {
            _cfg = cfg;
            _war3 = war3;
            _proc = HookCallback;
        }

        public bool Install()
        {
            try
            {
                IntPtr hMod = Native.GetModuleHandle(Process.GetCurrentProcess().MainModule.ModuleName);
                _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, hMod, 0);
            }
            catch (Exception ex)
            {
                Logger.Write("WH_KEYBOARD_LL 钩子安装失败: " + ex.Message);
                return false;
            }
            if (_hook == IntPtr.Zero)
            {
                Logger.Write("WH_KEYBOARD_LL 钩子安装失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
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
            }
        }

        public bool StartRecording()
        {
            if (_recording) return false;
            _recording = true;
            _recIndex = 1;
            Logger.Write("进入录制模式: 依次按技能栏 12 格的实际热键（Esc 取消）");
            return true;
        }

        private void CancelRecording()
        {
            _recording = false;
            Logger.Write("录制已取消");
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _cfg.Enabled && _hook != IntPtr.Zero)
            {
                Native.KBDLLHOOKSTRUCT kbd = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                bool down = (kbd.flags & Native.LLKHF_UP) == 0;
                uint vk = kbd.vkCode;

                // 1) 录制分支（最优先）
                if (_recording)
                {
                    if (!down) return (IntPtr)1;
                    if (vk == 0x1B)
                    {
                        CancelRecording();
                        return (IntPtr)1;
                    }
                    if (_war3.IsChat()) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    if (_recIndex <= 12)
                    {
                        _cfg.SlotMapKey[_recIndex] = vk;
                        _recIndex++;
                        if (_recIndex > 12)
                        {
                            _recording = false;
                            _cfg.SaveSlotMap();
                            _cfg.RebuildMappings();
                            Logger.Write("录制完成: 12 格热键已保存, 生效映射 " + _cfg.Mappings.Count + " 条");
                        }
                    }
                    return (IntPtr)1;
                }

                // 2) 录制热键触发
                if (down && _cfg.RecordHotkey != 0 && vk == _cfg.RecordHotkey && !_war3.IsChat())
                {
                    StartRecording();
                    return (IntPtr)1;
                }

                // 3) 主映射分支（软件注入键带 LLKHF_INJECTED 标志, 跳过防回环）
                if ((kbd.flags & Native.LLKHF_INJECTED) == 0)
                {
                    if (_cfg.IsChatAvoid)
                    {
                        if (vk == 0x0D) _war3.OnEnterKey();
                        else if (vk == 0x1B) _war3.OnEscapeKey();
                    }
                    KeyValuePair<uint, uint> map;
                    if (TryFindMapping(vk, out map))
                    {
                        bool fg = _war3.IsForeground();
                        bool chat = _war3.IsChat();
                        if (fg && !chat)
                        {
                            if (down)
                            {
                                SendKey(map.Value);
                                Logger.Write(string.Format("改键: {0}(0x{1:X2}) -> {2}(0x{3:X2}) DOWN", Vk.Name(vk), vk, Vk.Name(map.Value), map.Value));
                            }
                            return (IntPtr)1;
                        }
                        Logger.Write("放行: " + Vk.Name(vk) + " (前台=" + fg + " 聊天=" + chat + ")");
                    }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        private bool TryFindMapping(uint vk, out KeyValuePair<uint, uint> map)
        {
            lock (_cfg.SyncRoot)
            {
                foreach (KeyValuePair<uint, uint> m in _cfg.Mappings)
                {
                    if (m.Key == vk)
                    {
                        map = m;
                        return true;
                    }
                }
            }
            map = default(KeyValuePair<uint, uint>);
            return false;
        }

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

    // 全局热键接收窗口（RegisterHotKey 需要消息循环窗口）
    internal class HotkeyWindow : NativeWindow
    {
        private const int WM_HOTKEY = 0x0312;
        public Action<int> HotkeyPressed;

        public void CreateHandleIfNeeded()
        {
            if (Handle == IntPtr.Zero)
                CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (HotkeyPressed != null) HotkeyPressed(id);
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal class TrayContext : ApplicationContext
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private readonly KeybindEngine _engine;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly System.Windows.Forms.Timer _reloadTimer;
        private FileSystemWatcher _watcher;
        private HotkeyWindow _hotkeyWin;
        private DateTime _lastMtime;
        private DateTime _lastChangeTime = DateTime.MinValue;
        private bool _reloadPending = false;
        private const int ReloadQuietMs = 400; // 编辑器保存过程中的多事件抖动抑制

        public TrayContext(KeybindConfig cfg, War3Window war3, KeybindEngine engine)
        {
            _cfg = cfg;
            _war3 = war3;
            _engine = engine;

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "War3 改键工具";
            ContextMenuStrip menu = new ContextMenuStrip();

            var scanItem = new ToolStripMenuItem("重新扫描 war3 窗口");
            scanItem.Click += delegate { _war3.Refresh(); };
            menu.Items.Add(scanItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var reloadItem = new ToolStripMenuItem("重新加载配置");
            reloadItem.Click += delegate { ReloadConfig("托盘菜单"); };
            menu.Items.Add(reloadItem);

            var editItem = new ToolStripMenuItem("编辑 keybind.ini（记事本）");
            editItem.Click += delegate { OpenEditor(); };
            menu.Items.Add(editItem);

            var switchItem = new ToolStripMenuItem("切换配置文件...");
            switchItem.Click += delegate { ShowSwitchDialog(); };
            menu.Items.Add(switchItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var logItem = new ToolStripMenuItem("打开日志目录");
            logItem.Click += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + Logger.LogPath + "\""); }
                catch (Exception ex) { Logger.Write("打开日志目录失败: " + ex.Message); }
            };
            menu.Items.Add(logItem);

            var recItem = new ToolStripMenuItem("录制 12 格热键（进图后使用）");
            recItem.Click += delegate
            {
                if (_engine.StartRecording())
                    Notify("录制模式: 依次按技能栏 12 格的实际热键（Esc 取消）");
            };
            menu.Items.Add(recItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;

            // 全局热键（消息循环就绪后注册）
            ApplyHotkeys();

            // 配置热重载: 事件监听 + 去抖定时器 + mtime 轮询兜底
            _reloadTimer = new System.Windows.Forms.Timer();
            _reloadTimer.Interval = 500;
            _reloadTimer.Tick += OnReloadTimerTick;
            _reloadTimer.Start();
            _lastMtime = GetConfigMtime();
            SetupWatcher();

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = Math.Max(200, _cfg.PollIntervalMs);
            _pollTimer.Tick += delegate
            {
                _war3.Refresh();
                if (_watcher == null) CheckConfigMtime();
            };
            _pollTimer.Start();

            Logger.Write("== War3 改键工具 v3 启动 ==");
            Logger.Write(string.Format("Enabled={0} IsChatAvoid={1} UsePostMessage={2} ForegroundCheck={3} RecordHotkey={4} 映射数={5}",
                _cfg.Enabled, _cfg.IsChatAvoid, _cfg.UsePostMessage, _cfg.ForegroundCheck,
                (_cfg.RecordHotkey != 0) ? Vk.Name(_cfg.RecordHotkey) + "(0x" + _cfg.RecordHotkey.ToString("X2") + ")" : "无",
                _cfg.Mappings.Count));
            Logger.Write(string.Format("热键: 重载={0} 编辑={1}", _cfg.ReloadHotkeyDesc, _cfg.EditHotkeyDesc));
            LogMappingDetails("  映射");
            _war3.Refresh();
        }

        private void SetupWatcher()
        {
            try
            {
                if (_watcher != null)
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Dispose();
                    _watcher = null;
                }
                string dir = Path.GetDirectoryName(_cfg.ConfigPath);
                string file = Path.GetFileName(_cfg.ConfigPath);
                if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
                {
                    Logger.Write("配置文件监听未启用: 路径无效, 改用轮询检测");
                    return;
                }
                FileSystemWatcher w = new FileSystemWatcher(dir, file);
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                w.Changed += OnConfigFileChanged;
                w.Created += OnConfigFileChanged;
                w.Renamed += OnConfigFileChanged;
                w.Error += delegate
                {
                    try { w.EnableRaisingEvents = false; } catch { }
                    if (_watcher == w) _watcher = null;
                    Logger.Write("配置文件监听错误, 降级为轮询检测");
                };
                w.EnableRaisingEvents = true;
                _watcher = w;
                Logger.Write("配置文件监听已启用: " + _cfg.ConfigPath + "（变更自动热重载）");
            }
            catch (Exception ex)
            {
                Logger.Write("配置文件监听初始化失败, 改用轮询检测: " + ex.Message);
                _watcher = null;
            }
        }

        private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (DateTime.Now < _cfg.SuppressReloadUntil) return;
            _lastChangeTime = DateTime.Now;
            _reloadPending = true;
        }

        private void OnReloadTimerTick(object sender, EventArgs e)
        {
            if (!_reloadPending) return;
            if ((DateTime.Now - _lastChangeTime).TotalMilliseconds < ReloadQuietMs) return;
            _reloadPending = false;
            ReloadConfig("配置文件变更");
        }

        private void CheckConfigMtime()
        {
            try
            {
                if (DateTime.Now < _cfg.SuppressReloadUntil) return;
                if (!File.Exists(_cfg.ConfigPath)) return;
                DateTime mt = File.GetLastWriteTime(_cfg.ConfigPath);
                if (mt != _lastMtime)
                {
                    _lastMtime = mt;
                    ReloadConfig("轮询检测到配置变更");
                }
            }
            catch { }
        }

        private DateTime GetConfigMtime()
        {
            try
            {
                if (File.Exists(_cfg.ConfigPath)) return File.GetLastWriteTime(_cfg.ConfigPath);
            }
            catch { }
            return DateTime.MinValue;
        }

        private void ReloadConfig(string reason)
        {
            if (_engine.Recording)
            {
                Logger.Write("录制进行中, 跳过配置重载（" + reason + "）");
                return;
            }
            int before, after;
            lock (_cfg.SyncRoot)
            {
                before = _cfg.Mappings.Count;
                _cfg.Load();
            }
            lock (_cfg.SyncRoot)
            {
                after = _cfg.Mappings.Count;
            }
            _pollTimer.Interval = Math.Max(200, _cfg.PollIntervalMs);
            ApplyHotkeys();
            _lastMtime = GetConfigMtime();
            Logger.Write(string.Format("配置已重载（{0}）: 生效映射 {1} 条（原 {2} 条）", reason, after, before));
            LogMappingDetails("  映射");
            Notify(string.Format("配置已重载: {0} 条映射生效", after));
        }

        private void LogMappingDetails(string prefix)
        {
            lock (_cfg.SyncRoot)
            {
                for (int i = 0; i < _cfg.Specs.Count; i++)
                {
                    MappingSpec spec = _cfg.Specs[i];
                    string line = (i < _cfg.MappingSource.Count) ? _cfg.MappingSource[i] : (Vk.Name(spec.Src) + " -> ?");
                    if (spec.IsSlot)
                    {
                        uint vk = (spec.SlotN >= 1 && spec.SlotN < _cfg.SlotMapKey.Length) ? _cfg.SlotMapKey[spec.SlotN] : 0;
                        if (vk != 0)
                            Logger.Write(string.Format("{0}: {1}  (目标格 {2} = {3} 0x{4:X2})", prefix, line, spec.SlotN, Vk.Name(vk), vk));
                        else
                            Logger.Write(string.Format("{0}: {1}  (目标格 {2} 未录制, 录制后生效)", prefix, line, spec.SlotN));
                    }
                    else
                    {
                        Logger.Write(string.Format("{0}: {1}  (目标 {2} 0x{3:X2})", prefix, line, Vk.Name(spec.DirectVk), spec.DirectVk));
                    }
                }
            }
        }

        private void ApplyHotkeys()
        {
            if (_hotkeyWin == null)
            {
                _hotkeyWin = new HotkeyWindow();
                _hotkeyWin.CreateHandleIfNeeded();
                _hotkeyWin.HotkeyPressed = OnHotkeyPressed;
            }
            Native.UnregisterHotKey(_hotkeyWin.Handle, 1);
            Native.UnregisterHotKey(_hotkeyWin.Handle, 2);
            if (_cfg.ReloadHotkeyVk != 0)
            {
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 1, _cfg.ReloadHotkeyMods, _cfg.ReloadHotkeyVk))
                    Logger.Write("重载热键注册失败（可能被其他程序占用）: " + _cfg.ReloadHotkeyDesc);
            }
            if (_cfg.EditHotkeyVk != 0)
            {
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 2, _cfg.EditHotkeyMods, _cfg.EditHotkeyVk))
                    Logger.Write("编辑热键注册失败（可能被其他程序占用）: " + _cfg.EditHotkeyDesc);
            }
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == 1)
                ReloadConfig("热键 " + _cfg.ReloadHotkeyDesc);
            else if (id == 2)
                OpenEditor();
        }

        private void OpenEditor()
        {
            try { Process.Start("notepad.exe", "\"" + _cfg.ConfigPath + "\""); }
            catch (Exception ex) { Logger.Write("打开编辑器失败: " + ex.Message); }
        }

        private void ShowSwitchDialog()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "选择 keybind 配置文件";
                dlg.Filter = "keybind 配置文件 (*.ini)|*.ini|所有文件 (*.*)|*.*";
                try { dlg.InitialDirectory = Path.GetDirectoryName(_cfg.ConfigPath); } catch { }
                if (dlg.ShowDialog() == DialogResult.OK)
                    SwitchConfig(dlg.FileName);
            }
        }

        private void SwitchConfig(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                _cfg.SetConfigPath(full);
                if (!File.Exists(full))
                    _cfg.EnsureDefaultFile();
                _reloadPending = false;
                SetupWatcher();
                ReloadConfig("切换配置文件 " + Path.GetFileName(full));
            }
            catch (Exception ex)
            {
                Logger.Write("切换配置文件失败: " + ex.Message);
            }
        }

        private void Notify(string text)
        {
            try
            {
                _tray.BalloonTipTitle = "War3 改键工具";
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(1500);
            }
            catch { }
        }

        protected override void ExitThreadCore()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer.Dispose();
            }
            if (_reloadTimer != null)
            {
                _reloadTimer.Stop();
                _reloadTimer.Dispose();
            }
            if (_watcher != null)
            {
                try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { }
                _watcher = null;
            }
            if (_hotkeyWin != null)
            {
                try { Native.UnregisterHotKey(_hotkeyWin.Handle, 1); } catch { }
                try { Native.UnregisterHotKey(_hotkeyWin.Handle, 2); } catch { }
                try { _hotkeyWin.DestroyHandle(); } catch { }
                _hotkeyWin = null;
            }
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

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool createdNew;
            Mutex mutex = new Mutex(true, "KeybindTool_KKRE_8F2A1C", out createdNew);
            if (!createdNew) return; // 单实例

            KeybindConfig cfg = new KeybindConfig();
            cfg.EnsureDefaultFile();
            Logger.Init(true);            // 先开日志: 让 Load 阶段的警告也能落盘
            cfg.Load();
            Logger.SetEnabled(cfg.LogEnabled);
            Logger.Write("启动 PID=" + Process.GetCurrentProcess().Id);

            War3Window war3 = new War3Window(cfg);
            KeybindEngine engine = new KeybindEngine(cfg, war3);
            if (!engine.Install()) return;
            using (TrayContext ctx = new TrayContext(cfg, war3, engine))
            {
                Application.Run(ctx);
            }
        }
    }
}
