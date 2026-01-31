using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace GameOptimizer
{
    public enum OptimizeMethod { Affinity, CpuSet }

    class Program
    {
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMin, uint wMax, uint wRm);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        
        [DllImport("kernel32.dll")] private static extern uint GetLastError();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessDefaultCpuSets(IntPtr hProcess, [In] uint[] CpuSetIds, uint CpuSetIdCount);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint PROCESS_SET_LIMITED_INFORMATION = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg
        {
            public IntPtr handle; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public System.Drawing.Point p;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_CPU_SET_INFORMATION
        {
            public uint Size;
            public uint Type; // Must be 0
            public uint Id;
            public ushort Group;
            public byte LogicalProcessorIndex;
            public byte CoreIndex;
            public byte LastLevelCacheIndex;
            public byte NumaNodeIndex;
            public byte EfficiencyClass;
            public byte AllFlags;
            public uint SchedulingClass;
            public ulong AllocationTag;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemCpuSetInformation(
            IntPtr Information,
            uint BufferLength,
            out uint ReturnedLength,
            IntPtr Process,
            uint Flags);

        private static uint[] _cachedTargetCpuSetIds = Array.Empty<uint>();
        
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_AFFINITY_TAG_ID = 1;
        private const int HOTKEY_CPUSET_TAG_ID = 2;
        private const int HOTKEY_PROFILE_ID = 3;
        
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private static Dictionary<int, uint> _coreToCpuSetIdMap = new();
        private static uint[] targetCpuSets;
        

        // --- Configuration State ---
        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static readonly string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private static readonly string AppName = "GameCoreParker";
        
        private static ConfigData _config = new();
        private static readonly object _lock = new();

        // Tracker: Stores strings like "ProcessName|Method" to avoid constant re-application
        private static HashSet<string> _alreadySetStates = new();

        static void Main(string[] args)
        {
            LoadConfig();
            IntPtr hConsole = GetConsoleWindow();
            bool forceShow = args.Any(arg => arg.Equals("-show", StringComparison.OrdinalIgnoreCase));
            
            if(!forceShow)
            {
                ShowWindow(hConsole, SW_HIDE);
                // "Stalk" the window for a few seconds to ensure Windows Terminal doesn't force it open
                Task.Run(async () => { for (int i = 0; i < 5; i++) { ShowWindow(GetConsoleWindow(), SW_HIDE); await Task.Delay(500); } });
            }
            else
            {
                ShowWindow(hConsole, SW_SHOW);
                Console.WriteLine("=== CoreGameParker - alpha ===");
                Console.WriteLine("---------------------------------------");
            }

            // Register Hotkeys
            RegisterHotKey(IntPtr.Zero, HOTKEY_AFFINITY_TAG_ID, MOD_ALT, (uint)'T');         // Alt + T (Standard)
            RegisterHotKey(IntPtr.Zero, HOTKEY_CPUSET_TAG_ID, MOD_ALT, (uint)'A');           // Alt + A (Anti-Cheat)
            RegisterHotKey(IntPtr.Zero, HOTKEY_PROFILE_ID, MOD_ALT | MOD_CONTROL, (uint)'T'); // Ctrl + Alt + T (Settings)

            RebuildCpuSetCache();
            using Timer timer = new Timer(MonitorProcesses, null, 0, 5000);

            Console.WriteLine("GameCoreParker active. Use Alt+T/Alt+A to tag apps and Ctrl+Alt+T for settings.");

            NativeMsg msg = new NativeMsg();
            while (true)
            {
                if (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    if (msg.message == WM_HOTKEY)
                    {
                        int id = msg.wParam.ToInt32();
                        if (id == HOTKEY_AFFINITY_TAG_ID) ToggleTag(OptimizeMethod.Affinity);
                        else if (id == HOTKEY_CPUSET_TAG_ID) ToggleTag(OptimizeMethod.CpuSet);
                        else if (id == HOTKEY_PROFILE_ID) OpenProfileMenu(GetConsoleWindow());
                    }
                }
                Thread.Sleep(10);
            }
        }

        private static void ToggleTag(OptimizeMethod requestedMethod)
        {
            IntPtr hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out uint pid);

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                if (string.IsNullOrEmpty(name) || name.Equals("Idle", StringComparison.OrdinalIgnoreCase) || name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                    return;

                lock (_lock)
                {
                    if (_config.Apps.TryGetValue(name, out var existingMethod))
                    {
                        if (existingMethod == requestedMethod)
                        {
                            // Same method: Remove
                            _config.Apps.Remove(name);
                            _alreadySetStates.Remove($"{name}|{requestedMethod}");
                            Console.Beep(400, 250);
                            Console.WriteLine($@"'{name}' REMOVED from optimization.");
                        }
                        else
                        {
                            // Different method: Swap
                            _alreadySetStates.Remove($"{name}|{existingMethod}");
                            _config.Apps[name] = requestedMethod;
                            Console.Beep(1000, 250);
                            Console.WriteLine($@"'{name}' SWAPPED to {requestedMethod} mode.");
                        }
                    }
                    else
                    {
                        // New app
                        _config.Apps[name] = requestedMethod;
                        Console.Beep(800, 250);
                        Console.WriteLine($@"'{name}' ADDED for {requestedMethod} optimization.");
                    }
                }
                SaveConfig();
            }
            catch { }
        }
        
        private static void RefreshCpuSetMapping()
        {
            uint bufferLength = 0;
            
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out bufferLength, IntPtr.Zero, 0);
            if (bufferLength == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)bufferLength);
            try
            {
                if (GetSystemCpuSetInformation(buffer, bufferLength, out _, IntPtr.Zero, 0))
                {
                    _coreToCpuSetIdMap.Clear();
                    int offset = 0;
                    while (offset < bufferLength)
                    {
                        // Safety: Ensure we don't read past the buffer
                        if (offset + 32 > bufferLength) break;

                        var info = Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(buffer + offset);
                
                        // Type 0 is CpuSetInformation
                        if (info.Type == 0)
                        {
                            _coreToCpuSetIdMap[info.LogicalProcessorIndex] = info.Id;
                        }
                
                        offset += (int)info.Size;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Error] Topology Mapping failed: {ex.Message}");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        
        private static void RebuildCpuSetCache()
        {
            RefreshCpuSetMapping();
            if (_config.AffinityMask == 0)
            {
                _cachedTargetCpuSetIds = Array.Empty<uint>();
                return;
            }

            var ids = new List<uint>();
            for (int i = 0; i < 64; i++)
            {
                if ((_config.AffinityMask & (1L << i)) != 0)
                {
                    if (_coreToCpuSetIdMap.TryGetValue(i, out uint realId))
                        ids.Add(realId);
                }
            }

            _cachedTargetCpuSetIds = ids.ToArray();
    
            // Debug log to verify it worked (Only visible if -show is used)
            if (_cachedTargetCpuSetIds.Length > 0)
                Console.WriteLine($"[Cache] Rebuilt: {_cachedTargetCpuSetIds.Length} cores mapped to System IDs.");
        }

        private static void MonitorProcesses(object? state)
        {
            // Safety: If cache is empty but mask is set, rebuild it (Self-healing)
            if (_cachedTargetCpuSetIds.Length == 0 && _config.AffinityMask != 0)
            {
                RebuildCpuSetCache();
            }

            // Fast exit if still empty
            if (_cachedTargetCpuSetIds.Length == 0) return;

            Dictionary<string, OptimizeMethod> targets;
            lock (_lock) { targets = new Dictionary<string, OptimizeMethod>(_config.Apps); }

            // Targeted Cleanup (as optimized before)
            var statesToCleanup = new List<string>();
            foreach (var stateKey in _alreadySetStates)
            {
                string exeName = stateKey.Split('|')[0];
                var inst = Process.GetProcessesByName(exeName);
                if (inst.Length == 0) statesToCleanup.Add(stateKey);
                foreach (var i in inst) i.Dispose();
            }
            foreach (var key in statesToCleanup) _alreadySetStates.Remove(key);

            // Apply optimizations...
            foreach (var target in targets)
            {
                string stateKey = $"{target.Key}|{target.Value}";
                if (_alreadySetStates.Contains(stateKey)) continue;

                var procs = Process.GetProcessesByName(target.Key);
                bool applied = false;
                foreach (var p in procs)
                {
                    try
                    {
                        if (target.Value == OptimizeMethod.Affinity)
                        {
                            p.ProcessorAffinity = (IntPtr)_config.AffinityMask;
                            p.PriorityBoostEnabled = true;
                            p.PriorityClass = ProcessPriorityClass.High;
                            applied = true;
                        }
                        else
                        {
                            IntPtr hProc = OpenProcess(0x1000 | 0x2000, false, (uint)p.Id);
                            if (hProc != IntPtr.Zero)
                            {
                                // Use the cached IDs
                                applied = SetProcessDefaultCpuSets(hProc, _cachedTargetCpuSetIds, (uint)_cachedTargetCpuSetIds.Length);
                                CloseHandle(hProc);
                            }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                if (applied)
                {
                    _alreadySetStates.Add(stateKey);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Optimized '{target.Key}' via {target.Value}");
                }
                
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
        }

        private static void OpenProfileMenu(IntPtr hConsole)
        {
            if (hConsole == IntPtr.Zero)
            {
                AllocConsole();
                hConsole = GetConsoleWindow();
                var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(standardOutput);
            }
            
            ShowWindow(hConsole, SW_SHOW);
            if (GetStdHandle(STD_OUTPUT_HANDLE) == IntPtr.Zero) return;

            int totalCores = Environment.ProcessorCount;

            while (true)
            {
                Console.Clear();
                Console.WriteLine("=== CPU AFFINITY OPTIMIZER SETTINGS ===");
                Console.WriteLine($"Autostart: {(IsAutostartEnabled() ? "ENABLED" : "DISABLED")}");
                Console.WriteLine("---------------------------------------");
                Console.WriteLine("COMMANDS:");
                Console.WriteLine("  [0-15]   - Set specific core range");
                Console.WriteLine("  [Number] - Toggle specific core index");
                Console.WriteLine("  [A]      - Toggle Autostart");
                Console.WriteLine("  [S/Ent]  - Save and Return to Background");
                Console.WriteLine("---------------------------------------\n");

                for (int i = 0; i < totalCores; i++)
                {
                    bool isSet = (_config.AffinityMask & (1L << i)) != 0;
                    Console.Write($"[{ (isSet ? "X" : " ") }] Core {i,-2}  ");
                    if ((i + 1) % 4 == 0) Console.WriteLine();
                }

                Console.Write("\nInput: ");
                string? input = Console.ReadLine()?.Trim().ToUpper();
                if (string.IsNullOrEmpty(input) || input == "S") break;

                if (input == "A") { ToggleAutostart(); continue; }

                if (input.Contains("-")) _config.AffinityMask = ParseRange(input, totalCores);
                else if (int.TryParse(input, out int coreIdx) && coreIdx >= 0 && coreIdx < totalCores)
                    _config.AffinityMask ^= (1L << coreIdx);
            }

            lock (_lock) _alreadySetStates.Clear();
            SaveConfig();
            ShowWindow(hConsole, SW_HIDE);
        }

        private static long ParseRange(string input, int maxCores)
        {
            long mask = 0;
            string[] parts = input.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out int start) && int.TryParse(parts[1], out int end))
                for (int i = Math.Max(0, start); i <= Math.Min(end, maxCores - 1); i++) mask |= (1L << i);
            return mask;
        }

        private static bool IsAutostartEnabled()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            return key?.GetValue(AppName) != null;
        }

        private static void ToggleAutostart()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
            if (key == null) return;
            if (IsAutostartEnabled()) key.DeleteValue(AppName, false);
            else key.SetValue(AppName, $"\"{Process.GetCurrentProcess().MainModule?.FileName}\"");
            Thread.Sleep(800);
        }

        private static void LoadConfig()
        {
            if (!File.Exists(ConfigPath)) return;
            try { _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), SourceGenerationContext.Default.ConfigData) ?? new(); }
            catch { _config = new ConfigData(); }
        }

        private static void SaveConfig()
        {
            try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, SourceGenerationContext.Default.ConfigData)); }
            catch { }
        }
    }

    public class ConfigData
    {
        public Dictionary<string, OptimizeMethod> Apps { get; set; } = new();
        public long AffinityMask { get; set; } = 0;
    }
    
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ConfigData))]
    internal partial class SourceGenerationContext : JsonSerializerContext { }
}