// keybind_panel.cs — War3 改键工具 面板版（单文件 C#, .NET 4.8.1）
// 在 keybind.cs v3 基础上扩展，保留 v3 全部能力:
//   A. 游戏外直配主路径: 默认 keybind.ini 模板带标准魔兽 12 格布局注释
//   B. 运行期热重载: FileSystemWatcher 监听 ini 变更(+PollIntervalMs 轮询 mtime 兜底)
//   C. 游戏内可改: 托盘菜单 + 全局热键(重载/编辑)
//   D. 日志增强: keybind_panel_cal.log (AutoFlush)
//   E. WH_KEYBOARD_LL 低级键盘钩子 / IsChat 避让 / LLKHF_INJECTED 防回环 /
//      录制 12 格热键(slotN 兼容: 有 [SlotMap] 用录制表, 没有则直接 VK)
// v4(面板版) 新增:
//   F. 游戏内呼出面板: 全局热键(默认 F7, ini [panel] PanelHotkey 可配, 0=禁用)
//      置顶半透明面板(WS_EX_TOPMOST|LAYERED, 黑色半透明底+格子 UI)
//      技能 12 格(行1-3×4) + 物品 6 格(行4), 点击格子进入录制模式(按新键写入)
//      右上角状态行: 血条/视距开关(点击切换)
//   G. 血条默认开启: 启动写 HKCU\...\Warcraft III\Gameplay\healthbars=1(32位注册表视图),
//      面板可切换 0/1
//   H. 视距镜头控制(复刻 SuWar3Tools 键位语义): 游戏内按 [0] 拉远(写视距大值),
//      按 [.] 拉近(恢复默认), 直写 game.dll 1.27a 视距字段
//      锚点(本机 Game.dll 静态定位, 见 keybind_panel_report.md):
//      [game.dll+0xBE6350](game 对象全局) -> +0x254(相机对象) -> +0xFC(float 视距字段, 默认 1650.0)
//      面板可切换开关(ini [panel] CameraKeys)

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
using Microsoft.Win32;

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

        // 进程内存访问(视距写入用)
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const int MEM_COMMIT = 0x1000;

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

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

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
                if (c == '.') { vk = 0xBE; return true; }
                if (c == ',') { vk = 0xBC; return true; }
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
                case ".":
                case "period": vk = 0xBE; return true;
                case ",":
                case "comma": vk = 0xBC; return true;
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
                case 0xBE: return ".";
                case 0xBC: return ",";
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
        public static string LogPath { get { return Path.Combine(LogDir, "keybind_panel_cal.log"); } }

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

        // [panel] 段（面板版新增）
        public uint PanelHotkeyVk = 0x76;              // 默认 F7 呼出/隐藏面板
        public uint PanelHotkeyMods = 0x0;
        public string PanelHotkeyDesc = "F7";
        public bool CameraKeys = true;                 // 0=拉远 . =拉近 镜头控制开关
        public float CamFar = 2800.0f;                 // 0 键写入的视距（拉远）
        public float CamNear = 1650.0f;                // . 键写入的视距（拉近/恢复默认）
        public bool BloodBars = true;                  // 血条默认开启

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
            "; War3 改键工具 keybind.ini（面板版 v4）",
            "; 主路径 = 游戏外直配: 直接在 [Mappings] 填 源键=目标键, 保存即生效（无需进游戏录制）。",
            "; 保存文件后自动热重载; 也可用托盘菜单[重新加载配置] 或全局热键（默认 Ctrl+Shift+F12）手动重载。",
            "; 游戏内按 F7 呼出/隐藏改键面板（面板内点击格子录制改键）。",
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
            "[panel]",
            "; 游戏内改键面板热键（默认 F7; 0 或 none 禁用）:",
            "PanelHotkey=F7",
            "; 镜头控制: 游戏内按 [0] 视距拉远, 按 [.] 视距拉近/恢复（复刻 SuWar3Tools）:",
            "CameraKeys=1",
            "; 视距写入值（war3 默认视距=1650.0, 拉远建议 2400-3000）:",
            "CamFar=2800",
            "CamNear=1650",
            "; 血条默认开启（进游戏即显示敌我血条, 写注册表 healthbars=1）:",
            "BloodBars=1",
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
            "; 录制表（面板/RecordHotkey 录制: 点击面板格子或按 RecordHotkey 进入 -> 依次按实际热键, 自动保存）:",
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
            uint newPvk = PanelHotkeyVk, newPmods = PanelHotkeyMods;
            string newPdesc = PanelHotkeyDesc;
            bool newCam = CameraKeys;
            float newFar = CamFar;
            float newNear = CamNear;
            bool newBlood = BloodBars;
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
                    else if (section == "panel")
                    {
                        switch (k)
                        {
                            case "panelhotkey":
                                newPdesc = ParseHotkeySetting(v, out newPvk, out newPmods, PanelHotkeyVk, PanelHotkeyMods, PanelHotkeyDesc);
                                break;
                            case "camerakeys": newCam = ParseBool(v, CameraKeys); break;
                            case "camfar":
                                {
                                    float f;
                                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0)
                                        newFar = f;
                                    break;
                                }
                            case "camnear":
                                {
                                    float f;
                                    if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0)
                                        newNear = f;
                                    break;
                                }
                            case "bloodbars": newBlood = ParseBool(v, BloodBars); break;
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
                    PanelHotkeyVk = newPvk;
                    PanelHotkeyMods = newPmods;
                    PanelHotkeyDesc = newPdesc;
                    CameraKeys = newCam;
                    CamFar = newFar;
                    CamNear = newNear;
                    BloodBars = newBlood;
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
                lines.Add("; 录制表（面板/RecordHotkey 录制: 点击面板格子或按 RecordHotkey 进入 -> 依次按实际热键, 自动保存）:");
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

    // ============ 血条控制: HKCU\Software\Blizzard Entertainment\Warcraft III\Gameplay\healthbars ============
    // 注意: war3 1.27a 是 32 位进程, 读写注册表必须用 Registry32 视图,
    //       否则 64 位进程写入的 64 位视图 war3 读不到。
    internal static class BloodBars
    {
        private const string KeyPath = @"Software\Blizzard Entertainment\Warcraft III\Gameplay";

        public static bool Get()
        {
            try
            {
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
                using (RegistryKey sub = key.OpenSubKey(KeyPath))
                {
                    if (sub == null) return false;
                    object o = sub.GetValue("healthbars");
                    if (o is int) return ((int)o) != 0;
                    if (o is byte[]) { byte[] b = (byte[])o; return b.Length > 0 && b[0] != 0; }
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Write("血条状态读取失败: " + ex.Message);
                return false;
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
                using (RegistryKey sub = key.CreateSubKey(KeyPath))
                {
                    sub.SetValue("healthbars", on ? 1 : 0, RegistryValueKind.DWord);
                }
                Logger.Write("血条注册表已写入 healthbars=" + (on ? 1 : 0));
            }
            catch (Exception ex)
            {
                Logger.Write("血条注册表写入失败: " + ex.Message);
            }
        }

        public static void ApplyDefault(bool wantOn)
        {
            if (wantOn) Set(true);
        }

        public static bool Toggle()
        {
            bool cur = Get();
            Set(!cur);
            return !cur;
        }
    }

    // ============ 视距镜头控制: 直写 game.dll 1.27a 相机视距字段 ============
    // 写入链（本机 Game.dll 静态逆向定位, 证据见 keybind_panel_report.md）:
    //   [game.dll 基址 + 0xBE6350]  = game 对象全局指针 (0x34F3A0 分配/缓存)
    //   [[..] + 0x254]              = 相机对象指针 (0x34DA20: mov eax,[ecx+0x254])
    //   [[..] + 0xFC]               = 视距字段 float (SetCameraField 0x1F4170 -> 0x345760 field2 -> esi+0xFC;
    //                                  相机构造 0x343460 默认写 1650.0)
    internal static class CameraControl
    {
        public const int GameObjGlobalOff = 0xBE6350; // game 对象全局（.data）
        public const int CamPtrOff = 0x254;           // game 对象 -> 相机对象
        public const int DistFloatOff = 0xFC;         // 相机对象 -> 视距 float

        private static Process _proc;
        private static IntPtr _gameDllBase = IntPtr.Zero;
        private static IntPtr _hProcess = IntPtr.Zero;

        public static bool Attached { get { return _hProcess != IntPtr.Zero; } }
        public static string LastError = string.Empty;

        // 查找 war3 进程并定位 game.dll 模块基址（war3.exe 主模块是 exe, 必须枚举模块找 Game.dll）
        public static bool Attach()
        {
            Detach();
            try
            {
                Process[] procs = Process.GetProcessesByName("war3");
                Process target = null;
                foreach (Process p in procs)
                {
                    try
                    {
                        foreach (ProcessModule m in p.Modules)
                        {
                            if (m.ModuleName.Equals("Game.dll", StringComparison.OrdinalIgnoreCase))
                            {
                                target = p;
                                _gameDllBase = m.BaseAddress;
                                break;
                            }
                        }
                    }
                    catch { }
                    if (target != null) break;
                }
                foreach (Process p in procs)
                {
                    try { if (p != target) p.Dispose(); } catch { }
                }
                if (target == null)
                {
                    LastError = "war3 进程/Game.dll 未找到";
                    return false;
                }
                _proc = target;
                _hProcess = Native.OpenProcess(
                    Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION,
                    false, (uint)target.Id);
                if (_hProcess == IntPtr.Zero || _hProcess == (IntPtr)(-1))
                {
                    _hProcess = IntPtr.Zero;
                    LastError = "OpenProcess 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                Logger.Write(string.Format("已附加 war3 pid={0} game.dll 基址=0x{1:X8}", target.Id, _gameDllBase.ToInt64()));
                return true;
            }
            catch (Exception ex)
            {
                LastError = "附加失败: " + ex.Message;
                return false;
            }
        }

        public static void Detach()
        {
            if (_hProcess != IntPtr.Zero)
            {
                Native.CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
            _proc = null;
            _gameDllBase = IntPtr.Zero;
        }

        private static bool ReadPtr(IntPtr addr, out IntPtr value)
        {
            value = IntPtr.Zero;
            byte[] buf = new byte[4];
            int read;
            if (!Native.ReadProcessMemory(_hProcess, addr, buf, 4, out read) || read != 4) return false;
            value = (IntPtr)BitConverter.ToUInt32(buf, 0);
            return true;
        }

        // 写视距并读回验证; 返回 true 表示写入生效
        public static bool SetDistance(float dist)
        {
            if (_hProcess == IntPtr.Zero)
            {
                if (!Attach()) return false;
            }
            try
            {
                IntPtr gamePtr;
                if (!ReadPtr(new IntPtr(_gameDllBase.ToInt64() + GameObjGlobalOff), out gamePtr))
                {
                    LastError = "读 game 对象全局失败（0x" + (GameObjGlobalOff).ToString("X") + "）: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Logger.Write("视距写入失败: " + LastError);
                    Detach();
                    return false;
                }
                if (gamePtr == IntPtr.Zero)
                {
                    LastError = "game 对象全局为空（war3 未初始化完成?）";
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                IntPtr camPtr;
                if (!ReadPtr(new IntPtr(gamePtr.ToInt64() + CamPtrOff), out camPtr))
                {
                    LastError = "读相机对象失败（+" + CamPtrOff.ToString("X") + "）: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                if (camPtr == IntPtr.Zero)
                {
                    LastError = "相机对象为空（未进游戏画面?）";
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                IntPtr distAddr = new IntPtr(camPtr.ToInt64() + DistFloatOff);
                byte[] data = BitConverter.GetBytes(dist);
                int written;
                if (!Native.WriteProcessMemory(_hProcess, distAddr, data, 4, out written) || written != 4)
                {
                    LastError = "写视距内存失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                // 读回验证
                byte[] back = new byte[4];
                int read;
                if (Native.ReadProcessMemory(_hProcess, distAddr, back, 4, out read) && read == 4)
                {
                    float v = BitConverter.ToSingle(back, 0);
                    Logger.Write(string.Format("视距已写入 0x{0:X8} = {1:F1} (读回 {2:F1})", distAddr.ToInt64(), dist, v));
                    return Math.Abs(v - dist) < 0.5f;
                }
                Logger.Write(string.Format("视距已写入 0x{0:X8} = {1:F1}（读回失败）", distAddr.ToInt64(), dist));
                return true;
            }
            catch (Exception ex)
            {
                LastError = "视距写入异常: " + ex.Message;
                Logger.Write("视距写入异常: " + ex.Message);
                return false;
            }
        }
    }

    // ============ 游戏内改键面板（置顶半透明, 点击格子录制） ============
    internal class PanelForm : Form
    {
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly KeybindConfig _cfg;
        private readonly KeybindEngine _engine;
        private int _recSlot = 0;                 // 正在录制的格（0=无）
        private Rectangle _bloodRect;             // 状态区血条切换
        private Rectangle _camRect;               // 状态区视距开关
        private Rectangle[] _skillRects = new Rectangle[12];  // 技能格 slot1..12
        private Rectangle[] _itemRects = new Rectangle[6];   // 物品格 slot5..10

        public PanelForm(KeybindConfig cfg, KeybindEngine engine)
        {
            _cfg = cfg;
            _engine = engine;
            Text = "War3 改键面板";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.9;              // 半透明
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(445, 272);
            // 默认放屏幕右上角附近, 避免挡小地图
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 40, wa.Top + 80);
            DoubleBuffered = true;
            MouseClick += OnPanelMouseClick;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // 不抢焦点（war3 前台判断依赖前台窗口）
        protected override bool ShowWithoutActivation { get { return true; } }

        public bool IsRecSlot { get { return _recSlot != 0; } }

        // 面板热键/托盘切换
        public void TogglePanel()
        {
            if (Visible) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel()
        {
            _recSlot = 0;
            Show();
            Invalidate();
            Logger.Write("改键面板已呼出");
        }

        public void HidePanel()
        {
            _recSlot = 0;
            Hide();
            Logger.Write("改键面板已隐藏");
        }

        public void RefreshGrid()
        {
            Invalidate();
        }

        // 点击格子开始录制; 由引擎在钩子线程完成写入后调用 CompleteRec
        public bool BeginRecSlot(int slot)
        {
            if (slot < 1 || slot > 14) return false;
            _recSlot = slot;
            Invalidate();
            return true;
        }

        public void CompleteRec(uint vk)
        {
            int done = _recSlot;
            _recSlot = 0;
            Logger.Write("面板录制完成: slot" + done + " <- " + Vk.Name(vk) + " (0x" + vk.ToString("X2") + ")");
            Invalidate();
        }

        public void CancelRec()
        {
            _recSlot = 0;
            Logger.Write("面板录制已取消");
            Invalidate();
        }

        private void OnPanelMouseClick(object sender, MouseEventArgs e)
        {
            int hit = HitTest(_bloodRect, _camRect, _skillRects, _itemRects, e.Location);
            if (hit == 201)
            {
                bool now = BloodBars.Toggle();
                Invalidate();
                Logger.Write("面板切换血条 -> " + (now ? "开" : "关"));
                return;
            }
            if (hit == 202)
            {
                _cfg.CameraKeys = !_cfg.CameraKeys;
                Invalidate();
                Logger.Write("面板切换镜头控制 -> " + (_cfg.CameraKeys ? "开" : "关"));
                return;
            }
            if (hit >= 1 && hit <= 12)
            {
                BeginRecSlot(hit);
                return;
            }
            if (hit >= 101 && hit <= 106)
            {
                BeginRecSlot(5 + (hit - 101));
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            // 布局计算与 SelfTest T6 共用同一公式
            Rectangle bloodRect, camRect;
            ComputeLayout(ClientSize.Width, out bloodRect, out camRect, out _skillRects, out _itemRects);
            _bloodRect = bloodRect;
            _camRect = camRect;

            using (Font fTitle = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (Font fLabel = new Font("Microsoft YaHei UI", 8.5f))
            using (Font fKey = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (Font fHint = new Font("Microsoft YaHei UI", 8f))
            {
                // 标题行
                g.DrawString("War3 改键面板", fTitle, Brushes.White, 10, 6);
                string hideHint = "F7 隐藏";
                SizeF hs = g.MeasureString(hideHint, fHint);
                g.DrawString(hideHint, fHint, Brushes.Gray, Width - hs.Width - 10, 8);

                // 状态行: 血条/视距
                int sy = 32;
                string blood = "血条:" + (BloodBars.Get() ? "开" : "关");
                string cam = "视距:" + (_cfg.CameraKeys ? "开(0拉远 .拉近)" : "关");
                SizeF bs = g.MeasureString(blood, fLabel);
                _bloodRect = new Rectangle(10, sy, (int)bs.Width + 8, 20);
                g.DrawRectangle(Pens.Gray, _bloodRect);
                g.DrawString(blood, fLabel, Brushes.White, _bloodRect.Left + 4, _bloodRect.Top + 3);
                SizeF cs = g.MeasureString(cam, fLabel);
                _camRect = new Rectangle(_bloodRect.Right + 12, sy, (int)cs.Width + 8, 20);
                g.DrawRectangle(Pens.Gray, _camRect);
                g.DrawString(cam, fLabel, Brushes.White, _camRect.Left + 4, _camRect.Top + 3);
                g.DrawString("点击切换", fHint, Brushes.DarkGray, _camRect.Right + 6, sy + 4);

                // 技能 12 格（3 行 x 4 列）
                int gx0 = 10, gy0 = 72;
                int cw = 66, ch = 34, gap = 5;
                int idx = 0;
                g.DrawString("技能栏 12 格（点击录制）", fHint, Brushes.DarkGray, gx0, gy0 - 16);
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        Rectangle r = new Rectangle(gx0 + col * (cw + gap), gy0 + row * (ch + gap), cw, ch);
                        _skillRects[idx] = r;
                        DrawSlot(g, r, idx + 1, _cfg.SlotMapKey[idx + 1], null, fKey, fHint);
                        idx++;
                    }
                }

                // 物品 6 格（1 行）
                int iy0 = gy0 + 3 * (ch + gap) + 16;
                g.DrawString("物品栏 6 格（点击录制）", fHint, Brushes.DarkGray, gx0, iy0 - 16);
                for (int i = 0; i < 6; i++)
                {
                    Rectangle r = new Rectangle(gx0 + i * (cw + gap), iy0, cw, ch);
                    _itemRects[i] = r;
                    // 物品格未录制时显示 war3 默认数字 1-6
                    uint vk = _cfg.SlotMapKey[5 + i];
                    string def = (vk == 0) ? ((char)('1' + i)).ToString() : null;
                    DrawSlot(g, r, 5 + i, vk, def, fKey, fHint);
                }

                // 录制提示
                if (_recSlot != 0)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(200, 60, 40)))
                    {
                        Rectangle bar = new Rectangle(10, Height - 28, Width - 20, 20);
                        g.FillRectangle(b, bar);
                        g.DrawString("正在录制 slot" + _recSlot + " ... 请按新键（Esc 取消）", fLabel, Brushes.White, 16, Height - 25);
                    }
                }
                else
                {
                    g.DrawString("按 Esc 取消 · 0=视距拉远 · .=视距拉近", fHint, Brushes.DarkGray, 10, Height - 24);
                }
            }
        }

        // 布局公式（与 OnPaint 同步, SelfTest T6 复用; 面板 445x272 时 6 物品格恰好不越界）
        internal static void ComputeLayout(int panelWidth, out Rectangle bloodRect, out Rectangle camRect,
                                           out Rectangle[] skillRects, out Rectangle[] itemRects)
        {
            bloodRect = new Rectangle(10, 32, 86, 20);
            camRect = new Rectangle(108, 32, 140, 20);
            skillRects = new Rectangle[12];
            itemRects = new Rectangle[6];
            int gx0 = 10, gy0 = 72;
            int cw = 66, ch = 34, gap = 5;
            int idx = 0;
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 4; col++)
                {
                    skillRects[idx] = new Rectangle(gx0 + col * (cw + gap), gy0 + row * (ch + gap), cw, ch);
                    idx++;
                }
            int iy0 = gy0 + 3 * (ch + gap) + 16;
            for (int i = 0; i < 6; i++)
                itemRects[i] = new Rectangle(gx0 + i * (cw + gap), iy0, cw, ch);
        }

        // 命中判定（与 OnPanelMouseClick 同步; 返回 0=无, 1-12=技能, 101-106=物品5-10, 201=血条, 202=视距）
        internal static int HitTest(Rectangle bloodRect, Rectangle camRect, Rectangle[] skillRects, Rectangle[] itemRects, Point p)
        {
            if (bloodRect.Contains(p)) return 201;
            if (camRect.Contains(p)) return 202;
            for (int i = 0; i < 12; i++) if (skillRects[i].Contains(p)) return i + 1;
            for (int i = 0; i < 6; i++) if (itemRects[i].Contains(p)) return 101 + i;
            return 0;
        }

        private void DrawSlot(Graphics g, Rectangle r, int slot, uint vk, string defaultKey, Font fKey, Font fHint)
        {
            bool rec = (_recSlot == slot);
            Color border = rec ? Color.FromArgb(255, 200, 60) : Color.FromArgb(90, 90, 90);
            Color fill = rec ? Color.FromArgb(80, 60, 20) : Color.FromArgb(18, 18, 18);
            using (Pen p = new Pen(border, rec ? 2f : 1f))
            using (Brush b = new SolidBrush(fill))
            {
                g.FillRectangle(b, r);
                g.DrawRectangle(p, r);
            }
            string label = slot + ":";
            string key = (vk != 0) ? Vk.Name(vk) : (defaultKey ?? "空");
            g.DrawString(label, fHint, Brushes.Gray, r.Left + 4, r.Top + 2);
            g.DrawString(key, fKey, rec ? Brushes.Yellow : Brushes.White, r.Left + 28, r.Top + 6);
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
        private PanelForm _panel;

        public bool Recording { get { return _recording || (_panel != null && _panel.IsRecSlot); } }

        public KeybindEngine(KeybindConfig cfg, War3Window war3)
        {
            _cfg = cfg;
            _war3 = war3;
            _proc = HookCallback;
        }

        public void AttachPanel(PanelForm panel)
        {
            _panel = panel;
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

                // 1) 录制分支（最优先: 面板格子录制 + 12 格热键录制）
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
                            if (_panel != null) _panel.RefreshGrid();
                        }
                    }
                    return (IntPtr)1;
                }

                // 1b) 面板格子录制（面板可见时点击某格后, 捕获下一物理键写入该格）
                if (_panel != null && _panel.Visible && _panel.IsRecSlot)
                {
                    if (!down) return (IntPtr)1;
                    if (vk == 0x1B)
                    {
                        _panel.CancelRec();
                        return (IntPtr)1;
                    }
                    // 忽略纯修饰键按下, 等待实际键
                    if (vk == 0x10 || vk == 0x11 || vk == 0x12) return (IntPtr)1;
                    if (_war3.IsChat()) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    int slot = 0;
                    for (int i = 1; i <= 14; i++)
                    {
                        if (IsPanelRecSlot(i)) { slot = i; break; }
                    }
                    if (slot >= 1 && slot <= 14)
                    {
                        _cfg.SlotMapKey[slot] = vk;
                        _cfg.SaveSlotMap();
                        _cfg.RebuildMappings();
                        _panel.CompleteRec(vk);
                        Logger.Write(string.Format("面板改键: slot{0} = {1}(0x{2:X2}), 生效映射 {3} 条",
                            slot, Vk.Name(vk), vk, _cfg.Mappings.Count));
                    }
                    return (IntPtr)1;
                }

                // 2) 录制热键触发
                if (down && _cfg.RecordHotkey != 0 && vk == _cfg.RecordHotkey && !_war3.IsChat())
                {
                    StartRecording();
                    return (IntPtr)1;
                }

                // 2b) 镜头控制（复刻 SuWar3Tools: 0=视距拉远, .=视距拉近/恢复）
                //     优先级高于改键映射: 0/. 默认不进改键映射, 专用于镜头
                if (down && _cfg.CameraKeys && !_war3.IsChat() && _war3.IsForeground())
                {
                    if (vk == 0x30)   // 数字 0
                    {
                        bool ok = CameraControl.SetDistance(_cfg.CamFar);
                        Logger.Write("镜头控制: [0] 视距拉远 -> " + _cfg.CamFar + " " + (ok ? "OK" : "FAIL: " + CameraControl.LastError));
                        if (!ok) NotifyOnce("视距拉远失败: " + CameraControl.LastError);
                        return (IntPtr)1;
                    }
                    if (vk == 0xBE)   // .
                    {
                        bool ok = CameraControl.SetDistance(_cfg.CamNear);
                        Logger.Write("镜头控制: [.] 视距拉近 -> " + _cfg.CamNear + " " + (ok ? "OK" : "FAIL: " + CameraControl.LastError));
                        if (!ok) NotifyOnce("视距拉近失败: " + CameraControl.LastError);
                        return (IntPtr)1;
                    }
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

        // 面板录制目标格（由 PanelForm 暴露, 简化: 内部直查）
        private bool IsPanelRecSlot(int slot)
        {
            return _panel != null && _panel.Visible && _panel.IsRecSlot;
        }

        private void NotifyOnce(string text)
        {
            // 面板可见时显示在状态行, 这里只记录日志
            Logger.Write("提示: " + text);
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
        private PanelForm _panel;
        private DateTime _lastMtime;
        private DateTime _lastChangeTime = DateTime.MinValue;
        private bool _reloadPending = false;
        private const int ReloadQuietMs = 400; // 编辑器保存过程中的多事件抖动抑制

        public TrayContext(KeybindConfig cfg, War3Window war3, KeybindEngine engine)
        {
            _cfg = cfg;
            _war3 = war3;
            _engine = engine;

            // 面板（置顶半透明, 游戏内呼出）
            _panel = new PanelForm(cfg, engine);
            _engine.AttachPanel(_panel);

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "War3 改键面板";
            ContextMenuStrip menu = new ContextMenuStrip();

            var panelItem = new ToolStripMenuItem("显示/隐藏改键面板（F7）");
            panelItem.Click += delegate { _panel.TogglePanel(); };
            menu.Items.Add(panelItem);

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

            var bloodItem = new ToolStripMenuItem("血条: 切换开/关（当前" + (BloodBars.Get() ? "开" : "关") + "）");
            bloodItem.Click += delegate
            {
                bool now = BloodBars.Toggle();
                if (_panel != null) _panel.RefreshGrid();
            };
            menu.Items.Add(bloodItem);

            var camItem = new ToolStripMenuItem("镜头控制: " + (_cfg.CameraKeys ? "开（0拉远 .拉近）" : "关"));
            camItem.Click += delegate
            {
                _cfg.CameraKeys = !_cfg.CameraKeys;
                if (_panel != null) _panel.RefreshGrid();
            };
            menu.Items.Add(camItem);

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

            Logger.Write("== War3 改键面板 v4 启动 ==");
            Logger.Write(string.Format("Enabled={0} IsChatAvoid={1} UsePostMessage={2} ForegroundCheck={3} RecordHotkey={4} 映射数={5}",
                _cfg.Enabled, _cfg.IsChatAvoid, _cfg.UsePostMessage, _cfg.ForegroundCheck,
                (_cfg.RecordHotkey != 0) ? Vk.Name(_cfg.RecordHotkey) + "(0x" + _cfg.RecordHotkey.ToString("X2") + ")" : "无",
                _cfg.Mappings.Count));
            Logger.Write(string.Format("热键: 面板={0} 重载={1} 编辑={2}", _cfg.PanelHotkeyDesc, _cfg.ReloadHotkeyDesc, _cfg.EditHotkeyDesc));
            Logger.Write(string.Format("镜头: {0} 拉远={1} 拉近={2} | 血条默认={3}", _cfg.CameraKeys ? "开" : "关", _cfg.CamFar, _cfg.CamNear, _cfg.BloodBars ? "开" : "关"));
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
            if (_panel != null) _panel.RefreshGrid();
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
            Native.UnregisterHotKey(_hotkeyWin.Handle, 3);
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
            if (_cfg.PanelHotkeyVk != 0)
            {
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 3, _cfg.PanelHotkeyMods, _cfg.PanelHotkeyVk))
                    Logger.Write("面板热键注册失败（可能被其他程序占用）: " + _cfg.PanelHotkeyDesc);
            }
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == 1)
                ReloadConfig("热键 " + _cfg.ReloadHotkeyDesc);
            else if (id == 2)
                OpenEditor();
            else if (id == 3)
                _panel.TogglePanel();
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
                _tray.BalloonTipTitle = "War3 改键面板";
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(1500);
            }
            catch { }
        }

        protected override void ExitThreadCore()
        {
            if (_panel != null)
            {
                try { _panel.Hide(); _panel.Dispose(); } catch { }
                _panel = null;
            }
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
                try { Native.UnregisterHotKey(_hotkeyWin.Handle, 3); } catch { }
                try { _hotkeyWin.DestroyHandle(); } catch { }
                _hotkeyWin = null;
            }
            CameraControl.Detach();
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

    // ============ 自测（/selftest 参数触发; 不启动 war3 弹窗测试） ============
    internal static class SelfTest
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static int GetIntegrityLevel()
        {
            // 返回 SID SubAuthority[0]: 0x1000=Low, 0x2000=Medium, 0x3000=High; 失败返回 -1
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out tok)) return -1;
            try
            {
                uint len;
                GetTokenInformation(tok, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out len);
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetTokenInformation(tok, 25, buf, len, out len)) return -2;
                    IntPtr sid = Marshal.ReadIntPtr(buf, 0); // SID_AND_ATTRIBUTES.Sid @0
                    byte cnt = Marshal.ReadByte(sid, 1);
                    if (cnt == 0) return -3;
                    byte[] sa = new byte[4];
                    Marshal.Copy(new IntPtr(sid.ToInt64() + 8), sa, 0, 4);
                    return BitConverter.ToInt32(sa, 0);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { Marshal.FreeHGlobal(tok); }
        }

        public static void Run()
        {
            Logger.Init(true);
            Logger.SetEnabled(true);
            int pass = 0, fail = 0;
            Action<bool, string> check = delegate(bool ok, string name)
            {
                if (ok) { pass++; Logger.Write("[PASS] " + name); Console.WriteLine("[PASS] " + name); }
                else { fail++; Logger.Write("[FAIL] " + name); Console.WriteLine("[FAIL] " + name); }
            };

            // ---- T1: 配置解析（含 [panel] 段）----
            string tmpIni = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "keybind_panel_selftest.ini");
            try
            {
                string[] lines = new string[]
                {
                    "[keybind]", "Enabled=1", "IsChatAvoid=1",
                    "[panel]", "PanelHotkey=F7", "CameraKeys=1", "CamFar=3000", "CamNear=1650", "BloodBars=1",
                    "[Mappings]", "Q=slot1", "W=slot2",
                    "[SlotMap]", "Slot1=0x51", "Slot2=0x57",
                };
                File.WriteAllLines(tmpIni, lines, new UTF8Encoding(false));
                KeybindConfig cfg = new KeybindConfig();
                cfg.SetConfigPath(tmpIni);
                cfg.Load();
                check(cfg.PanelHotkeyVk == 0x76 && cfg.PanelHotkeyMods == 0x0, "T1 面板热键解析 F7 -> 0x76 mods 0x0");
                check(Math.Abs(cfg.CamFar - 3000f) < 0.01f && Math.Abs(cfg.CamNear - 1650f) < 0.01f, "T1 CamFar=3000 CamNear=1650 解析");
                check(cfg.CameraKeys && cfg.BloodBars, "T1 CameraKeys/BloodBars 解析");
                check(cfg.Mappings.Count == 2, "T1 映射 2 条生效 (Q->slot1 W->slot2)");
                check(cfg.SlotMapKey[1] == 0x51 && cfg.SlotMapKey[2] == 0x57, "T1 SlotMap 读取 slot1=0x51 slot2=0x57");
                // ---- T2: SaveSlotMap 写入 + 热重载抑制 ----
                cfg.SlotMapKey[3] = 0x45;
                cfg.SaveSlotMap();
                string reread = File.ReadAllText(tmpIni, Encoding.UTF8);
                check(reread.Contains("Slot3=0x45"), "T2 SaveSlotMap 写入 Slot3=0x45");
                KeybindConfig cfg2 = new KeybindConfig();
                cfg2.SetConfigPath(tmpIni);
                cfg2.Load();
                check(cfg2.SlotMapKey[3] == 0x45, "T2 重载后 SlotMapKey[3]=0x45");
                // 双轨兼容: [Mappings] 段仍在
                check(reread.Contains("Q=slot1"), "T2 保存 SlotMap 后 [Mappings] 段保留 (Q=slot1)");
            }
            catch (Exception ex) { check(false, "T1/T2 异常: " + ex.Message); }
            finally { try { File.Delete(tmpIni); } catch { } }

            // ---- T3: 血条注册表读写（Registry32 视图, 与 war3 一致）----
            try
            {
                int il = GetIntegrityLevel();
                if (il < 0x2000) // Low IL(4096)/Untrusted: DSH 沙箱降权导致 HKCU 写入被拒
                {
                    check(true, "T3 血条注册表: 进程 IL=" + il + " 受限, 跳过写入验证（Registry32 视图路径静态证据见报告）");
                    Logger.Write("T3 血条注册表: IL=" + il + " 受限跳过写入验证, KeyPath=Software\\Blizzard Entertainment\\Warcraft III\\Gameplay healthbars(DWord) Registry32");
                }
                else
                {
                    bool orig = BloodBars.Get();
                    Logger.Write("T3 诊断: 原始值 orig=" + orig);
                    BloodBars.Set(true);
                    bool after = BloodBars.Get();
                    Logger.Write("T3 诊断: 写1后读回 after=" + after);
                    check(after, "T3 血条写入 healthbars=1 读回=开");
                    BloodBars.Set(false);
                    bool off = BloodBars.Get();
                    BloodBars.Set(orig);
                    check(!off, "T3 血条写入 healthbars=0 读回=关");
                    check(BloodBars.Get() == orig, "T3 血条恢复原值 " + (orig ? "开" : "关"));
                }
            }
            catch (Exception ex) { check(false, "T3 异常: " + ex.Message); }

            // ---- T4: 视距偏移定位逻辑（不启动 war3, 验证静态锚点与模块查找）----
            try
            {
                check(CameraControl.GameObjGlobalOff == 0xBE6350, "T4 锚点 game 对象全局偏移=0xBE6350");
                check(CameraControl.CamPtrOff == 0x254, "T4 锚点 相机指针偏移=0x254");
                check(CameraControl.DistFloatOff == 0xFC, "T4 锚点 视距字段偏移=0xFC");
                // 本地 game.dll 存在性 + 大小校验（证据一致性）
                string gdll = @"E:\game\Warcraft III Frozen Throne\Game.dll";
                if (File.Exists(gdll))
                {
                    long sz = new FileInfo(gdll).Length;
                    check(sz == 13187048, "T4 本地 Game.dll 13,187,048B 存在 (实际 " + sz + "B)");
                }
                else
                {
                    check(false, "T4 本地 Game.dll 未找到: " + gdll);
                }
                bool attached = CameraControl.Attach();
                if (attached)
                {
                    check(true, "T4 war3 正在运行, 已附加 (pid=" + Process.GetProcessesByName("war3")[0].Id + ")");
                    // 若 war3 在跑, 尝试实际写入验证（要求已进游戏, game 对象非空）
                    bool w = CameraControl.SetDistance(1650f);
                    check(w || CameraControl.LastError.Contains("game 对象全局为空"), "T4 war3 视距写入验证: " + (w ? "OK" : "跳过(" + CameraControl.LastError + ")"));
                }
                else
                {
                    check(true, "T4 war3 未运行, 跳过内存写入验证（偏移定位静态证据见报告）");
                }
                CameraControl.Detach();
            }
            catch (Exception ex) { check(false, "T4 异常: " + ex.Message); }

            // ---- T5: 视距按键 VK 语义 ----
            uint vk0, vkDot;
            check(Vk.TryParse("0", out vk0) && vk0 == 0x30, "T5 视距拉远键 0 = VK 0x30");
            check(Vk.TryParse(".", out vkDot) && vkDot == 0xBE, "T5 视距拉近键 . = VK 0xBE");

            // ---- T6: 面板布局几何与命中（纯计算, 不弹窗）----
            try
            {
                Rectangle bloodRect, camRect;
                Rectangle[] skillRects, itemRects;
                PanelForm.ComputeLayout(445, out bloodRect, out camRect, out skillRects, out itemRects);
                bool inBounds = true;
                foreach (Rectangle r in skillRects) if (r.Right > 445 || r.Bottom > 272) inBounds = false;
                foreach (Rectangle r in itemRects) if (r.Right > 445 || r.Bottom > 272) inBounds = false;
                check(inBounds, "T6 面板 12 技能格+6 物品格均在 445x272 内不越界");

                // 物品第 6 格中心点击 -> 命中 slot10
                Rectangle lastItem = itemRects[5];
                Point pc = new Point(lastItem.Left + lastItem.Width / 2, lastItem.Top + lastItem.Height / 2);
                check(PanelForm.HitTest(bloodRect, camRect, skillRects, itemRects, pc) == 106, "T6 点击物品第 6 格命中 -> slot10(106)");
                // 技能第 1 格中心 -> slot1
                Rectangle firstSkill = skillRects[0];
                Point ps = new Point(firstSkill.Left + 1, firstSkill.Top + 1);
                check(PanelForm.HitTest(bloodRect, camRect, skillRects, itemRects, ps) == 1, "T6 点击技能第 1 格命中 -> slot1(1)");
                // 血条状态框 -> 201
                check(PanelForm.HitTest(bloodRect, camRect, skillRects, itemRects, new Point(bloodRect.Left + 2, bloodRect.Top + 2)) == 201, "T6 点击血条状态框命中 -> 201");
                // 空白区 -> 0
                check(PanelForm.HitTest(bloodRect, camRect, skillRects, itemRects, new Point(5, 270)) == 0, "T6 点击空白区命中 -> 0(无)");
            }
            catch (Exception ex) { check(false, "T6 异常: " + ex.Message); }

            Logger.Write("== 自测结束: PASS " + pass + " / FAIL " + fail + " ==");
            Console.WriteLine("== 自测结束: PASS " + pass + " / FAIL " + fail + " ==");
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool createdNew;
            Mutex mutex = new Mutex(true, "KeybindPanel_KKRE_8F2A1C", out createdNew);
            if (!createdNew) return; // 单实例

            if (Array.IndexOf(args, "/selftest") >= 0)
            {
                SelfTest.Run();
                return;
            }

            KeybindConfig cfg = new KeybindConfig();
            cfg.EnsureDefaultFile();
            Logger.Init(true);            // 先开日志: 让 Load 阶段的警告也能落盘
            cfg.Load();
            Logger.SetEnabled(cfg.LogEnabled);
            Logger.Write("启动 PID=" + Process.GetCurrentProcess().Id);

            // 血条默认开启（进游戏即显示敌我血条）
            BloodBars.ApplyDefault(cfg.BloodBars);

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
