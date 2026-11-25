// keybind_panel_v2.cs — War3 改键工具 面板版 v2（槽位驱动模型, 单文件 C#, .NET 4.8.1）
// 基于 keybind_panel.cs v4 重构, 对齐娱乐大师 42 格 [KEYBINDS] 语义:
//   A. 槽位驱动: 42 格 = 技能 12(A_X0Y0..A_X3Y2, 4列×3行) + 物品 6(I_X0Y0..I_X1Y2, 2列×3行)
//      + 书页 12(SA_) + 商店 12(NS_); 每格值=VK 或空(空=未绑定)
//   B. 映射语义: 面板格子↔用户快捷键绑定表; 运行时按该快捷键 → 向 war3 发
//      "该槽位当前触发键"(默认键表 Q/W/E/R+T/G/B 技能 + 1-6 物品; 图自定义 hotkey
//      由"录制各格当前触发键"覆盖)
//   C. 普适性: 槽位引擎级稳定(换英雄/换图格内容变, 但格1永远是格1); 映射与英雄无关
//   D. 多配置 profiles: keybind.ini 主文件 + keybind_<名>.ini 独立文件, 托盘菜单切换,
//      切换后即时热重载(无 OpenFileDialog 弹窗)
//   E. 保留: F7 面板热键/血条默认开/视距 0=拉远 .=拉近/WH_KEYBOARD_LL/IsChat 避让/
//      热重载(FileSystemWatcher+轮询兜底)/旧 [Mappings] 兼容(源码级保留, 配置以 KEYBINDS 为主)
//   F. 视距修复(v2): Attach 改用 FindWindow("Warcraft III")→GetWindowThreadProcessId→
//      OpenProcess(绕过受限 token 进程枚举); 相机链 [game.dll+0xBE6350]→[+0x254],
//      距离字段默认 CamOffs=0x5B0(现场实测菜单下 1700.0; 写后游戏帧级还原属派生字段,
//      详见 keybind_panel_v2_report.md §3; 旧 +0xFC 为镜像指针已废弃)

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
    // ============ 原生互操作 ============
    internal static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x100;
        public const int WM_KEYUP = 0x101;
        public const int WM_HOTKEY = 0x0312;
        public const uint LLKHF_INJECTED = 0x10;
        public const uint LLKHF_UP = 0x80;
        public const uint KEYEVENTF_KEYUP = 0x2;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;

        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;

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
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
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

        // psapi.dll 枚举模块(受限 token 下可用, 已验证; x86 进程不能用 kernel32 EnumProcessModules)
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool EnumProcessModules(IntPtr hProcess, IntPtr[] lphModule, uint cb, out uint lpcbNeeded);
        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleBaseName(IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [StructLayout(LayoutKind.Sequential)]
        public struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

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

    // ============ 键名解析 ============
    internal static class Vk
    {
        // 娱乐大师 42 格 KEYBINDS 槽位名 -> 索引: 0-11=A_, 12-17=I_, 18-29=SA_, 30-41=NS_
        public static readonly string[] SlotNames = new string[42];
        public const int SlotsSkill = 12;      // A_ 技能
        public const int SlotsItem = 6;        // I_ 物品
        public const int SlotsTotal = 42;

        static Vk()
        {
            int idx = 0;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    SlotNames[idx++] = "A_X" + x + "Y" + y;      // 4列×3行
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 2; x++)
                    SlotNames[idx++] = "I_X" + x + "Y" + y;      // 2列×3行
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    SlotNames[idx++] = "SA_X" + x + "Y" + y;     // 书页 4列×3行
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    SlotNames[idx++] = "NS_X" + x + "Y" + y;     // 商店 4列×3行
        }

        // 面板可见格: 0-11=A_ 技能(4×3), 12-17=I_ 物品(2×3)
        public const int PanelSlots = 18;

        // 槽位默认触发键(war3 默认技能格 Q/W/E/R + T/G/B, 物品格 1-6; 0=无默认, 需录制)
        private static readonly uint[] SkillDef = { 0x51, 0x57, 0x45, 0x52 };  // Q W E R
        private static readonly uint[] SkillDef2 = { 0x54, 0x47, 0x42 };       // T G B
        public static uint DefaultTrigger(int slot)
        {
            if (slot >= 0 && slot < 4) return SkillDef[slot];
            if (slot >= 4 && slot < 7) return SkillDef2[slot - 4];
            if (slot >= 12 && slot < 18) return (uint)('1' + (slot - 12)); // I_ -> 1-6
            return 0;
        }

        public static bool TryParseSlotKeybind(string name, out int idx)
        {
            idx = -1;
            if (name == null) return false;
            string t = name.Trim().ToUpperInvariant();
            for (int i = 0; i < SlotNames.Length; i++)
            {
                if (SlotNames[i].Equals(t, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    return true;
                }
            }
            return false;
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

    // ============ 日志 ============
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
            lock (_sync) { _enabled = enabled; }
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

        public static void Write(string fmt, object a0) { Write(string.Format(fmt, a0)); }
        public static void Write(string fmt, object a0, object a1) { Write(string.Format(fmt, a0, a1)); }
    }

    // ============ 配置 ============
    internal class KeybindConfig
    {
        public readonly object SyncRoot = new object();

        public bool Enabled = true;
        public bool IsChatAvoid = true;
        public bool UsePostMessage = false;
        public bool ForegroundCheck = true;
        public bool LogEnabled = true;
        public int PollIntervalMs = 5000;
        public uint RecordHotkey = 0x78;

        public uint ReloadHotkeyVk = 0x7B;
        public uint ReloadHotkeyMods = 0x6;
        public string ReloadHotkeyDesc = "Ctrl+Shift+F12";
        public uint EditHotkeyVk = 0x7A;
        public uint EditHotkeyMods = 0x6;
        public string EditHotkeyDesc = "Ctrl+Shift+F11";

        public uint PanelHotkeyVk = 0x76;
        public uint PanelHotkeyMods = 0x0;
        public string PanelHotkeyDesc = "F7";
        public bool CameraKeys = true;
        public float CamFar = 2800.0f;
        public float CamNear = 1650.0f;
        public bool BloodBars = true;
        public int CamOffs = 0x5B0;   // v2: 相机距离字段候选偏移(旧 0xFC 为镜像指针已废弃)

        public DateTime SuppressReloadUntil = DateTime.MinValue;

        // v3 自动读开关: 1=运行时自动读游戏内技能栏 hotkey(优先于录制); 0=仅录制/默认
        public bool AutoReadHotkeys = true;
        // v3 自动读最近状态(供面板/托盘显示)
        public string AutoReadStatus = string.Empty;

        // 槽位驱动核心: BindKey[42] = 面板格绑定的用户快捷键(KEYBINDS 值)
        //             TriggerKey[42] = 该槽位当前触发键(0=用默认键表 DefaultTrigger)
        //             AutoTrigger[42] = v3 自动读到的槽位当前 hotkey(0=未读到, 优先级最高)
        public readonly uint[] BindKey = new uint[Vk.SlotsTotal];
        public readonly uint[] TriggerKey = new uint[Vk.SlotsTotal];
        public readonly uint[] AutoTrigger = new uint[Vk.SlotsTotal];

        // 运行时查表: 源键 VK -> 槽位索引(由 BindKey 反查)
        private readonly Dictionary<uint, int> _bindLookup = new Dictionary<uint, int>();

        // 旧 [Mappings] 兼容(Src=DirectVk 直发)
        private readonly Dictionary<uint, uint> _legacyMappings = new Dictionary<uint, uint>();

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

        public void SetConfigPath(string path) { _configPath = Path.GetFullPath(path); }

        // 运行时按 vk 查找应触发的目标 VK(槽位当前触发键)
        public bool TryResolve(uint srcVk, out uint dstVk)
        {
            lock (SyncRoot)
            {
                int slot;
                if (_bindLookup.TryGetValue(srcVk, out slot))
                {
                    dstVk = EffectiveTrigger(slot);
                    return dstVk != 0;
                }
                if (_legacyMappings.TryGetValue(srcVk, out dstVk)) return true;
                dstVk = 0;
                return false;
            }
        }

        // 槽位当前触发键: v3 自动读(AutoTrigger) > 录制覆盖(TriggerKey) > 默认键表
        public uint EffectiveTrigger(int slot)
        {
            if (slot < 0 || slot >= Vk.SlotsTotal) return 0;
            uint a = AutoTrigger[slot];
            if (a != 0) return a;
            uint t = TriggerKey[slot];
            if (t != 0) return t;
            return Vk.DefaultTrigger(slot);
        }

        // v3: 写入某槽位自动读到的触发键(0=清除)
        public void SetAutoTrigger(int slot, uint vk)
        {
            if (slot < 0 || slot >= Vk.SlotsTotal) return;
            lock (SyncRoot) { AutoTrigger[slot] = vk; }
        }

        public string EffectiveTriggerName(int slot)
        {
            uint t = EffectiveTrigger(slot);
            return t != 0 ? Vk.Name(t) : "无";
        }

        public static readonly string[] DefaultLines = new string[]
        {
            "; War3 改键工具 keybind.ini（面板版 v2, 槽位驱动模型）",
            "; 主路径 = 游戏外直配: 在 [KEYBINDS] 填 槽位名=用户快捷键(VK 或键名), 保存即生效(无需进游戏)。",
            "; 语义: 运行时按该快捷键 -> 触发对应引擎槽位当前内容(槽位引擎级稳定, 换英雄/换图不变)。",
            "; 触发键 = 槽位当前实际热键: 默认 Q/W/E/R+T/G/B(技能) + 1-6(物品);",
            ";   图自定义 hotkey 时用托盘[录制各格当前触发键]覆盖(进图后使用)。",
            "; 多配置: 本文件为主配置; 复制为 keybind_<名>.ini 即得新 profile, 托盘菜单切换。",
            "",
            "[keybind]",
            "Enabled=1",
            "IsChatAvoid=1",
            "UsePostMessage=0",
            "ForegroundCheck=1",
            "LogEnabled=1",
            "PollIntervalMs=5000",
            "RecordHotkey=0x78",
            "; 全局热键(0 或 none 禁用):",
            "ReloadHotkey=Ctrl+Shift+F12",
            "EditHotkey=Ctrl+Shift+F11",
            "",
            "[panel]",
            "; 面板热键(默认 F7; 0 或 none 禁用):",
            "PanelHotkey=F7",
            "; 镜头控制: 游戏内按 [0] 视距拉远, 按 [.] 视距拉近/恢复:",
            "CameraKeys=1",
            "CamFar=2800",
            "CamNear=1650",
            "; v2 相机距离字段偏移(0x5B0=实测当前距离, 派生字段; 0xFC 已废弃):",
            "CamOffs=0x5B0",
            "; v3 自动读技能栏 hotkey: 1=运行时直读游戏内各槽位当前热键(优先于录制, 兜底录制)",
            ";   读取链 [Game.dll+0xBE6350]->CGameUI+0x3C8->CCommandBar+0x154 槽表->按钮+0x190->Data+0x270",
            "AutoReadHotkeys=1",
            "; 血条默认开启:",
            "BloodBars=1",
            "",
            "[KEYBINDS]",
            "; 42 格键位表: 槽位名=用户快捷键(VK 或空=未绑定)",
            "; 技能 12 格(4列×3行 A_X0Y0..A_X3Y2) + 物品 6 格(2列×3行 I_X0Y0..I_X1Y2)",
            "; 书页 SA_/商店 NS_ 12 格各(4列×3行, 面板未显示, 供兼容)",
            "A_X0Y0=",
            "A_X1Y0=",
            "A_X2Y0=",
            "A_X3Y0=",
            "A_X0Y1=",
            "A_X1Y1=",
            "A_X2Y1=",
            "A_X3Y1=",
            "A_X0Y2=",
            "A_X1Y2=",
            "A_X2Y2=",
            "A_X3Y2=",
            "I_X0Y0=",
            "I_X1Y0=",
            "I_X0Y1=",
            "I_X1Y1=",
            "I_X0Y2=",
            "I_X1Y2=",
            "SA_X0Y0=",
            "SA_X1Y0=",
            "SA_X2Y0=",
            "SA_X3Y0=",
            "SA_X0Y1=",
            "SA_X1Y1=",
            "SA_X2Y1=",
            "SA_X3Y1=",
            "SA_X0Y2=",
            "SA_X1Y2=",
            "SA_X2Y2=",
            "SA_X3Y2=",
            "NS_X0Y0=",
            "NS_X1Y0=",
            "NS_X2Y0=",
            "NS_X3Y0=",
            "NS_X0Y1=",
            "NS_X1Y1=",
            "NS_X2Y1=",
            "NS_X3Y1=",
            "NS_X0Y2=",
            "NS_X1Y2=",
            "NS_X2Y2=",
            "NS_X3Y2=",
            "",
            "[SlotTriggers]",
            "; 各槽位当前触发键覆盖(0/空=用默认键表 QWER+TGB / 1-6):",
            "; 录制: 托盘菜单[录制各格当前触发键]进图后依次按实际热键, 自动写入本段",
            "",
            "[Mappings]",
            "; 旧版兼容(源键=目标键直发, 保留; 新配置建议用 [KEYBINDS])",
            ";F1=Q",
            ";F2=W",
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
            bool newEnabled = Enabled, newChat = IsChatAvoid, newPost = UsePostMessage, newFg = ForegroundCheck, newLog = LogEnabled;
            int newPoll = PollIntervalMs;
            uint newRec = RecordHotkey;
            uint newRvk = ReloadHotkeyVk, newRmods = ReloadHotkeyMods; string newRdesc = ReloadHotkeyDesc;
            uint newEvk = EditHotkeyVk, newEmods = EditHotkeyMods; string newEdesc = EditHotkeyDesc;
            uint newPvk = PanelHotkeyVk, newPmods = PanelHotkeyMods; string newPdesc = PanelHotkeyDesc;
            bool newCam = CameraKeys;
            float newFar = CamFar, newNear = CamNear;
            bool newBlood = BloodBars;
            int newCamOffs = CamOffs;
            bool newAutoRead = AutoReadHotkeys;
            uint[] newBind = new uint[Vk.SlotsTotal];
            uint[] newTrigger = new uint[Vk.SlotsTotal];
            Dictionary<uint, uint> newLegacy = new Dictionary<uint, uint>();
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
                    string k = trimmed.Substring(0, eq).Trim();
                    string v = trimmed.Substring(eq + 1).Trim();
                    if (section == "keybind")
                    {
                        string kl = k.ToLowerInvariant();
                        if (kl == "enabled") newEnabled = ParseBool(v, Enabled);
                        else if (kl == "ischatavoid") newChat = ParseBool(v, IsChatAvoid);
                        else if (kl == "usepostmessage") newPost = ParseBool(v, UsePostMessage);
                        else if (kl == "foregroundcheck") newFg = ParseBool(v, ForegroundCheck);
                        else if (kl == "logenabled") newLog = ParseBool(v, LogEnabled);
                        else if (kl == "pollintervalms") newPoll = ParseInt(v, PollIntervalMs);
                        else if (kl == "recordhotkey") { uint rv; if (Vk.TryParse(v, out rv)) newRec = rv; }
                        else if (kl == "reloadhotkey") newRdesc = ParseHotkeySetting(v, out newRvk, out newRmods, ReloadHotkeyVk, ReloadHotkeyMods, ReloadHotkeyDesc);
                        else if (kl == "edithotkey") newEdesc = ParseHotkeySetting(v, out newEvk, out newEmods, EditHotkeyVk, EditHotkeyMods, EditHotkeyDesc);
                    }
                    else if (section == "panel")
                    {
                        string kl = k.ToLowerInvariant();
                        if (kl == "panelhotkey") newPdesc = ParseHotkeySetting(v, out newPvk, out newPmods, PanelHotkeyVk, PanelHotkeyMods, PanelHotkeyDesc);
                        else if (kl == "camerakeys") newCam = ParseBool(v, CameraKeys);
                        else if (kl == "camfar") { float f; if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0) newFar = f; }
                        else if (kl == "camnear") { float f; if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0) newNear = f; }
                        else if (kl == "bloodbars") newBlood = ParseBool(v, BloodBars);
                        else if (kl == "autoreadhotkeys") newAutoRead = ParseBool(v, AutoReadHotkeys);
                        else if (kl == "camoffs")
                        {
                            int o;
                            if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out o)) newCamOffs = o;
                            else if (int.TryParse(v, out o)) newCamOffs = o;
                        }
                    }
                    else if (section == "keybinds")
                    {
                        int idx;
                        if (Vk.TryParseSlotKeybind(k, out idx))
                        {
                            uint vk;
                            if (v.Length > 0 && Vk.TryParse(v, out vk)) newBind[idx] = vk;
                        }
                    }
                    else if (section == "slottriggers")
                    {
                        int idx;
                        if (Vk.TryParseSlotKeybind(k, out idx))
                        {
                            uint vk;
                            if (v.Length > 0 && Vk.TryParse(v, out vk)) newTrigger[idx] = vk;
                        }
                    }
                    else if (section == "mappings")
                    {
                        uint svk;
                        if (Vk.TryParse(k, out svk))
                        {
                            uint dvk;
                            if (Vk.TryParse(v, out dvk) && dvk != 0) newLegacy[svk] = dvk;
                        }
                    }
                }
                lock (SyncRoot)
                {
                    Enabled = newEnabled; IsChatAvoid = newChat; UsePostMessage = newPost;
                    ForegroundCheck = newFg; LogEnabled = newLog; PollIntervalMs = newPoll;
                    RecordHotkey = newRec;
                    ReloadHotkeyVk = newRvk; ReloadHotkeyMods = newRmods; ReloadHotkeyDesc = newRdesc;
                    EditHotkeyVk = newEvk; EditHotkeyMods = newEmods; EditHotkeyDesc = newEdesc;
                    PanelHotkeyVk = newPvk; PanelHotkeyMods = newPmods; PanelHotkeyDesc = newPdesc;
                    CameraKeys = newCam; CamFar = newFar; CamNear = newNear; BloodBars = newBlood;
                    CamOffs = newCamOffs; AutoReadHotkeys = newAutoRead;
                    Array.Copy(newBind, BindKey, Vk.SlotsTotal);
                    Array.Copy(newTrigger, TriggerKey, Vk.SlotsTotal);
                    _legacyMappings.Clear();
                    foreach (var kv in newLegacy) _legacyMappings[kv.Key] = kv.Value;
                    RebuildLookup();
                }
            }
            catch (Exception ex)
            {
                Logger.Write("配置加载异常（保留旧配置）: " + ex.Message);
            }
        }

        private void RebuildLookup()
        {
            _bindLookup.Clear();
            for (int i = 0; i < Vk.SlotsTotal; i++)
            {
                if (BindKey[i] != 0 && !_bindLookup.ContainsKey(BindKey[i]))
                    _bindLookup[BindKey[i]] = i;
            }
        }

        // 保存全部槽位配置(KEYBINDS + SlotTriggers), 保留其他段, 抑制热重载
        public void SaveKeybinds()
        {
            try
            {
                List<string> lines = new List<string>();
                if (File.Exists(ConfigPath))
                {
                    string[] src = File.ReadAllLines(ConfigPath, Encoding.UTF8);
                    bool inKb = false, inTrig = false;
                    foreach (string raw in src)
                    {
                        string trimmed = raw.Trim();
                        if (trimmed.Length > 0 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                        {
                            string sec = trimmed.Substring(1, trimmed.Length - 2).Trim().ToLowerInvariant();
                            if (sec == "keybinds" || sec == "slottriggers") { inKb = sec == "keybinds"; inTrig = sec == "slottriggers"; continue; }
                            inKb = false; inTrig = false;
                        }
                        if (inKb || inTrig) continue;
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
                lines.Add("[KEYBINDS]");
                for (int i = 0; i < Vk.SlotsTotal; i++)
                    lines.Add(Vk.SlotNames[i] + "=" + (BindKey[i] != 0 ? ("0x" + BindKey[i].ToString("X2")) : ""));
                lines.Add("");
                lines.Add("[SlotTriggers]");
                for (int i = 0; i < Vk.SlotsTotal; i++)
                    lines.Add(Vk.SlotNames[i] + "=" + (TriggerKey[i] != 0 ? ("0x" + TriggerKey[i].ToString("X2")) : ""));
                SuppressReloadUntil = DateTime.Now.AddSeconds(1.5);
                File.WriteAllLines(ConfigPath, lines.ToArray(), new UTF8Encoding(false));
                Logger.Write("KEYBINDS 已写入: " + ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.Write("KEYBINDS 写入失败: " + ex.Message);
            }
        }

        public int BoundCount
        {
            get { lock (SyncRoot) { int c = 0; for (int i = 0; i < Vk.SlotsTotal; i++) if (BindKey[i] != 0) c++; return c; } }
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

    // ============ 血条控制 ============
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
                    object v = sub.GetValue("healthbars");
                    return v is int && (int)v != 0;
                }
            }
            catch { return false; }
        }

        public static bool Set(bool on)
        {
            try
            {
                using (RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32))
                using (RegistryKey sub = key.CreateSubKey(KeyPath))
                {
                    sub.SetValue("healthbars", on ? 1 : 0, RegistryValueKind.DWord);
                }
                return true;
            }
            catch { return false; }
        }

        public static bool Toggle()
        {
            bool cur = Get();
            Set(!cur);
            return !cur;
        }

        public static void ApplyDefault(bool on)
        {
            if (on) Set(true);
        }
    }

    // ============ 视距镜头控制(v2: Attach 修复 + CamOffs 候选偏移) ============
    // 写入链: [game.dll+0xBE6350](game 对象全局) -> +0x254(相机对象)
    //   -> +CamOffs(float 距离字段, v2 默认 0x5B0)
    // 证据: SetCameraField 0x1F4170 -> 0x34F398(getter 取 [0xBE6350]) -> 0x34DA20(mov eax,[ecx+0x254])
    //   -> 0x345760 switch field2 -> 0x345892(lea ecx,[esi+0x174] 子对象 setter 链 0x45720→0x458A0→0x3DFB2→0x3DFE0→0x3E28AA)
    // 现场实测: camPtr+0x5B0=1700.0(菜单下当前距离), 写 3000 后 <100ms 被游戏帧级还原(派生字段);
    //   旧 +0xFC 读值为镜像指针 0x65BBFD60(非 float, 已废弃, 禁止写入避免破坏镜像)。
    internal static class CameraControl
    {
        public const int GameObjGlobalOff = 0xBE6350;
        public const int CamPtrOff = 0x254;

        private static IntPtr _hProcess = IntPtr.Zero;
        private static IntPtr _gameDllBase = IntPtr.Zero;
        private static uint _war3Pid = 0;

        public static bool Attached { get { return _hProcess != IntPtr.Zero; } }
        public static string LastError = string.Empty;
        public static uint War3Pid { get { return _war3Pid; } }

        // 供自测读取当前基址(仅读)
        public static IntPtr GetBaseForTest()
        {
            return _gameDllBase;
        }

        // 找 war3 窗口(受限 token 下 FindWindow 可用) -> PID -> OpenProcess -> psapi 枚举 Game.dll 基址
        public static bool Attach()
        {
            Detach();
            try
            {
                IntPtr hwnd = Native.FindWindow(null, "Warcraft III");
                if (hwnd == IntPtr.Zero)
                {
                    // 兜底: 枚举所有顶层窗口按标题匹配(窗口标题可能带版本后缀)
                    IntPtr found = IntPtr.Zero;
                    Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        string t = Native.GetWindowTextSafe(h);
                        if (t.IndexOf("Warcraft", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = h;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    hwnd = found;
                }
                if (hwnd == IntPtr.Zero)
                {
                    LastError = "war3 窗口未找到(标题不含 Warcraft)";
                    return false;
                }
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0)
                {
                    LastError = "GetWindowThreadProcessId 失败";
                    return false;
                }
                _hProcess = Native.OpenProcess(
                    Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION | Native.PROCESS_QUERY_INFORMATION,
                    false, pid);
                if (_hProcess == IntPtr.Zero || _hProcess == (IntPtr)(-1))
                {
                    _hProcess = IntPtr.Zero;
                    LastError = "OpenProcess 失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                _war3Pid = pid;

                // psapi 枚举模块找 Game.dll(x86 进程受限 token 可用)
                uint need = 0;
                Native.EnumProcessModules(_hProcess, null, 0, out need);
                int count = (int)(need / IntPtr.Size);
                IntPtr[] mods = new IntPtr[count];
                Native.EnumProcessModules(_hProcess, mods, need, out need);
                bool foundDll = false;
                for (int i = 0; i < mods.Length; i++)
                {
                    StringBuilder sb = new StringBuilder(260);
                    Native.GetModuleBaseName(_hProcess, mods[i], sb, 260);
                    if (sb.ToString().Equals("Game.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Native.MODULEINFO mi;
                        if (Native.GetModuleInformation(_hProcess, mods[i], out mi, (uint)Marshal.SizeOf(typeof(Native.MODULEINFO))))
                        {
                            _gameDllBase = mi.lpBaseOfDll;
                            foundDll = true;
                            break;
                        }
                    }
                }
                if (!foundDll)
                {
                    LastError = "Game.dll 模块未找到";
                    Detach();
                    return false;
                }
                Logger.Write(string.Format("已附加 war3 pid={0} game.dll 基址=0x{1:X8} (窗口句柄 0x{2:X})", pid, _gameDllBase.ToInt64(), hwnd.ToInt64()));
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
            _gameDllBase = IntPtr.Zero;
            _war3Pid = 0;
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

        public static bool SetDistance(float dist, int camOffs)
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
                    LastError = "读 game 对象全局失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
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
                    LastError = "读相机对象失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                if (camPtr == IntPtr.Zero)
                {
                    LastError = "相机对象为空（未进游戏画面?）";
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                IntPtr distAddr = new IntPtr(camPtr.ToInt64() + camOffs);
                byte[] data = BitConverter.GetBytes(dist);
                int written;
                if (!Native.WriteProcessMemory(_hProcess, distAddr, data, 4, out written) || written != 4)
                {
                    LastError = "写视距内存失败: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    Logger.Write("视距写入失败: " + LastError);
                    return false;
                }
                byte[] back = new byte[4];
                int read;
                if (Native.ReadProcessMemory(_hProcess, distAddr, back, 4, out read) && read == 4)
                {
                    float v = BitConverter.ToSingle(back, 0);
                    Logger.Write(string.Format("视距已写入 0x{0:X8}(+0x{1:X3}) = {2:F1} (读回 {3:F1})", distAddr.ToInt64(), camOffs, dist, v));
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

    // ============ v3 技能栏 hotkey 自动读取(RPM 直读, 方案 B) ============
    // 对标娱乐大师 WCG.dll 的运行时技能栏 hotkey 感知, 但走内存直读不注入:
    //   [Game.dll+0xBE6350] -> CGameUI -> [+0x3C8] -> CCommandBar(技能栏, vtable 0x990030)
    //   -> [+0x154] 槽表(行步长16, 行指针@+row*16+8, [行+col*4] = CCommandButton, vtable 0x98F6A8)
    //   -> [+0x190] CCommandButtonData(vtable 0x98F6A0) -> [+0x270] "Hotkey"(w3a 解析写入点, char)
    // 每级校验 vtable 防偏移漂移; 全部失败时调用方降级为录制/默认表。
    internal static class HotkeyReader
    {
        // ---- game.dll 内偏移(1.27.0.52240 patched2, RVA) ----
        public const int GameObjGlobalOff = 0xBE6350;   // CGameUI 单例
        public const int CommandBarOff = 0x3C8;          // CGameUI -> CCommandBar
        public const int SlotTableOff = 0x154;           // CCommandBar -> 槽表
        public const int RowStride = 16;                 // 槽表行步长
        public const int RowPtrAdd = 8;                  // 行指针 @ +row*16+8
        public const int ColStride = 4;                  // [行+col*4] = 按钮
        public const int ButtonDataOff = 0x190;          // CCommandButton -> CCommandButtonData
        public const int HotkeyFieldOff = 0x270;         // CCommandButtonData -> "Hotkey" char
        // vtable RVA (实际地址 = Game.dll 基址 + RVA)
        public const int VtCommandBar = 0x990030;        // CCommandBar
        public const int VtButton = 0x98F6A8;            // CCommandButton
        public const int VtButtonData = 0x98F6A0;        // CCommandButtonData
        // 物品栏候选 CCommandBar 对象偏移(CGameUI 内, 静态未定案 -> 探测)
        public static readonly int[] ItemBarCandidates = { 0x3BC, 0x3F4, 0x3F8, 0x3C0, 0x374 };

        public const string S_OK = "OK";
        public const string S_NOGAME = "NOGAME";
        public const string S_NOMAP = "NOMAP";
        public const string S_CHAIN = "CHAIN";
        public const string S_NOITEM = "NOITEM";

        private static IntPtr _hProcess = IntPtr.Zero;
        private static IntPtr _gameDllBase = IntPtr.Zero;
        private static uint _war3Pid = 0;
        public static bool Attached { get { return _hProcess != IntPtr.Zero; } }
        public static string LastError = string.Empty;

        // 供自测: 注入 mock 基址(只读)
        public static void SetMockBase(IntPtr baseAddr, uint pid, IntPtr hProcess) { _gameDllBase = baseAddr; _war3Pid = pid; _hProcess = hProcess; }
        public static void ClearMockBase() { _gameDllBase = IntPtr.Zero; _war3Pid = 0; _hProcess = IntPtr.Zero; }
        public static IntPtr GetBaseForTest() { return _gameDllBase; }

        public static bool Attach()
        {
            Detach();
            try
            {
                IntPtr hwnd = Native.FindWindow(null, "Warcraft III");
                if (hwnd == IntPtr.Zero)
                {
                    IntPtr found = IntPtr.Zero;
                    Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        string t = Native.GetWindowTextSafe(h);
                        if (t.IndexOf("Warcraft", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
                        return true;
                    }, IntPtr.Zero);
                    hwnd = found;
                }
                if (hwnd == IntPtr.Zero) { LastError = S_NOGAME + ": war3 窗口未找到"; return false; }
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) { LastError = S_NOGAME + ": GetWindowThreadProcessId 失败"; return false; }
                // 只读权限(自动读不需要写)
                _hProcess = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, pid);
                if (_hProcess == IntPtr.Zero || _hProcess == (IntPtr)(-1))
                {
                    _hProcess = IntPtr.Zero;
                    LastError = S_NOGAME + ": OpenProcess 失败 " + Marshal.GetLastWin32Error();
                    return false;
                }
                _war3Pid = pid;

                uint need = 0;
                Native.EnumProcessModules(_hProcess, null, 0, out need);
                int count = (int)(need / IntPtr.Size);
                if (count <= 0) { LastError = S_CHAIN + ": 模块枚举失败"; Detach(); return false; }
                IntPtr[] mods = new IntPtr[count];
                Native.EnumProcessModules(_hProcess, mods, need, out need);
                bool foundDll = false;
                for (int i = 0; i < mods.Length; i++)
                {
                    StringBuilder sb = new StringBuilder(260);
                    Native.GetModuleBaseName(_hProcess, mods[i], sb, 260);
                    if (sb.ToString().Equals("Game.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Native.MODULEINFO mi;
                        if (Native.GetModuleInformation(_hProcess, mods[i], out mi, (uint)Marshal.SizeOf(typeof(Native.MODULEINFO))))
                        {
                            _gameDllBase = mi.lpBaseOfDll;
                            foundDll = true;
                            break;
                        }
                    }
                }
                if (!foundDll) { LastError = S_CHAIN + ": Game.dll 模块未找到"; Detach(); return false; }
                return true;
            }
            catch (Exception ex) { LastError = S_NOGAME + ": 附加异常 " + ex.Message; return false; }
        }

        public static void Detach()
        {
            if (_hProcess != IntPtr.Zero) Native.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero; _gameDllBase = IntPtr.Zero; _war3Pid = 0;
        }

        private static bool R32(IntPtr addr, out uint v)
        {
            v = 0;
            if (_hProcess == IntPtr.Zero) return false;
            byte[] b = new byte[4];
            int rd;
            if (!Native.ReadProcessMemory(_hProcess, addr, b, 4, out rd) || rd != 4) return false;
            v = BitConverter.ToUInt32(b, 0);
            return true;
        }

        private static IntPtr PtrAdd(IntPtr p, int off) { return new IntPtr(p.ToInt64() + off); }

        // 读一个按钮对象的 hotkey char(+0x190 -> Data +0x270), 校验 vtable; 失败返回 0
        public static uint ReadButtonHotkey(IntPtr btn)
        {
            uint vt;
            if (!R32(btn, out vt)) return 0;
            if ((int)vt != (int)(_gameDllBase.ToInt64() + VtButton)) return 0;   // 非 CCommandButton
            uint dataRaw;
            if (!R32(PtrAdd(btn, ButtonDataOff), out dataRaw)) return 0;
            if (dataRaw == 0) return 0;
            IntPtr data = new IntPtr(dataRaw);
            uint dvt;
            if (!R32(data, out dvt)) return 0;
            if ((int)dvt != (int)(_gameDllBase.ToInt64() + VtButtonData)) return 0; // 非 CCommandButtonData
            uint hk;
            if (!R32(PtrAdd(data, HotkeyFieldOff), out hk)) return 0;
            // w3a "Hotkey" 解析写入: 值形态 = char(ASCII/VK 低位字节, 如 0x51='Q'=VK_Q, 0x31='1')
            uint c = hk & 0xFF;
            if (c == 0 || c == 0xFF) return 0;
            if (c >= 0x30 && c <= 0x5A) return c;      // 字母数字
            if (c >= 0x21 && c <= 0x2F) return c;      // 符号
            if (c >= 0x70 && c <= 0x7B) return c;      // VK_F1-F12
            if (c >= 0x08 && c <= 0x2D) return c;      // 其他 VK
            return c;
        }

        // 读技能栏 12 格: 返回 false 且 out status 给分类
        public static bool ReadSkillBar(uint[] outKeys, out string status)
        {
            status = S_NOMAP;
            if (_hProcess == IntPtr.Zero) { status = S_NOGAME; return false; }
            uint uiRaw;
            if (!R32(PtrAdd(_gameDllBase, GameObjGlobalOff), out uiRaw) || uiRaw == 0) { status = S_NOMAP; return false; }
            IntPtr ui = new IntPtr(uiRaw);
            uint cbRaw;
            if (!R32(PtrAdd(ui, CommandBarOff), out cbRaw) || cbRaw == 0) { status = S_NOMAP; return false; }
            IntPtr cb = new IntPtr(cbRaw);
            uint cbVt;
            if (!R32(cb, out cbVt) || (int)cbVt != (int)(_gameDllBase.ToInt64() + VtCommandBar)) { status = S_CHAIN; return false; }
            uint tblRaw;
            if (!R32(PtrAdd(cb, SlotTableOff), out tblRaw) || tblRaw == 0) { status = S_CHAIN; return false; }
            IntPtr tbl = new IntPtr(tblRaw);
            int n = 0;
            for (int row = 0; row < 3 && n < outKeys.Length; row++)
            {
                uint rowPtrRaw;
                if (!R32(PtrAdd(tbl, row * RowStride + RowPtrAdd), out rowPtrRaw) || rowPtrRaw == 0) { status = S_CHAIN; return false; }
                IntPtr rowPtr = new IntPtr(rowPtrRaw);
                for (int col = 0; col < 4 && n < outKeys.Length; col++)
                {
                    uint btnRaw;
                    if (!R32(PtrAdd(rowPtr, col * ColStride), out btnRaw) || btnRaw == 0) { outKeys[n++] = 0; continue; }
                    outKeys[n++] = ReadButtonHotkey(new IntPtr(btnRaw));
                }
            }
            status = S_OK;
            return true;
        }

        // 读物品栏 6 格: 从 CGameUI 候选偏移探测第二个 CCommandBar(vtable 0x990030);
        // 静态未定案 -> 探测失败返回 false (status=NOITEM), 调用方降级录制。
        public static bool ReadItemBar(uint[] outKeys, out string status)
        {
            status = S_NOITEM;
            if (_hProcess == IntPtr.Zero) { status = S_NOGAME; return false; }
            uint uiRaw;
            if (!R32(PtrAdd(_gameDllBase, GameObjGlobalOff), out uiRaw) || uiRaw == 0) { status = S_NOMAP; return false; }
            IntPtr ui = new IntPtr(uiRaw);
            foreach (int off in ItemBarCandidates)
            {
                uint objRaw;
                if (!R32(PtrAdd(ui, off), out objRaw) || objRaw == 0) continue;
                IntPtr obj = new IntPtr(objRaw);
                uint vt;
                if (!R32(obj, out vt)) continue;
                if ((int)vt != (int)(_gameDllBase.ToInt64() + VtCommandBar)) continue; // 只认 CCommandBar 类
                // 同技能栏结构读 2 行 x 3 列(物品 6 格)
                uint tblRaw;
                if (!R32(PtrAdd(obj, SlotTableOff), out tblRaw) || tblRaw == 0) continue;
                IntPtr tbl = new IntPtr(tblRaw);
                int n = 0;
                for (int row = 0; row < 2 && n < outKeys.Length; row++)
                {
                    uint rowPtrRaw;
                    if (!R32(PtrAdd(tbl, row * RowStride + RowPtrAdd), out rowPtrRaw) || rowPtrRaw == 0) { status = S_CHAIN; return false; }
                    IntPtr rowPtr = new IntPtr(rowPtrRaw);
                    for (int col = 0; col < 3 && n < outKeys.Length; col++)
                    {
                        uint btnRaw;
                        if (!R32(PtrAdd(rowPtr, col * ColStride), out btnRaw) || btnRaw == 0) { outKeys[n++] = 0; continue; }
                        outKeys[n++] = ReadButtonHotkey(new IntPtr(btnRaw));
                    }
                }
                status = S_OK;
                return true;
            }
            return false;
        }

        // 全量读取: 技能 12 + 物品 6 -> 写入 cfg.AutoTrigger; 返回状态串
        public static string RefreshInto(KeybindConfig cfg)
        {
            if (!cfg.AutoReadHotkeys) { cfg.AutoReadStatus = "OFF"; return "OFF"; }
            if (!Attached && !Attach()) { cfg.AutoReadStatus = LastError; return LastError; }
            uint[] skill = new uint[Vk.SlotsSkill];
            uint[] item = new uint[Vk.SlotsItem];
            string s1, s2;
            bool ok1 = ReadSkillBar(skill, out s1);
            bool ok2 = ReadItemBar(item, out s2);
            for (int i = 0; i < Vk.SlotsSkill; i++) cfg.SetAutoTrigger(i, ok1 ? skill[i] : 0);
            for (int i = 0; i < Vk.SlotsItem; i++) cfg.SetAutoTrigger(Vk.SlotsSkill + i, ok2 ? item[i] : 0);
            string st = (ok1 ? "OK" : s1) + "+" + (ok2 ? "OK" : s2);
            cfg.AutoReadStatus = st;
            return st;
        }
    }

    // ============ 游戏内改键面板(槽位驱动: 4×3 技能 + 2×3 物品) ============
    internal class PanelForm : Form
    {
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private readonly KeybindConfig _cfg;
        private readonly KeybindEngine _engine;
        private int _recSlot = -1;                 // 正在录制的格索引(0-17 面板格; -1=无)
        private int _recMode = 0;                  // 0=绑键(KEYBINDS), 1=录触发键(SlotTriggers)
        private Rectangle _bloodRect;
        private Rectangle _camRect;
        private Rectangle[] _slotRects = new Rectangle[Vk.PanelSlots];  // 0-11=A_ 技能, 12-17=I_ 物品

        public PanelForm(KeybindConfig cfg, KeybindEngine engine)
        {
            _cfg = cfg;
            _engine = engine;
            Text = "War3 改键面板 v3";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.9;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(445, 360);
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

        protected override bool ShowWithoutActivation { get { return true; } }

        public bool IsRecSlot { get { return _recSlot >= 0; } }
        public int RecSlotIndex { get { return _recSlot; } }
        public bool IsTriggerMode() { return _recMode == 1; }

        public void TogglePanel()
        {
            if (Visible) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel()
        {
            _recSlot = -1;
            Show();
            Invalidate();
            Logger.Write("改键面板已呼出");
        }

        public void HidePanel()
        {
            _recSlot = -1;
            Hide();
            Logger.Write("改键面板已隐藏");
        }

        public void RefreshGrid() { Invalidate(); }

        // 点击面板格进入录制(0=绑键)
        public bool BeginRecSlot(int slot)
        {
            if (slot < 0 || slot >= Vk.PanelSlots) return false;
            _recSlot = slot;
            _recMode = 0;
            Invalidate();
            return true;
        }

        // 托盘: 录制槽位当前触发键(依次按实际热键)
        public bool BeginRecTrigger(int slot)
        {
            if (slot < 0 || slot >= Vk.PanelSlots) return false;
            _recSlot = slot;
            _recMode = 1;
            Invalidate();
            return true;
        }

        public void CompleteRec(uint vk)
        {
            int done = _recSlot;
            string name = done >= 0 && done < Vk.SlotsTotal ? Vk.SlotNames[done] : "?";
            _recSlot = -1;
            Logger.Write("面板录制完成: " + name + " <- " + Vk.Name(vk) + " (0x" + vk.ToString("X2") + ")");
            Invalidate();
        }

        public void CancelRec()
        {
            _recSlot = -1;
            Logger.Write("面板录制已取消");
            Invalidate();
        }

        private void OnPanelMouseClick(object sender, MouseEventArgs e)
        {
            int hit = HitTest(_bloodRect, _camRect, _slotRects, e.Location);
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
            if (hit >= 1 && hit <= Vk.PanelSlots)
            {
                // 左键=绑定快捷键; 右键=录制该格当前触发键(游戏内该格实际热键)
                if (e.Button == MouseButtons.Right)
                    BeginRecTrigger(hit - 1);
                else
                    BeginRecSlot(hit - 1);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Black);

            Rectangle bloodRect, camRect;
            ComputeLayout(ClientSize.Width, out bloodRect, out camRect, out _slotRects);
            _bloodRect = bloodRect;
            _camRect = camRect;

            using (Font fTitle = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
            using (Font fLabel = new Font("Microsoft YaHei UI", 8.5f))
            using (Font fKey = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (Font fHint = new Font("Microsoft YaHei UI", 8f))
            {
                g.DrawString("War3 改键面板 v3", fTitle, Brushes.White, 10, 6);
                string hideHint = "F7 隐藏";
                SizeF hs = g.MeasureString(hideHint, fHint);
                g.DrawString(hideHint, fHint, Brushes.Gray, Width - hs.Width - 10, 8);

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

                // 技能 12 格(4列×3行 A_X0Y0..)
                int gx0 = 10, gy0 = 76;
                int cw = 64, ch = 34, gap = 5;
                g.DrawString("技能栏(4×3, 点击格子→按快捷键=绑定该格)", fHint, Brushes.DarkGray, gx0, gy0 - 16);
                int idx = 0;
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        Rectangle r = new Rectangle(gx0 + x * (cw + gap), gy0 + y * (ch + gap), cw, ch);
                        _slotRects[idx] = r;
                        DrawSlot(g, r, idx, fKey, fHint);
                        idx++;
                    }

                // 物品 6 格(2列×3行 I_X0Y0..)
                int iy0 = gy0 + 3 * (ch + gap) + 16;
                g.DrawString("物品栏(2×3, 点击录制)", fHint, Brushes.DarkGray, gx0, iy0 - 16);
                for (int y = 0; y < 3; y++)
                    for (int x = 0; x < 2; x++)
                    {
                        Rectangle r = new Rectangle(gx0 + x * (cw + gap), iy0 + y * (ch + gap), cw, ch);
                        _slotRects[idx] = r;
                        DrawSlot(g, r, idx, fKey, fHint);
                        idx++;
                    }

                // 底部提示/录制状态
                if (_recSlot >= 0)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(200, 60, 40)))
                    {
                        Rectangle bar = new Rectangle(10, Height - 28, Width - 20, 20);
                        g.FillRectangle(b, bar);
                        string mode = _recMode == 0 ? "绑定快捷键" : "录制该格当前触发键";
                        g.DrawString("正在录制 " + Vk.SlotNames[_recSlot] + " (" + mode + ") ... 请按键(Esc 取消)", fLabel, Brushes.White, 16, Height - 25);
                    }
                }
                else
                {
                    string bound = _cfg.BoundCount.ToString();
                    g.DrawString("已绑定 " + bound + "/18 格 · Esc 取消 · 0=视距拉远 · .=视距拉近", fHint, Brushes.DarkGray, 10, Height - 24);
                }
            }
        }

        // 布局(与 SelfTest 共用): 4×3 技能 + 2×3 物品, 445x360 内不越界
        internal static void ComputeLayout(int panelWidth, out Rectangle bloodRect, out Rectangle camRect, out Rectangle[] slotRects)
        {
            bloodRect = new Rectangle(10, 32, 86, 20);
            camRect = new Rectangle(108, 32, 150, 20);
            slotRects = new Rectangle[Vk.PanelSlots];
            int gx0 = 10, gy0 = 76;
            int cw = 64, ch = 34, gap = 5;
            int idx = 0;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    slotRects[idx++] = new Rectangle(gx0 + x * (cw + gap), gy0 + y * (ch + gap), cw, ch);
            int iy0 = gy0 + 3 * (ch + gap) + 16;
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 2; x++)
                    slotRects[idx++] = new Rectangle(gx0 + x * (cw + gap), iy0 + y * (ch + gap), cw, ch);
        }

        // 命中: 0=无, 1-18=面板格(0-17 索引+1), 201=血条, 202=视距
        internal static int HitTest(Rectangle bloodRect, Rectangle camRect, Rectangle[] slotRects, Point p)
        {
            if (bloodRect.Contains(p)) return 201;
            if (camRect.Contains(p)) return 202;
            for (int i = 0; i < slotRects.Length; i++)
                if (slotRects[i].Contains(p)) return i + 1;
            return 0;
        }

        private void DrawSlot(Graphics g, Rectangle r, int slot, Font fKey, Font fHint)
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
            // 第一行: 槽位名(短名)
            string shortName = Vk.SlotNames[slot];
            g.DrawString(shortName, fHint, Brushes.Gray, r.Left + 4, r.Top + 2);
            // 第二行: 绑定键 -> 触发键(v3 自动读来源用青色标记)
            uint bind = _cfg.BindKey[slot];
            uint trig = _cfg.EffectiveTrigger(slot);
            bool auto = _cfg.AutoTrigger[slot] != 0;
            string line = (bind != 0 ? Vk.Name(bind) : "空") + " -> " + (trig != 0 ? Vk.Name(trig) : "-");
            Brush tb = rec ? Brushes.Yellow : (auto ? Brushes.Cyan : Brushes.White);
            g.DrawString(line, fKey, tb, r.Left + 4, r.Top + 14);
        }
    }

    // ============ war3 窗口识别 ============
    internal class War3Window
    {
        private readonly KeybindConfig _cfg;
        public IntPtr HWnd { get; private set; }
        public uint Pid { get; private set; }
        private bool _chatOpen = false;

        public War3Window(KeybindConfig cfg) { _cfg = cfg; }

        public void Refresh()
        {
            IntPtr h = Native.FindWindow(null, "Warcraft III");
            if (h == IntPtr.Zero)
            {
                Native.EnumWindows(delegate(IntPtr hw, IntPtr l)
                {
                    string t = Native.GetWindowTextSafe(hw);
                    if (t.IndexOf("Warcraft", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        h = hw;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            }
            if (h != IntPtr.Zero)
            {
                HWnd = h;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                Pid = pid;
            }
        }

        public bool IsForeground()
        {
            if (!_cfg.ForegroundCheck) return true;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == HWnd) return true;
            if (fg == IntPtr.Zero || HWnd == IntPtr.Zero) return false;
            uint fgPid, wPid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            Native.GetWindowThreadProcessId(HWnd, out wPid);
            return fgPid == wPid;
        }

        public bool IsChat()
        {
            if (_chatOpen) return true;
            try
            {
                if (HWnd == IntPtr.Zero) return false;
                uint tid;
                Native.GetWindowThreadProcessId(HWnd, out tid);
                Native.GUITHREADINFO gti = new Native.GUITHREADINFO();
                gti.cbSize = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO));
                if (Native.GetGUIThreadInfo(tid, ref gti))
                    return gti.hwndFocus != IntPtr.Zero && gti.hwndFocus != HWnd;
            }
            catch { }
            return false;
        }

        public void OnEnterKey() { _chatOpen = !_chatOpen; }
        public void OnEscapeKey() { _chatOpen = false; }
    }

    // ============ 改键引擎(槽位驱动) ============
    internal class KeybindEngine
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private IntPtr _hook;
        private readonly Native.LowLevelKeyboardProc _proc;
        private bool _recording = false;     // 12格顺序录制(进图后录触发键)
        private int _recIndex = 0;
        private PanelForm _panel;

        public bool Recording { get { return _recording || (_panel != null && _panel.IsRecSlot); } }

        public KeybindEngine(KeybindConfig cfg, War3Window war3)
        {
            _cfg = cfg;
            _war3 = war3;
            _proc = HookCallback;
        }

        public void AttachPanel(PanelForm panel) { _panel = panel; }

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
            Logger.Write("WH_KEYBOARD_LL 钩子已安装, 已绑定格 " + _cfg.BoundCount + " 格");
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

        // 进图后: 依次录技能 12 格 + 物品 6 格的实际热键 -> SlotTriggers
        public bool StartRecording()
        {
            if (_recording) return false;
            _recording = true;
            _recIndex = 0;
            Logger.Write("进入录制模式: 依次按 18 格(技能12+物品6)的实际热键（Esc 取消, 跳过=按任意未占用键再取消）");
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

                // 1) 顺序录制(进图录 18 格触发键)
                if (_recording)
                {
                    if (!down) return (IntPtr)1;
                    if (vk == 0x1B)
                    {
                        CancelRecording();
                        return (IntPtr)1;
                    }
                    if (_war3.IsChat()) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    if (_recIndex < Vk.PanelSlots)
                    {
                        _cfg.TriggerKey[_recIndex] = vk;
                        _recIndex++;
                        if (_recIndex >= Vk.PanelSlots)
                        {
                            _recording = false;
                            _cfg.SaveKeybinds();
                            Logger.Write("录制完成: 18 格当前触发键已保存");
                            if (_panel != null) _panel.RefreshGrid();
                        }
                    }
                    return (IntPtr)1;
                }

                // 1b) 面板格子录制(点格后捕获下一键)
                if (_panel != null && _panel.Visible && _panel.IsRecSlot)
                {
                    if (!down) return (IntPtr)1;
                    if (vk == 0x1B)
                    {
                        _panel.CancelRec();
                        return (IntPtr)1;
                    }
                    if (vk == 0x10 || vk == 0x11 || vk == 0x12) return (IntPtr)1; // 纯修饰键
                    if (_war3.IsChat()) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                    int slot = _panel.RecSlotIndex;
                    if (slot >= 0 && slot < Vk.PanelSlots)
                    {
                        // 绑键或录触发键后立即保存
                        if (_panel.IsRecSlot)  // 防重复(本分支已消费)
                        {
                            // 读模式: 由 PanelForm 私有 _recMode 决定; 引擎无法直接读, 用新方法
                            bool triggerMode = _panel.IsTriggerMode();
                            if (triggerMode) _cfg.TriggerKey[slot] = vk;
                            else _cfg.BindKey[slot] = vk;
                            _cfg.SaveKeybinds();
                            _panel.CompleteRec(vk);
                            Logger.Write(string.Format("面板改键: {0} = {1}(0x{2:X2}) [{3}]",
                                Vk.SlotNames[slot], Vk.Name(vk), vk, triggerMode ? "录触发键" : "绑快捷键"));
                        }
                    }
                    return (IntPtr)1;
                }

                // 2) 录制热键触发(进图后录各格实际热键)
                if (down && _cfg.RecordHotkey != 0 && vk == _cfg.RecordHotkey && !_war3.IsChat())
                {
                    StartRecording();
                    return (IntPtr)1;
                }

                // 2b) 镜头控制
                if (down && _cfg.CameraKeys && !_war3.IsChat() && _war3.IsForeground())
                {
                    if (vk == 0x30)
                    {
                        bool ok = CameraControl.SetDistance(_cfg.CamFar, _cfg.CamOffs);
                        Logger.Write("镜头控制: [0] 视距拉远 -> " + _cfg.CamFar + " " + (ok ? "OK" : "FAIL: " + CameraControl.LastError));
                        return (IntPtr)1;
                    }
                    if (vk == 0xBE)
                    {
                        bool ok = CameraControl.SetDistance(_cfg.CamNear, _cfg.CamOffs);
                        Logger.Write("镜头控制: [.] 视距拉近 -> " + _cfg.CamNear + " " + (ok ? "OK" : "FAIL: " + CameraControl.LastError));
                        return (IntPtr)1;
                    }
                }

                // 3) 主映射: 槽位驱动 + 旧 [Mappings] 兼容
                if ((kbd.flags & Native.LLKHF_INJECTED) == 0)
                {
                    if (_cfg.IsChatAvoid)
                    {
                        if (vk == 0x0D) _war3.OnEnterKey();
                        else if (vk == 0x1B) _war3.OnEscapeKey();
                    }
                    uint dst;
                    if (_cfg.TryResolve(vk, out dst))
                    {
                        bool fg = _war3.IsForeground();
                        bool chat = _war3.IsChat();
                        if (fg && !chat)
                        {
                            if (down)
                            {
                                SendKey(dst);
                                Logger.Write(string.Format("改键: {0}(0x{1:X2}) -> {2}(0x{3:X2}) DOWN", Vk.Name(vk), vk, Vk.Name(dst), dst));
                            }
                            return (IntPtr)1;
                        }
                        Logger.Write("放行: " + Vk.Name(vk) + " (前台=" + fg + " 聊天=" + chat + ")");
                    }
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
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

    // ============ 全局热键接收窗口 ============
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

    // ============ 托盘/主循环(profiles 切换无弹窗) ============
    internal class TrayContext : ApplicationContext
    {
        private readonly KeybindConfig _cfg;
        private readonly War3Window _war3;
        private readonly KeybindEngine _engine;
        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly System.Windows.Forms.Timer _reloadTimer;
        private readonly System.Windows.Forms.Timer _autoReadTimer;
        private FileSystemWatcher _watcher;
        private HotkeyWindow _hotkeyWin;
        private PanelForm _panel;
        private DateTime _lastMtime;
        private DateTime _lastChangeTime = DateTime.MinValue;
        private bool _reloadPending = false;
        private const int ReloadQuietMs = 400;
        private const int AutoReadIntervalMs = 1000;   // v3 自动读轮询间隔

        public TrayContext(KeybindConfig cfg, War3Window war3, KeybindEngine engine)
        {
            _cfg = cfg;
            _war3 = war3;
            _engine = engine;

            _panel = new PanelForm(cfg, engine);
            _engine.AttachPanel(_panel);

            _tray = new NotifyIcon();
            _tray.Icon = SystemIcons.Application;
            _tray.Text = "War3 改键面板 v3";
            ContextMenuStrip menu = new ContextMenuStrip();

            var panelItem = new ToolStripMenuItem("显示/隐藏改键面板（F7）");
            panelItem.Click += delegate { _panel.TogglePanel(); };
            menu.Items.Add(panelItem);

            var scanItem = new ToolStripMenuItem("重新扫描 war3 窗口");
            scanItem.Click += delegate { _war3.Refresh(); };
            menu.Items.Add(scanItem);

            // v3 自动读技能栏 hotkey 开关(兜底录制)
            var autoReadItem = new ToolStripMenuItem("自动读技能栏 hotkey: " + (_cfg.AutoReadHotkeys ? "开" : "关"));
            autoReadItem.Click += delegate
            {
                _cfg.AutoReadHotkeys = !_cfg.AutoReadHotkeys;
                autoReadItem.Text = "自动读技能栏 hotkey: " + (_cfg.AutoReadHotkeys ? "开" : "关");
                if (_cfg.AutoReadHotkeys) { RefreshAutoRead(); }
                else
                {
                    for (int i = 0; i < Vk.SlotsTotal; i++) _cfg.SetAutoTrigger(i, 0);
                    _cfg.AutoReadStatus = "OFF";
                }
                if (_panel != null) _panel.RefreshGrid();
                Notify("自动读技能栏 hotkey: " + (_cfg.AutoReadHotkeys ? "已开启（游戏内各槽位热键自动同步）" : "已关闭（用录制/默认键表）"));
            };
            menu.Items.Add(autoReadItem);

            // 录制各格当前触发键(进图后)
            var recTrigItem = new ToolStripMenuItem("录制各格当前触发键（进图后按 18 格实际热键）");
            recTrigItem.Click += delegate
            {
                if (_engine.StartRecording())
                    Notify("录制模式: 依次按技能12+物品6格实际热键（Esc 取消）");
            };
            menu.Items.Add(recTrigItem);

            // 录制单格触发键(点面板格)
            var recOneItem = new ToolStripMenuItem("录制单格触发键：先点面板格子，再按游戏内该格实际键");
            recOneItem.Click += delegate
            {
                _panel.ShowPanel();
                Notify("先点面板格子（该格进入录制），再按游戏内该格实际热键");
            };
            menu.Items.Add(recOneItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            // profiles 切换(无 OpenFileDialog: 列出现有 keybind_*.ini)
            var profilesMenu = new ToolStripMenuItem("切换配置(profiles)");
            menu.Items.Add(profilesMenu);
            RebuildProfilesMenu(profilesMenu);

            var reloadItem = new ToolStripMenuItem("重新加载配置");
            reloadItem.Click += delegate { ReloadConfig("托盘菜单"); };
            menu.Items.Add(reloadItem);

            var editItem = new ToolStripMenuItem("编辑当前配置（记事本）");
            editItem.Click += delegate { OpenEditor(); };
            menu.Items.Add(editItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var bloodItem = new ToolStripMenuItem("血条: 切换开/关（当前" + (BloodBars.Get() ? "开" : "关") + "）");
            bloodItem.Click += delegate { BloodBars.Toggle(); if (_panel != null) _panel.RefreshGrid(); };
            menu.Items.Add(bloodItem);

            var camItem = new ToolStripMenuItem("镜头控制: " + (_cfg.CameraKeys ? "开（0拉远 .拉近）" : "关"));
            camItem.Click += delegate { _cfg.CameraKeys = !_cfg.CameraKeys; if (_panel != null) _panel.RefreshGrid(); };
            menu.Items.Add(camItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var logItem = new ToolStripMenuItem("打开日志目录");
            logItem.Click += delegate
            {
                try { Process.Start("explorer.exe", "/select,\"" + Logger.LogPath + "\""); }
                catch (Exception ex) { Logger.Write("打开日志目录失败: " + ex.Message); }
            };
            menu.Items.Add(logItem);

            menu.Items.Add(new ToolStripMenuItem("-"));

            var exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitThread(); };
            menu.Items.Add(exitItem);

            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;

            ApplyHotkeys();

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

            // v3: 自动读技能栏 hotkey(1s 轮询; 失败静默, 读到了就更新 AutoTrigger)
            _autoReadTimer = new System.Windows.Forms.Timer();
            _autoReadTimer.Interval = AutoReadIntervalMs;
            _autoReadTimer.Tick += delegate { RefreshAutoRead(); };
            _autoReadTimer.Start();
            if (_cfg.AutoReadHotkeys) RefreshAutoRead();

            Logger.Write("== War3 改键面板 v3 启动 ==");
            Logger.Write(string.Format("Enabled={0} IsChatAvoid={1} UsePostMessage={2} ForegroundCheck={3} 已绑定格={4}",
                _cfg.Enabled, _cfg.IsChatAvoid, _cfg.UsePostMessage, _cfg.ForegroundCheck, _cfg.BoundCount));
            Logger.Write(string.Format("热键: 面板={0} 重载={1} 编辑={2}", _cfg.PanelHotkeyDesc, _cfg.ReloadHotkeyDesc, _cfg.EditHotkeyDesc));
            Logger.Write(string.Format("镜头: {0} 拉远={1} 拉近={2} 偏移=0x{3:X3} | 血条默认={4}",
                _cfg.CameraKeys ? "开" : "关", _cfg.CamFar, _cfg.CamNear, _cfg.CamOffs, _cfg.BloodBars ? "开" : "关"));
            LogKeybinds();
            _war3.Refresh();
        }

        private void RebuildProfilesMenu(ToolStripMenuItem profilesMenu)
        {
            profilesMenu.DropDownItems.Clear();
            string dir;
            try { dir = Path.GetDirectoryName(_cfg.ConfigPath); }
            catch { dir = null; }
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                string[] files = Directory.GetFiles(dir, "keybind*.ini", SearchOption.TopDirectoryOnly);
                Array.Sort(files);
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    bool cur = Path.GetFullPath(f).Equals(Path.GetFullPath(_cfg.ConfigPath), StringComparison.OrdinalIgnoreCase);
                    var item = new ToolStripMenuItem((cur ? "✓ " : "") + name);
                    string path = f;
                    item.Click += delegate { SwitchConfig(path); };
                    profilesMenu.DropDownItems.Add(item);
                }
            }
            catch (Exception ex) { Logger.Write("profiles 扫描失败: " + ex.Message); }
        }

        private void SetupWatcher()
        {
            try
            {
                if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
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

        // v3: 自动读技能栏 hotkey -> 写 AutoTrigger; 面板格青色=自动读来源
        private void RefreshAutoRead()
        {
            try
            {
                string st = HotkeyReader.RefreshInto(_cfg);
                if (_panel != null && _panel.Visible) _panel.RefreshGrid();
            }
            catch (Exception ex)
            {
                Logger.Write("自动读技能栏 hotkey 异常: " + ex.Message);
            }
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
            int before = _cfg.BoundCount;
            lock (_cfg.SyncRoot) { _cfg.Load(); }
            int after = _cfg.BoundCount;
            _pollTimer.Interval = Math.Max(200, _cfg.PollIntervalMs);
            ApplyHotkeys();
            _lastMtime = GetConfigMtime();
            Logger.Write(string.Format("配置已重载（{0}）: 已绑定 {1} 格（原 {2} 格）", reason, after, before));
            LogKeybinds();
            RefreshAutoRead();
            if (_panel != null) _panel.RefreshGrid();
            Notify(string.Format("配置已重载: {0} 格绑定生效", after));
        }

        private void LogKeybinds()
        {
            lock (_cfg.SyncRoot)
            {
                for (int i = 0; i < Vk.PanelSlots; i++)
                {
                    if (_cfg.BindKey[i] != 0 || _cfg.TriggerKey[i] != 0)
                        Logger.Write(string.Format("  {0}: 绑定={1} 触发={2}", Vk.SlotNames[i],
                            _cfg.BindKey[i] != 0 ? Vk.Name(_cfg.BindKey[i]) : "-",
                            _cfg.EffectiveTrigger(i) != 0 ? Vk.Name(_cfg.EffectiveTrigger(i)) : "-"));
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
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 1, _cfg.ReloadHotkeyMods, _cfg.ReloadHotkeyVk))
                    Logger.Write("重载热键注册失败（可能被其他程序占用）: " + _cfg.ReloadHotkeyDesc);
            if (_cfg.EditHotkeyVk != 0)
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 2, _cfg.EditHotkeyMods, _cfg.EditHotkeyVk))
                    Logger.Write("编辑热键注册失败（可能被其他程序占用）: " + _cfg.EditHotkeyDesc);
            if (_cfg.PanelHotkeyVk != 0)
                if (!Native.RegisterHotKey(_hotkeyWin.Handle, 3, _cfg.PanelHotkeyMods, _cfg.PanelHotkeyVk))
                    Logger.Write("面板热键注册失败（可能被其他程序占用）: " + _cfg.PanelHotkeyDesc);
        }

        private void OnHotkeyPressed(int id)
        {
            if (id == 1) ReloadConfig("热键 " + _cfg.ReloadHotkeyDesc);
            else if (id == 2) OpenEditor();
            else if (id == 3) _panel.TogglePanel();
        }

        private void OpenEditor()
        {
            try { Process.Start("notepad.exe", "\"" + _cfg.ConfigPath + "\""); }
            catch (Exception ex) { Logger.Write("打开编辑器失败: " + ex.Message); }
        }

        private void SwitchConfig(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                _cfg.SetConfigPath(full);
                if (!File.Exists(full)) _cfg.EnsureDefaultFile();
                _reloadPending = false;
                SetupWatcher();
                ReloadConfig("切换配置 " + Path.GetFileName(full));
            }
            catch (Exception ex)
            {
                Logger.Write("切换配置失败: " + ex.Message);
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
            if (_panel != null) { try { _panel.Hide(); _panel.Dispose(); } catch { } _panel = null; }
            if (_pollTimer != null) { _pollTimer.Stop(); _pollTimer.Dispose(); }
            if (_reloadTimer != null) { _reloadTimer.Stop(); _reloadTimer.Dispose(); }
            if (_autoReadTimer != null) { _autoReadTimer.Stop(); _autoReadTimer.Dispose(); }
            HotkeyReader.Detach();
            if (_watcher != null) { try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } catch { } _watcher = null; }
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
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            Logger.Write("== 退出 ==");
            base.ExitThreadCore();
        }
    }

    // ============ 自测 ============
    internal static class SelfTest
    {
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

            // ---- T1: 配置解析(KEYBINDS 槽位驱动) ----
            string tmpIni = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "keybind_v2_selftest.ini");
            try
            {
                string[] lines = new string[]
                {
                    "[keybind]", "Enabled=1", "IsChatAvoid=1",
                    "[panel]", "PanelHotkey=F7", "CameraKeys=1", "CamFar=3000", "CamNear=1650", "BloodBars=1", "CamOffs=0x5B0",
                    "[KEYBINDS]", "A_X0Y0=0x70", "A_X1Y0=W", "I_X0Y0=0x31",
                    "[SlotTriggers]", "A_X0Y0=0x51",
                    "[Mappings]", "F2=Q",
                };
                File.WriteAllLines(tmpIni, lines, new UTF8Encoding(false));
                KeybindConfig cfg = new KeybindConfig();
                cfg.SetConfigPath(tmpIni);
                cfg.Load();
                check(cfg.PanelHotkeyVk == 0x76 && cfg.PanelHotkeyMods == 0x0, "T1 面板热键解析 F7 -> 0x76 mods 0x0");
                check(Math.Abs(cfg.CamFar - 3000f) < 0.01f && Math.Abs(cfg.CamNear - 1650f) < 0.01f, "T1 CamFar=3000 CamNear=1650 解析");
                check(cfg.CamOffs == 0x5B0, "T1 CamOffs=0x5B0 解析");
                check(cfg.BindKey[0] == 0x70 && cfg.BindKey[1] == 0x57 && cfg.BindKey[12] == 0x31, "T1 KEYBINDS 解析 A_X0Y0=0x70 A_X1Y0=W I_X0Y0=0x31");
                check(cfg.TriggerKey[0] == 0x51, "T1 SlotTriggers A_X0Y0=0x51 解析");
                uint dst;
                check(cfg.TryResolve(0x70, out dst) && dst == 0x51, "T1 运行时解析 0x70 -> 槽位触发键 0x51");
                check(cfg.TryResolve(0x71, out dst) && dst == 0x51, "T1 旧 [Mappings] 兼容 F2(0x71) -> Q(0x51)");
                check(cfg.TryResolve(0x31, out dst) && dst == (uint)'1', "T1 I_X0Y0 触发键默认 1(未覆盖)");

                // ---- T2: 保存往返 ----
                cfg.BindKey[2] = 0x45;
                cfg.SaveKeybinds();
                string reread = File.ReadAllText(tmpIni, Encoding.UTF8);
                check(reread.Contains("A_X2Y0=0x45"), "T2 SaveKeybinds 写入 A_X2Y0=0x45");
                KeybindConfig cfg2 = new KeybindConfig();
                cfg2.SetConfigPath(tmpIni);
                cfg2.Load();
                check(cfg2.BindKey[2] == 0x45, "T2 重载后 BindKey[2]=0x45");
                check(cfg2.BindKey[0] == 0x70, "T2 重载后 BindKey[0]=0x70 保留");
            }
            catch (Exception ex) { check(false, "T1/T2 异常: " + ex.Message); }
            finally { try { File.Delete(tmpIni); } catch { } }

            // ---- T3: 血条注册表(IL 受限跳过) ----
            try
            {
                int il = GetIntegrityLevel();
                if (il < 0x2000)
                    check(true, "T3 血条注册表: 进程 IL=" + il + " 受限, 跳过写入验证");
                else
                {
                    bool orig = BloodBars.Get();
                    BloodBars.Set(true);
                    bool after = BloodBars.Get();
                    BloodBars.Set(orig);
                    check(after, "T3 血条写入 healthbars=1 读回=开(已恢复原值)");
                }
            }
            catch (Exception ex) { check(false, "T3 异常: " + ex.Message); }

            // ---- T4: 视距(静态锚点 + Attach) ----
            try
            {
                check(CameraControl.GameObjGlobalOff == 0xBE6350, "T4 锚点 game 对象全局偏移=0xBE6350");
                check(CameraControl.CamPtrOff == 0x254, "T4 锚点 相机指针偏移=0x254");
                string gdll = @"E:\game\Warcraft III Frozen Throne\Game.dll";
                if (File.Exists(gdll))
                {
                    long sz = new FileInfo(gdll).Length;
                    check(sz == 13187048, "T4 本地 Game.dll 13,187,048B 存在 (实际 " + sz + "B)");
                }
                else check(true, "T4 本地 Game.dll 不在默认路径, 跳过存在性检查");
                bool attached = CameraControl.Attach();
                if (attached)
                {
                    check(true, "T4 war3 运行中, Attach 成功 (pid=" + CameraControl.War3Pid + ", 基址 0x" + CameraControl.GetBaseForTest().ToString("X8") + ")");
                    CameraControl.Detach();
                }
                else
                {
                    check(true, "T4 war3 未运行, 跳过内存写入验证（Attach 逻辑=FindWindow+psapi）");
                }
            }
            catch (Exception ex) { check(false, "T4 异常: " + ex.Message); }

            // ---- T5: 视距按键 VK 语义 ----
            uint vk0, vkDot;
            check(Vk.TryParse("0", out vk0) && vk0 == 0x30, "T5 视距拉远键 0 = VK 0x30");
            check(Vk.TryParse(".", out vkDot) && vkDot == 0xBE, "T5 视距拉近键 . = VK 0xBE");

            // ---- T6: 面板布局几何与命中(纯计算) ----
            try
            {
                Rectangle bloodRect, camRect;
                Rectangle[] slotRects;
                PanelForm.ComputeLayout(445, out bloodRect, out camRect, out slotRects);
                bool inBounds = true;
                foreach (Rectangle r in slotRects) if (r.Right > 445 || r.Bottom > 360) inBounds = false;
                check(inBounds, "T6 面板 18 格(12技能4×3+6物品2×3)均在 445x360 内不越界");
                check(slotRects.Length == 18, "T6 面板格数=18");
                // 技能第1格(A_X0Y0) -> 1
                Rectangle first = slotRects[0];
                check(PanelForm.HitTest(bloodRect, camRect, slotRects, new Point(first.Left + 1, first.Top + 1)) == 1, "T6 点击技能第 1 格命中 -> 1");
                // 物品第1格(I_X0Y0, 索引12) -> 13
                Rectangle item0 = slotRects[12];
                check(PanelForm.HitTest(bloodRect, camRect, slotRects, new Point(item0.Left + item0.Width / 2, item0.Top + item0.Height / 2)) == 13, "T6 点击物品第 1 格命中 -> 13");
                // 血条 -> 201
                check(PanelForm.HitTest(bloodRect, camRect, slotRects, new Point(bloodRect.Left + 2, bloodRect.Top + 2)) == 201, "T6 点击血条状态框命中 -> 201");
                // 空白 -> 0
                check(PanelForm.HitTest(bloodRect, camRect, slotRects, new Point(5, 355)) == 0, "T6 点击空白区命中 -> 0(无)");
                // 槽位默认触发键表
                check(Vk.DefaultTrigger(0) == (uint)'Q' && Vk.DefaultTrigger(3) == (uint)'R', "T6 技能默认触发 A_X0Y0=Q A_X3Y0=R");
                check(Vk.DefaultTrigger(4) == (uint)'T' && Vk.DefaultTrigger(12) == (uint)'1', "T6 默认触发 A_X0Y1=T I_X0Y0=1");
                check(Vk.DefaultTrigger(10) == 0, "T6 第 11 格无默认触发(需录制)");
            }
            catch (Exception ex) { check(false, "T6 异常: " + ex.Message); }

            // ---- T7: v3 自动读技能栏 hotkey(静态偏移自检 + 无游戏降级) ----
            try
            {
                check(HotkeyReader.GameObjGlobalOff == 0xBE6350, "T7 CGameUI 全局偏移 0xBE6350");
                check(HotkeyReader.CommandBarOff == 0x3C8, "T7 CGameUI->CCommandBar 偏移 0x3C8");
                check(HotkeyReader.SlotTableOff == 0x154, "T7 CCommandBar 槽表偏移 0x154");
                check(HotkeyReader.ButtonDataOff == 0x190 && HotkeyReader.HotkeyFieldOff == 0x270,
                    "T7 按钮->Data 0x190 / Data->Hotkey 0x270");
                check(HotkeyReader.VtCommandBar == 0x990030 && HotkeyReader.VtButton == 0x98F6A8 && HotkeyReader.VtButtonData == 0x98F6A0,
                    "T7 vtable 0x990030/0x98F6A8/0x98F6A0");
                // 无游戏降级: 未 Attach 时 RefreshInto 必须返回 NOGAME 而非崩溃
                HotkeyReader.Detach();
                KeybindConfig c7 = new KeybindConfig();
                c7.AutoReadHotkeys = true;
                string st7 = HotkeyReader.RefreshInto(c7);
                check(st7.StartsWith("NOGAME") || st7.StartsWith("OFF"), "T7 无游戏时自动读降级不崩溃 (" + st7 + ")");
                // 关闭开关 -> OFF
                c7.AutoReadHotkeys = false;
                string st7b = HotkeyReader.RefreshInto(c7);
                check(st7b == "OFF", "T7 AutoReadHotkeys=false 返回 OFF");
                // 槽位读取优先级: AutoTrigger > TriggerKey > 默认
                KeybindConfig c8 = new KeybindConfig();
                c8.SetAutoTrigger(0, (uint)'Y');
                check(c8.EffectiveTrigger(0) == (uint)'Y', "T7 AutoTrigger 优先于默认键");
                c8.SetAutoTrigger(0, 0);
                check(c8.EffectiveTrigger(0) == (uint)'Q', "T7 清除 AutoTrigger 回落默认 Q");
                check(c8.AutoTrigger[12] == 0, "T7 物品格初始 AutoTrigger=0(未读)");
            }
            catch (Exception ex) { check(false, "T7 异常: " + ex.Message); }

            Logger.Write("== 自测结束: PASS " + pass + " / FAIL " + fail + " ==");
            Console.WriteLine("== 自测结束: PASS " + pass + " / FAIL " + fail + " ==");
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static int GetIntegrityLevel()
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x0008, out tok)) return -1;
            try
            {
                uint len;
                GetTokenInformation(tok, 25, IntPtr.Zero, 0, out len);
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (!GetTokenInformation(tok, 25, buf, len, out len)) return -2;
                    IntPtr sid = Marshal.ReadIntPtr(buf, 0);
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
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool createdNew;
            Mutex mutex = new Mutex(true, "KeybindPanel_KKRE_V3_8F2A1C", out createdNew);
            if (!createdNew) return; // 单实例(与 v1 旧实例不冲突, 可并存调试)

            if (Array.IndexOf(args, "/selftest") >= 0)
            {
                SelfTest.Run();
                return;
            }

            KeybindConfig cfg = new KeybindConfig();
            cfg.EnsureDefaultFile();
            Logger.Init(true);
            cfg.Load();
            Logger.SetEnabled(cfg.LogEnabled);
            Logger.Write("启动 PID=" + Process.GetCurrentProcess().Id);

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
