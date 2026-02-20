using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
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
        // --- WinAPI Imports ---
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMin, uint wMax, uint wRm);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
        
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr hMem);
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessDefaultCpuSets(IntPtr hProcess, [In] uint[] CpuSetIds, uint CpuSetIdCount);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr Information, uint BufferLength, out uint ReturnedLength, IntPtr Process, uint Flags);

        // --- Power Management Imports ---
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid schemeGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, out IntPtr dest);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr set, byte[] buffer, uint bufSize);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr set, IntPtr buffer, ref uint bufSize);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint flags, uint index, ref Guid buffer, ref uint bufSize);
        
        // Power GUIDs
        private static Guid GUID_BALANCED = new Guid("381b4222-f694-41f0-9685-ff5bb260df2e");
        private static Guid GUID_HIGH_PERF = new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        
        // These GUIDs are used for processor performance states and core parking
        private static Guid GUID_SUBGROUP_HARDDISK = new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60");
        private static Guid GUID_OFFHARDDISK = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e"); // Hard disk idle timeout | AC 0 DC 0
        
        private static Guid GUID_POWERPLANTYPE = new Guid("245d8541-3943-4422-b025-13a784f679b7"); // Power Plan Type | AC 1 DC 2 
        
        private static Guid GUID_SUBGROUP_PROCESSOR = new Guid("54533251-82be-4824-96c1-47b60b740d00");
        private static Guid GUID_EPP = new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863"); // Energy Performance Preference (EPP) | AC 10 (Highest Performance) DC 10 (Highest Performance)
        private static Guid GUID_PROCESSOR_PARKING_MIN = new Guid("0cc5b647-c1df-4637-891a-dec35c318583"); // Processor performance core parking min cores | AC 100 DC 100
        private static Guid GUID_PROCESSOR_PARKING_P_MIN = new Guid("0cc5b647-c1df-4637-891a-dec35c318584"); // Processor performance core parking min cores for power efficiency | AC 100 DC 100
        private static Guid GUID_PROCESSOR_STATE_MIN = new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c"); // Processor state minimal state | AC 100 DC 100
        private static Guid GUID_PROCESSOR_STATE_MAX = new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed"); // Processor state maximum state | AC 100 DC 100
        
        private static Guid GUID_SUBGROUP_DISPLAY = new Guid("0cc5b647-c1df-4637-891a-dec35c318583");
        private static Guid GUID_TURNDISPLAY_OFF = new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e"); // Display idle timeout | AC 0
        
        
        
        private const string REG_PATH_PRIORITY = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
        private const string REG_PATH_GAMES_TASK = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
        private const string REG_PRIORITY_CONTROL = @"System\CurrentControlSet\Control\PriorityControl";
        
        //Image File Execution Options (IFEO) Path
        private const string IFEO_PATH = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        
        private const string POWER_PLAN_NAME = "GameCoreParker Performance";
        private const int STD_OUTPUT_HANDLE = -11;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg { public IntPtr handle; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public System.Drawing.Point p; }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_CPU_SET_INFORMATION { public uint Size; public uint Type; public uint Id; public ushort Group; public byte LogicalProcessorIndex; public byte CoreIndex; public byte LastLevelCacheIndex; public byte NumaNodeIndex; public byte EfficiencyClass; public byte AllFlags; public uint SchedulingClass; public ulong AllocationTag; }

        private static uint[] _cachedTargetCpuSetIds = Array.Empty<uint>();
        private static Dictionary<int, uint> _coreToCpuSetIdMap = new();
        private static bool _isPowerPlanActive = false;
        private static Guid _systemFoundGuid = Guid.Empty;
        
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_AFFINITY_TAG_ID = 1;
        private const int HOTKEY_CPUSET_TAG_ID = 2;
        private const int HOTKEY_PROFILE_ID = 3;
        
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
        private static readonly string AppName = "GameCoreParker";
        private static ConfigData _config = new();
        private static readonly object _lock = new();
        private static HashSet<string> _alreadySetStates = new();

        static void Main(string[] args)
        {
            try
            {
                EnsureSingleInstance();
                LoadConfig();
                IntPtr hConsole = GetConsoleWindow();
                bool forceShow = args.Any(arg => arg.Equals("-show", StringComparison.OrdinalIgnoreCase));
                
                if (_config.EnableLogging)
                {
                    Console.SetOut(new MultiTextWriter(Console.Out, LogPath));
                    Console.WriteLine("--- GameCoreParker Service Started ---");
                }
                
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
                    ShowWindow(hConsole, 0);
                    Task.Run(async () => { for (int i = 0; i < 5; i++) { ShowWindow(GetConsoleWindow(), 0); await Task.Delay(500); } });
                }


                RegisterHotKey(IntPtr.Zero, HOTKEY_AFFINITY_TAG_ID, MOD_ALT, (uint)'T');         // Alt + T (Standard)
                RegisterHotKey(IntPtr.Zero, HOTKEY_CPUSET_TAG_ID, MOD_ALT, (uint)'A');           // Alt + A (Anti-Cheat)
                RegisterHotKey(IntPtr.Zero, HOTKEY_PROFILE_ID, MOD_ALT | MOD_CONTROL, (uint)'T'); // Ctrl + Alt + T (Settings)

                RebuildCpuSetCache();
                FindOrCreatePowerPlan();
                
                Console.WriteLine("GameCoreParker active. Use Alt+T/Alt+A to tag apps and Ctrl+Alt+T for settings.");
                using Timer timer = new Timer(MonitorProcesses, null, 0, 5000);

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
            catch (Exception ex) {
                Console.WriteLine("GameCoreParker failed to launch for reason:\n" + ex);
            }
            
        }
        
        private static void EnsureSingleInstance()
        {
            Process current = Process.GetCurrentProcess();
            Process[] runningProcesses = Process.GetProcessesByName(current.ProcessName);

            foreach (Process p in runningProcesses)
            {
                if (p.Id != current.Id)
                {
                    try
                    {
                        p.Kill();
                        p.WaitForExit(2000); 
                    }
                    catch (Exception)
                    { }
                    finally
                    {
                        p.Dispose();
                    }
                }
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
                string fullPath = proc.MainModule?.FileName ?? ""; // We need the full path for GameStore

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
                            RemoveIfeoRegistry(name);
                            Console.Beep(400, 250);
                            Console.WriteLine($@"'{name}' REMOVED from optimization.");
                        }
                        else
                        {
                            // Different method: Swap
                            _alreadySetStates.Remove($"{name}|{existingMethod}");
                            _config.Apps[name] = requestedMethod;
                            ApplyIfeoRegistry(name);
                            Console.Beep(1000, 250);
                            Console.WriteLine($@"'{name}' SWAPPED to {requestedMethod} mode.");
                        }
                    }
                    else
                    {
                        // New app
                        _config.Apps[name] = requestedMethod;
                        ApplyIfeoRegistry(name);
                        Console.Beep(800, 250);
                        Console.WriteLine($@"'{name}' ADDED for {requestedMethod} optimization.");
                    }
                }
                SaveConfig();
            }
            catch { }
        }
                        
        private static void RebuildCpuSetCache()
        {
            uint bufferLength = 0;
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out bufferLength, IntPtr.Zero, 0);
            if (bufferLength == 0) return;
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferLength);
            try {
                if (GetSystemCpuSetInformation(buffer, bufferLength, out _, IntPtr.Zero, 0)) {
                    _coreToCpuSetIdMap.Clear();
                    int offset = 0;
                    while (offset + 32 <= bufferLength) {
                        var info = Marshal.PtrToStructure<SYSTEM_CPU_SET_INFORMATION>(buffer + offset);
                        if (info.Type == 0) _coreToCpuSetIdMap[info.LogicalProcessorIndex] = info.Id;
                        offset += (int)info.Size;
                    }
                }
            } finally { Marshal.FreeHGlobal(buffer); }

            if (_config.AffinityMask == 0) { _cachedTargetCpuSetIds = Array.Empty<uint>(); return; }
            var ids = new List<uint>();
            for (int i = 0; i < 64; i++) if ((_config.AffinityMask & (1L << i)) != 0 && _coreToCpuSetIdMap.TryGetValue(i, out uint realId)) ids.Add(realId);
            _cachedTargetCpuSetIds = ids.ToArray();
        }

        private static void FindOrCreatePowerPlan()
        {
            _systemFoundGuid = Guid.Empty;
            uint index = 0;
            Guid scheme = Guid.Empty;
            uint size = (uint)Marshal.SizeOf(typeof(Guid));

            while (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, ref scheme, ref size) == 0)
            {
                uint nameSize = 0;
                PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref nameSize);
                if (nameSize > 0)
                {
                    IntPtr namePtr = Marshal.AllocHGlobal((int)nameSize);
                    if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, namePtr, ref nameSize) == 0)
                    {
                        if (Marshal.PtrToStringUni(namePtr) == POWER_PLAN_NAME) { _systemFoundGuid = scheme; Marshal.FreeHGlobal(namePtr); break; }
                    }
                    Marshal.FreeHGlobal(namePtr);
                }
                index++;
            }

            if (_systemFoundGuid == Guid.Empty)
            {
                IntPtr ptr = IntPtr.Zero;
                if (PowerDuplicateScheme(IntPtr.Zero, ref GUID_HIGH_PERF, out ptr) == 0) // Use High Performance
                {
                    _systemFoundGuid = Marshal.PtrToStructure<Guid>(ptr);
                    byte[] bName = System.Text.Encoding.Unicode.GetBytes(POWER_PLAN_NAME);
                    PowerWriteFriendlyName(IntPtr.Zero, ref _systemFoundGuid, IntPtr.Zero, IntPtr.Zero, bName, (uint)bName.Length);
                    Marshal.FreeHGlobal(ptr);
                }
            }
            
            // Always enforce settings on the found/created GUID
            if (_systemFoundGuid != Guid.Empty) ApplyBitsumSettings(ref _systemFoundGuid);
        }

        private static void ApplyBitsumSettings(ref Guid scheme)
        {
            //Harddisk
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_HARDDISK, ref GUID_OFFHARDDISK, 0);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_HARDDISK, ref GUID_OFFHARDDISK, 0);
            
            //Power Plan Type
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_POWERPLANTYPE, ref GUID_POWERPLANTYPE, 1);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_POWERPLANTYPE, ref GUID_POWERPLANTYPE, 2);
            
            // Display
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_DISPLAY, ref GUID_TURNDISPLAY_OFF, 0);
            
            
            // Processor
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_EPP, 10);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_EPP, 10);
            
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_PARKING_MIN, 100);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_PARKING_MIN, 100);
            
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_PARKING_P_MIN, 100);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_PARKING_P_MIN, 100);
            
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_STATE_MIN, 100);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_STATE_MIN, 100);
            
            PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_STATE_MAX, 100);
            PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref GUID_SUBGROUP_PROCESSOR, ref GUID_PROCESSOR_STATE_MAX, 100);
            
        }

        private static void DeleteCustomPowerPlan()
        {
            ApplyPowerPlan(false);
            if (_systemFoundGuid != Guid.Empty) { PowerDeleteScheme(IntPtr.Zero, ref _systemFoundGuid); _systemFoundGuid = Guid.Empty; }
        }
        
        private static void SetRegistryTweaks(bool enabled)
        {
            try
            {
                // System Responsiveness (MMCSS)
                // 10 = Gaming (Full resources), 20 = Desktop Default
                using (var key = Registry.LocalMachine.OpenSubKey(REG_PATH_PRIORITY, true))
                {
                    key?.SetValue("SystemResponsiveness", enabled ? 10: 20, RegistryValueKind.DWord);
                }

                // Win32 Priority Separation (Quantum)
                // 38 (0x26) = Short, Variable, 3:1 ratio (Best for 9950X3D latency)
                // 2 = Windows Default
                using (var key = Registry.LocalMachine.OpenSubKey(REG_PRIORITY_CONTROL, true))
                {
                    key?.SetValue("Win32PrioritySeparation", enabled ? 38 : 2, RegistryValueKind.DWord);
                }
                
                // 3. MMCSS Games Task Specifics
                using (var key = Registry.LocalMachine.OpenSubKey(REG_PATH_GAMES_TASK, true))
                {
                    if (key != null)
                    {
                        // Set GPU Priority (8 is gaming default, ensures it hasn't been throttled)
                        key.SetValue("GPU Priority", 8, RegistryValueKind.DWord);

                        // Set Thread Priority (6 = Gaming, 2 = Windows Default)
                        key.SetValue("Priority", enabled ? 6 : 2, RegistryValueKind.DWord);
                        
                        //Set gaming affinity mask
                        key.SetValue("Affinity", enabled ? _config.AffinityMask : 0, RegistryValueKind.DWord);

                        // Set Scheduling Category (High vs Medium)
                        key.SetValue("Scheduling Category", enabled ? "High" : "Medium", RegistryValueKind.String);

                        // Set SFIO (Special File I/O) Priority (High vs Normal)
                        key.SetValue("SFIO Priority", enabled ? "High" : "Normal", RegistryValueKind.String);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Registry Error] {ex.Message} (Ensure running as Admin)");
            }
        }

        private static bool IsCustomRegistrySet()
        {
            using (var key = Registry.LocalMachine.OpenSubKey(REG_PATH_PRIORITY, true))
            {
                if (key != null)
                {
                    var val = key.GetValue("SystemResponsiveness");
                    if (val is int intVal)
                    {
                        return intVal != 20;
                    };
                }
            }
            return false;
        }

        private static void ApplyPowerPlan(bool high)
        {
            if (high && _systemFoundGuid == Guid.Empty) FindOrCreatePowerPlan();
            Guid target = high ? _systemFoundGuid : GUID_BALANCED;
            string method = high ? POWER_PLAN_NAME : "BALANCED";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Power plan switched to {method} mode.");
            if (target != Guid.Empty)
            {
                PowerSetActiveScheme(IntPtr.Zero, ref target);
            }
        }
        
        private static void ApplyIfeoRegistry(string exeName)
        {
            try
            {
                // Ensure the EXE has an entry in IFEO
                string rootPath = $@"{IFEO_PATH}\{exeName}.exe";
                string perfPath = $@"{rootPath}\PerfOptions";

                using (var key = Registry.LocalMachine.CreateSubKey(rootPath, true))
                {
                    if(key != null)
                    {
                        using (var perfKey = Registry.LocalMachine.CreateSubKey(perfPath, true))
                        {
                            if (perfKey != null)
                            {
                                perfKey.SetValue("CpuPriorityClass", 3, RegistryValueKind.DWord);
                                // IoPriority: 3 = High
                                perfKey.SetValue("IoPriority", 3, RegistryValueKind.DWord);
                                // PagePriority (Memory): 5 = Highest
                                perfKey.SetValue("PagePriority", 5, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IFEO Error] Could not write to registry for {exeName}: {ex.Message}");
            }
        }
        
        private static void RemoveIfeoRegistry(string exeName)
        {
            try
            {
                string rootPath = $@"{IFEO_PATH}\{exeName}.exe";
                using (var rootKey = Registry.LocalMachine.OpenSubKey(rootPath, true))
                {
                    if (rootKey != null)
                    {
                        rootKey.DeleteSubKey("PerfOptions", false);
                        if (rootKey.SubKeyCount == 0 && rootKey.ValueCount == 0)
                            Registry.LocalMachine.DeleteSubKey(rootPath, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[IFEO Error] Could not remove registry for {exeName}: {ex.Message}");
            }
        }

        private static void MonitorProcesses(object? state)
        {
            if (_cachedTargetCpuSetIds.Length == 0 && _config.AffinityMask != 0) RebuildCpuSetCache();

            Dictionary<string, OptimizeMethod> targets;
            lock (_lock) { targets = new Dictionary<string, OptimizeMethod>(_config.Apps); }

            var cleanup = new List<string>();
            
            foreach (var stateKey in _alreadySetStates)
            {
                var inst = Process.GetProcessesByName(stateKey.Split('|')[0]);
                if (inst.Length == 0) cleanup.Add(stateKey);
                foreach (var i in inst) i.Dispose();
            }
            foreach (var key in cleanup) _alreadySetStates.Remove(key);
            
            bool anyAppRunning = false;
            if (_config.UsePowerOptimization)
            {
                foreach (var appName in targets.Keys)
                {
                    var inst = Process.GetProcessesByName(appName);
                    if (inst.Length > 0) anyAppRunning = true;
                    foreach (var i in inst) i.Dispose();
                    if (anyAppRunning) break; 
                }

                if (anyAppRunning && !_isPowerPlanActive)
                {
                    FindOrCreatePowerPlan();
                    ApplyPowerPlan(true);     // Switch to Custom Plan
                    _isPowerPlanActive = true;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Power/Registry] High Performance Mode Engaged.");
                }
                else if (!anyAppRunning && _isPowerPlanActive)
                {
                    ApplyPowerPlan(false);     // Revert to Balanced
                    _isPowerPlanActive = false;
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [Power/Registry] System Reverted to Balanced.");
                }
            }

            foreach (var target in targets)
            {
                if (_alreadySetStates.Contains($"{target.Key}|{target.Value}")) continue;
                var procs = Process.GetProcessesByName(target.Key);
                bool applied = false;
                foreach (var p in procs)
                {
                    try {
                        if (target.Value == OptimizeMethod.Affinity)
                        {
                            p.ProcessorAffinity = (IntPtr)_config.AffinityMask; 
                            p.PriorityBoostEnabled = true; 
                            p.PriorityClass = ProcessPriorityClass.High; 
                            applied = true;
                        }
                        else
                        {
                            IntPtr h = OpenProcess(0x3000, false, (uint)p.Id);
                            if (h != IntPtr.Zero)
                            {
                                applied = SetProcessDefaultCpuSets(h, _cachedTargetCpuSetIds, (uint)_cachedTargetCpuSetIds.Length); 
                                CloseHandle(h);
                            }
                        }
                    } catch { } finally { p.Dispose(); }
                }

                if (applied)
                {
                    _alreadySetStates.Add($"{target.Key}|{target.Value}");
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Optimized '{target.Key}' via {target.Value}");
                }
            }
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }

        private static void OpenProfileMenu(IntPtr hConsole)
        {
            if (hConsole == IntPtr.Zero)
            {
                AllocConsole(); 
                hConsole = GetConsoleWindow(); 
                var standardOutput = new StreamWriter(Console.OpenStandardOutput()){AutoFlush=true};
                
                if (_config.EnableLogging)
                    Console.SetOut(new MultiTextWriter(standardOutput, LogPath));
                else
                    Console.SetOut(standardOutput);
            }
            ShowWindow(hConsole, 5);
            int cores = Environment.ProcessorCount;

            while (true)
            {
                Console.Clear();
                Console.WriteLine("=== SETTINGS ===");
                Console.WriteLine("COMMANDS:");
                Console.WriteLine($"  [P] Modify Power Plan: {(_config.UsePowerOptimization ? "ON" : "OFF")}");
                Console.WriteLine($"  [A] Toggle Start with Windows: {(IsAutostartEnabled() ? "ENABLED" : "DISABLED")}");
                Console.WriteLine($"  [R] Toggle Registry Edits: {( IsCustomRegistrySet() ? "ENABLED" : "DISABLED")}");
                Console.WriteLine("  [0-15]   - Set specific core range");
                Console.WriteLine("  [Number] - Toggle specific core index");
                Console.WriteLine("  [S/Ent]  - Save and Return to Background");
                Console.WriteLine("---------------------------------------\n"); 
                for (int i = 0; i < cores; i++) { Console.Write($"[{((_config.AffinityMask & (1L << i)) != 0 ? "X" : " ")}] Core {i,-2} "); if ((i + 1) % 4 == 0) Console.WriteLine(); }
                Console.Write("\nInput: ");
                string input = Console.ReadLine()?.Trim().ToUpper() ?? "";
                if (input == "S" || input == "") break;
                else if (input == "A") ToggleAutostart();
                else if (input == "R") SetRegistryTweaks(!IsCustomRegistrySet());
                else if (input == "P") { _config.UsePowerOptimization = !_config.UsePowerOptimization; if (!_config.UsePowerOptimization) DeleteCustomPowerPlan(); }
                else if (input.Contains("-")) _config.AffinityMask = ParseRange(input, cores); 
                else if (int.TryParse(input, out int idx) && idx >= 0 && idx < cores) _config.AffinityMask ^= (1L << idx);
            }
            lock (_lock) _alreadySetStates.Clear();
            RebuildCpuSetCache();
            SaveConfig();
            ShowWindow(hConsole, 0);
        }

        private static long ParseRange(string input, int max)
        {
            long mask = 0;
            var parts = input.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out int s) && int.TryParse(parts[1], out int e))
                for (int i = Math.Max(0, s); i <= Math.Min(e, max - 1); i++) mask |= (1L << i);
            return mask;
        }

        private static bool IsAutostartEnabled()
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Query /TN \"{AppName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode == 0;
            }
            catch { return false; }
        }
        
        // Toggle autostart using Task Scheduler instead of previous method because you cannot start as admin
        private static void ToggleAutostart()
        {
            string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool exists = IsAutostartEnabled();

            try
            {
                if (exists)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = $"/Delete /TN \"{AppName}\" /F",
                        CreateNoWindow = true
                    })?.WaitForExit();
                    Console.WriteLine("[Autostart] Task Scheduler entry removed.");
                }
                else
                {
                    string args = $"/Create /TN \"{AppName}\" /TR \"'{exePath}'\" /SC ONLOGON /RL HIGHEST /F";
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        Arguments = args,
                        CreateNoWindow = true
                    })?.WaitForExit();
                    Console.WriteLine("[Autostart] Task Scheduler entry created (Elevated).");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Autostart Error] {ex.Message}");
            }
            Thread.Sleep(800);
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        
        private static void LoadConfig() { if (File.Exists(ConfigPath)) try { _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), SourceGenerationContext.Default.ConfigData) ?? new(); } catch { } }
        private static void SaveConfig() { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, SourceGenerationContext.Default.ConfigData)); }
    }

    public class ConfigData
    {
        public Dictionary<string, OptimizeMethod> Apps { get; set; } = new(); 
        public long AffinityMask { get; set; } = 0; 
        public bool UsePowerOptimization { get; set; } = false;
        public bool EnableLogging { get; set; } = false;
    }
    [JsonSourceGenerationOptions(WriteIndented = true)] [JsonSerializable(typeof(ConfigData))] internal partial class SourceGenerationContext : JsonSerializerContext { }
}