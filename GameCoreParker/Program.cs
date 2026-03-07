using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace GameOptimizer
{
    public enum OptimizeMethod { Affinity, CpuSet, InverseAffinity, InverseCpuSet }

    class Program
    {
        // --- WinAPI Imports ---
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMin, uint wMax, uint wRm);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
        
        private const uint ENABLE_EXTENDED_FLAGS = 0x0080;
        private const int STD_INPUT_HANDLE = -10;

        
        [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetProcessDefaultCpuSets(IntPtr hProcess, [In] uint[] CpuSetIds, uint CpuSetIdCount);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemCpuSetInformation(IntPtr Information, uint BufferLength, out uint ReturnedLength, IntPtr Process, uint Flags);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetLogicalProcessorInformationEx(uint relationshipType, IntPtr buffer, ref uint returnedLength);

        // --- Power Management Imports ---
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid schemeGuid);
        [DllImport("powrprof.dll")] private static extern uint PowerDuplicateScheme(IntPtr root, ref Guid source, out IntPtr dest);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint AcValueIndex);
        [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr RootPowerKey, ref Guid SchemeGuid, ref Guid SubGroupOfPowerSettingsGuid, ref Guid PowerSettingGuid, uint DcValueIndex);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerWriteFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr set, byte[] buffer, uint bufSize);
        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme, IntPtr sub, IntPtr set, IntPtr buffer, ref uint bufSize);
        [DllImport("powrprof.dll")] private static extern uint PowerDeleteScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr sub, uint flags, uint index, ref Guid buffer, ref uint bufSize);
        
        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(IntPtr szDataSource, IntPtr dwUserData, out IntPtr phQuery);
        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint PdhAddCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);
        [DllImport("pdh.dll", SetLastError = true)] private static extern uint PdhCollectQueryData(IntPtr hQuery);
        [DllImport("pdh.dll", SetLastError = true)] private static extern uint PdhGetFormattedCounterValue(IntPtr hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);
        [DllImport("pdh.dll", SetLastError = true)] private static extern uint PdhCloseQuery(IntPtr hQuery);
        [DllImport("pdh.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);
        
        
        [StructLayout(LayoutKind.Sequential)]
        public struct GROUP_AFFINITY
        {
            public UIntPtr Mask;
            public ushort Group;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public ushort[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CACHE_RELATIONSHIP
        {
            public byte Level;
            public byte Associativity;
            public ushort LineSize;
            public uint CacheSize;
            public byte Type;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
            public byte[] Reserved;
            public GROUP_AFFINITY GroupMask;
        }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX
        {
            public uint Relationship; // RelationCache = 2
            public uint Size;
            // Followed by relationship-specific data (CacheRelationship in our case)
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct PDH_FMT_COUNTERVALUE
        {
            [FieldOffset(0)] public uint CStatus;
            [FieldOffset(8)] public double doubleValue; // We use double for percentages
        }
        
        public class CcdInfo
        {
            public long Mask { get; set; }
            public uint L3Size { get; set; }
            public string Tag { get; set; } = "";
        }
        
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

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeMsg { public IntPtr handle; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public System.Drawing.Point p; }
        
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_CPU_SET_INFORMATION { public uint Size; public uint Type; public uint Id; public ushort Group; public byte LogicalProcessorIndex; public byte CoreIndex; public byte LastLevelCacheIndex; public byte NumaNodeIndex; public byte EfficiencyClass; public byte AllFlags; public uint SchedulingClass; public ulong AllocationTag; }

        private static uint[] _cachedTargetCpuSetIds = Array.Empty<uint>();
        private static uint[] _cachedInverseCpuSetIds = Array.Empty<uint>();
        private static Dictionary<int, uint> _coreToCpuSetIdMap = new();
        private static bool _isPowerPlanActive = false;
        private static bool _backgroundIsolated = false; // Track background app state
        private static Guid _systemFoundGuid = Guid.Empty;
        private static List<CcdInfo> _ccds = new();
        
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_AFFINITY_TAG_ID = 1;
        private const int HOTKEY_CPUSET_TAG_ID = 2;
        private const int HOTKEY_INVERSE_TAG_ID = 4; 
        private const int HOTKEY_PROFILE_ID = 3;
        
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        
        private static IntPtr _pdhQuery;
        private static IntPtr[]? _pdhUsageCounters;
        private static IntPtr[]? _pdhFreqCounters;
        private static float _baseClockMHz = 0;
        private static string _currentInput = "";
        private static bool _needsRedraw = true;

        private static string _currentGame = "";
        private static OptimizeMethod _currentGameOptimizeMethod = OptimizeMethod.Affinity;

        private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt");
        private static readonly string AppName = "GameCoreParker";
        private static ConfigData _config = new();
        private static readonly object _lock = new();
        private static HashSet<string> _alreadySetStates = new();
        
        private static bool _isMenuOpen = false;
        internal static bool IsMenuOpenInternal => _isMenuOpen;
        private static TextWriter _rawConsole = Console.Out; // Captures the direct console stream

        static void Main(string[] args)
        {
            try
            {
                EnsureSingleInstance();
                LoadConfig();
                IntPtr hConsole = GetConsoleWindow();
                _rawConsole = Console.Out; 
                
                bool forceShow = args.Any(arg => arg.Equals("-show", StringComparison.OrdinalIgnoreCase));
                
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                
                if (_config.EnableLogging)
                {
                    Console.SetOut(new MultiTextWriter(_rawConsole, LogPath));
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


                RegisterHotKey(IntPtr.Zero, HOTKEY_AFFINITY_TAG_ID, MOD_ALT| MOD_CONTROL, (uint)'9');         // Ctrl + Alt + 9 (Standard)
                RegisterHotKey(IntPtr.Zero, HOTKEY_CPUSET_TAG_ID, MOD_ALT| MOD_CONTROL, (uint)'0');           // Ctrl + Alt + 0 (Anti-Cheat)
                RegisterHotKey(IntPtr.Zero, HOTKEY_INVERSE_TAG_ID, MOD_ALT | MOD_CONTROL, (uint)'8');         // Ctrl + Alt + 8 (Inverse Mask Mode) (For games that may benefit from being on high frequency cores
                RegisterHotKey(IntPtr.Zero, HOTKEY_PROFILE_ID, MOD_ALT | MOD_CONTROL, (uint)'T');             // Ctrl + Alt + T (Settings)

                RebuildCpuSetCache();
                FindOrCreatePowerPlan();
                
                Console.WriteLine("GameCoreParker active. Use Ctrl+Alt+9 (Affinity Mode) / Ctrl+Alt+0 (CPUSet Mode) to tag apps, or Ctrl+Alt+8 to inverse the game's used mask, and Ctrl+Alt+T for settings.");
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
                            else if (id == HOTKEY_INVERSE_TAG_ID) ToggleTag(OptimizeMethod.InverseAffinity); 
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

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            lock (_lock)
            {
                Console.WriteLine("\nClosing GameCoreParker. Reverting changes if a game is active");

                UnregisterHotKey(IntPtr.Zero, HOTKEY_AFFINITY_TAG_ID);
                UnregisterHotKey(IntPtr.Zero, HOTKEY_CPUSET_TAG_ID);
                UnregisterHotKey(IntPtr.Zero, HOTKEY_INVERSE_TAG_ID);
                UnregisterHotKey(IntPtr.Zero, HOTKEY_PROFILE_ID);
                Console.WriteLine("Unregistered hotkeys.");

                if (_isPowerPlanActive)
                {
                    PowerSetActiveScheme(IntPtr.Zero, ref GUID_BALANCED);
                    Console.WriteLine("Power Plan reverted to Balanced.");
                }


                long fullMask = (1L << Environment.ProcessorCount) - 1;
                if (_backgroundIsolated)
                {
                    ApplyMaskToApps(_config.BackgroundApps, fullMask);
                    Console.WriteLine("Reverted background app isolation.");
                }

                CleanupPdh();

                // 6. Final Memory Flush
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
                Console.WriteLine("Successfully closed GameCoreParker");
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
        
        private static void InitializeMonitorData()
        {
            if (_pdhUsageCounters != null) return;
            if (_ccds.Count == 0) DetectCcdTopology();
            
            using (var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                _baseClockMHz = Convert.ToSingle(key?.GetValue("~MHz") ?? 4300);

            if (PdhOpenQuery(IntPtr.Zero, IntPtr.Zero, out _pdhQuery) != 0)
                throw new Exception("Could not open PDH Query.");

            int coreCount = Environment.ProcessorCount;
            _pdhUsageCounters = new IntPtr[coreCount];

            for (int i = 0; i < coreCount; i++)
            {
                string usagePath = $"\\Processor Information(0,{i})\\% Processor Utility";
                PdhAddEnglishCounter(_pdhQuery, usagePath, IntPtr.Zero, out _pdhUsageCounters[i]);
            }

            // Prime the data twice
            PdhCollectQueryData(_pdhQuery);
            Thread.Sleep(100);
        }
        
        private static void DetectCcdTopology()
        {
            _ccds.Clear();
            uint returnLength = 0;
            GetLogicalProcessorInformationEx(2, IntPtr.Zero, ref returnLength); 

            IntPtr buffer = Marshal.AllocHGlobal((int)returnLength);
            try
            {
                if (GetLogicalProcessorInformationEx(2, buffer, ref returnLength))
                {
                    int offset = 0;
                    while (offset < returnLength)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buffer + offset);
                        var cacheInfo = Marshal.PtrToStructure<CACHE_RELATIONSHIP>(buffer + offset + 8);
                
                        if (cacheInfo.Level == 3)
                        {
                            _ccds.Add(new CcdInfo { 
                                Mask = (long)cacheInfo.GroupMask.Mask, 
                                L3Size = cacheInfo.CacheSize 
                            });
                        }
                        offset += (int)info.Size;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            if (_ccds.Count == 0)
                _ccds.Add(new CcdInfo { Mask = (1L << Environment.ProcessorCount) - 1, L3Size = 0 });
            
            if (_ccds.Count > 1)
            {
                uint maxL3 = _ccds.Max(c => c.L3Size);
                uint minL3 = _ccds.Min(c => c.L3Size);

                foreach (var ccd in _ccds)
                {
                    if (maxL3 != minL3)
                        ccd.Tag = (ccd.L3Size == maxL3) ? "(CACHE)" : "(FREQ)";
                    else
                        ccd.Tag = ""; 
                }
            }
            else if (_ccds.Count == 1 && _ccds[0].L3Size > 32 * 1024 * 1024)
                _ccds[0].Tag = "(CACHE)";
        }
        
        private static int GetPopCount(ulong value)
        {
            int count = 0;
            while (value != 0)
            {
                value &= (value - 1);
                count++;
            }
            return count;
        }
        
        private static void CleanupPdh()
        {
            if (_pdhQuery != IntPtr.Zero)
            {
                PdhCloseQuery(_pdhQuery);
                _pdhQuery = IntPtr.Zero;
            }
        }

        private static int GetCoreIndexInCcd(int ccdIdx, int coreInCcd)
        {
            long mask = _ccds[ccdIdx].Mask; // Changed from _ccdMasks
            int count = 0;
            for (int i = 0; i < 64; i++)
            {
                if (((mask >> i) & 1) == 1)
                {
                    if (count == coreInCcd) return i;
                    count++;
                }
            }
            return -1;
        }

        private static void ToggleTag(OptimizeMethod requestedMethod)
        {
            IntPtr hwnd = GetForegroundWindow();
            GetWindowThreadProcessId(hwnd, out uint pid);

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string name = proc.ProcessName;

                if (string.IsNullOrEmpty(name) || 
                    name.Equals("Idle", StringComparison.OrdinalIgnoreCase) || 
                    name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("GameCoreParker", StringComparison.OrdinalIgnoreCase))
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

            long fullMask = (1L << Environment.ProcessorCount) - 1;
            long invMask = fullMask ^ _config.AffinityMask;

            // Cache Normal
            var ids = new List<uint>();
            for (int i = 0; i < 64; i++) if ((_config.AffinityMask & (1L << i)) != 0 && _coreToCpuSetIdMap.TryGetValue(i, out uint r)) ids.Add(r);
            _cachedTargetCpuSetIds = ids.ToArray();

            // Cache Inverse
            var invIds = new List<uint>();
            for (int i = 0; i < 64; i++) if ((invMask & (1L << i)) != 0 && _coreToCpuSetIdMap.TryGetValue(i, out uint r)) invIds.Add(r);
            _cachedInverseCpuSetIds = invIds.ToArray();
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
                // 38
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
            if ((_cachedTargetCpuSetIds.Length == 0 || _cachedInverseCpuSetIds.Length == 0) && _config.AffinityMask != 0) RebuildCpuSetCache();
            Dictionary<string, OptimizeMethod> targets;
            HashSet<string> backgroundApps;
            lock (_lock) { targets = new Dictionary<string, OptimizeMethod>(_config.Apps); backgroundApps = new HashSet<string>(_config.BackgroundApps); }

            // Cleanup stale
            var cleanup = new List<string>();
            foreach (var sk in _alreadySetStates) {
                var inst = Process.GetProcessesByName(sk.Split('|')[0]);
                if (inst.Length == 0) cleanup.Add(sk);
                foreach (var i in inst) i.Dispose();
            }
            foreach (var k in cleanup) _alreadySetStates.Remove(k);

            bool gameRunning = false;
            bool isInverseGame = false;
            foreach (var app in targets) {
                var inst = Process.GetProcessesByName(app.Key);
                if (inst.Length > 0) {
                    gameRunning = true;
                    _currentGame = app.Key;
                    _currentGameOptimizeMethod = app.Value;
                    if (app.Value == OptimizeMethod.InverseAffinity || app.Value == OptimizeMethod.InverseCpuSet)
                        isInverseGame = true;
                }
                foreach (var i in inst) i.Dispose();
                if (gameRunning) break;
            }

            // 1. BACKGROUND ISOLATION (Other Affinity Mode)
            long fullMask = (1L << Environment.ProcessorCount) - 1;
            long inverseMask = fullMask ^ _config.AffinityMask;
            long bgMask = isInverseGame ? _config.AffinityMask : inverseMask;

            if (gameRunning && !_backgroundIsolated) {
                ApplyMaskToApps(backgroundApps, bgMask);
                _backgroundIsolated = true;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Game active. Background apps isolated to other CCD.");
            } else if (!gameRunning && _backgroundIsolated) {
                ApplyMaskToApps(backgroundApps, fullMask);
                _backgroundIsolated = false;
                _currentGame = "";
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Game closed. Background apps released.");
            }

            // 2. POWER PLAN
            if (_config.UsePowerOptimization) {
                if (gameRunning && !_isPowerPlanActive) { ApplyPowerPlan(true); _isPowerPlanActive = true; }
                else if (!gameRunning && _isPowerPlanActive) { ApplyPowerPlan(false); _isPowerPlanActive = false; }
            }

            // 3. TARGET APPS
            foreach (var target in targets) {
                if (_alreadySetStates.Contains($"{target.Key}|{target.Value}")) continue;
                var procs = Process.GetProcessesByName(target.Key);
                bool applied = false;
                foreach (var p in procs) {
                    try {
                        long currentMask = (target.Value == OptimizeMethod.InverseAffinity || target.Value == OptimizeMethod.InverseCpuSet) ? inverseMask : _config.AffinityMask;
                        uint[] currentCpuSets = (target.Value == OptimizeMethod.InverseAffinity || target.Value == OptimizeMethod.InverseCpuSet) ? _cachedInverseCpuSetIds : _cachedTargetCpuSetIds;

                        if (target.Value == OptimizeMethod.Affinity || target.Value == OptimizeMethod.InverseAffinity) {
                            p.ProcessorAffinity = (IntPtr)currentMask;
                            p.PriorityClass = ProcessPriorityClass.High;
                            applied = true;
                        } else {
                            IntPtr h = OpenProcess(0x3000, false, (uint)p.Id);
                            if (h != IntPtr.Zero) { SetProcessDefaultCpuSets(h, currentCpuSets, (uint)currentCpuSets.Length); CloseHandle(h); applied = true; }
                        }
                    } catch { } finally { p.Dispose(); }
                }
                if (applied) _alreadySetStates.Add($"{target.Key}|{target.Value}");
            }
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
        }
        
        private static void ApplyMaskToApps(HashSet<string> apps, long mask) {
            foreach (var name in apps) {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs) { try { p.ProcessorAffinity = (IntPtr)mask; } catch { } finally { p.Dispose(); } }
            }
        }
        
        private static string ReadInputManually(string prompt)
        {
            _rawConsole.Write(prompt);
            StringBuilder input = new StringBuilder();
            Console.CursorVisible = true; // Show cursor while typing

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);

                    if (key.Key == ConsoleKey.Enter)
                    {
                        _rawConsole.WriteLine();
                        break;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (input.Length > 0)
                        {
                            input.Remove(input.Length - 1, 1);
                            // Standard console backspace sequence: Back, Space, Back
                            _rawConsole.Write("\b \b");
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        input.Append(key.KeyChar);
                        _rawConsole.Write(key.KeyChar.ToString());
                    }
                }
                Thread.Sleep(5); // Ultra-low latency
            }

            Console.CursorVisible = false; // Hide cursor again
            return input.ToString().Trim();
        }
        
        private static void ManageGameList()
        {
            while (true)
            {
                Console.Clear();
                _rawConsole.WriteLine("=== MANAGE GAMES ===");
        
                lock (_lock)
                {
                    foreach (var g in _config.Apps)
                        WriteCurrentGameInfo(g.Key, g.Value);
                }

                Console.ForegroundColor = ConsoleColor.White;
                string joinedNames = string.Join(", ", Enum.GetNames(typeof(OptimizeMethod)));
                _rawConsole.WriteLine($"\nAvailable Methods: {joinedNames}");
                _rawConsole.WriteLine("\n'EXE Name' to remove, or 'EXE Name:Method' to add (e.g. Cemu:CpuSet)");

                string input = ReadInputManually("\nENTER to return or Type Command: ");
        
                if (string.IsNullOrEmpty(input) || input.Equals("ESC", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Clear();
                    break;
                }

                lock (_lock)
                {
                    if (input.Contains(":", StringComparison.OrdinalIgnoreCase))
                    {
                        var p = input.Split(':');
                        if (p.Length == 2 && Enum.TryParse<OptimizeMethod>(p[1], true, out var m))
                        {
                            _config.Apps[p[0]] = m;
                            ApplyIfeoRegistry(p[0]);
                        }
                    }
                    else if (_config.Apps.Remove(input))
                    {
                        RemoveIfeoRegistry(input);
                    }
                }
                SaveConfig();
            }
        }

        private static void WriteCurrentGameInfo(string name, OptimizeMethod method, bool clearLine = false)
        {
            Console.ForegroundColor = method switch
            {
                OptimizeMethod.Affinity => ConsoleColor.Green,
                OptimizeMethod.CpuSet => ConsoleColor.Magenta,
                OptimizeMethod.InverseAffinity => ConsoleColor.DarkBlue,
                OptimizeMethod.InverseCpuSet => ConsoleColor.DarkYellow,
                _ => ConsoleColor.White
            };
            string output = $" - {name} ({method})";
            
            if (clearLine)
                _rawConsole.WriteLine(output.PadRight(Console.WindowWidth));
            else
                _rawConsole.WriteLine(output);

            Console.ResetColor();
        }

        private static void ManageBackgroundList()
        {
            while (true)
            {
                Console.Clear();
                _rawConsole.WriteLine("=== BACKGROUND APPS (Isolation CCD) ===");
        
                lock (_lock)
                {
                    foreach (var a in _config.BackgroundApps) 
                        _rawConsole.WriteLine($" - {a}");
                }

                _rawConsole.WriteLine("\n'EXE Name' to Add/Remove, or ENTER to return to menu:");
                string input = ReadInputManually("> ");
        
                if (string.IsNullOrEmpty(input) || input.Equals("ESC", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Clear();
                    break;
                }

                lock (_lock)
                {
                    if (!_config.BackgroundApps.Add(input)) 
                        _config.BackgroundApps.Remove(input);
                }
                SaveConfig();
            }
        }

        private static void OpenProfileMenu(IntPtr hConsole)
        {
            if (hConsole == IntPtr.Zero)
            {
                AllocConsole();
                hConsole = GetConsoleWindow();
                AdjustConsoleWindowSize();
      
                _rawConsole = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        
                if (_config.EnableLogging)
                    Console.SetOut(new MultiTextWriter(_rawConsole, LogPath));
            }
            ShowWindow(hConsole, 5); // SW_SHOW
            
            IntPtr hInput = GetStdHandle(STD_INPUT_HANDLE);
            uint prevMode;
            GetConsoleMode(hInput, out prevMode);
            if (hInput != IntPtr.Zero && GetConsoleMode(hInput, out prevMode))
            {
                SetConsoleMode(hInput, ENABLE_EXTENDED_FLAGS); 
            }
            
            try { InitializeMonitorData(); }
            catch (Exception ex) { _rawConsole.WriteLine($"Monitor Init Error: {ex.Message}"); Thread.Sleep(2000); }

            int maxRows = _ccds.Max(c => GetPopCount((ulong)c.Mask));
            _currentInput = "";
            Console.CursorVisible = false;
            _isMenuOpen = true; 

            while (true)
            {
                PdhCollectQueryData(_pdhQuery);
                Console.SetCursorPosition(0, 0);
                Console.ForegroundColor = ConsoleColor.White;
                _rawConsole.WriteLine($"=== CPU REAL-TIME MONITOR | CCDs: {_ccds.Count}  ===");

                _rawConsole.WriteLine("Currently Active Game: ");
                if (_currentGame.Length != 0)
                    WriteCurrentGameInfo(_currentGame, _currentGameOptimizeMethod, true);
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    _rawConsole.WriteLine("No game detected. Waiting for tagged game to launch...\n".PadRight(Console.WindowWidth));
                }
                
                
                for (int c = 0; c < _ccds.Count; c++)
                {
                    string label = $"CCD {c}";
                    if (!string.IsNullOrEmpty(_ccds[c].Tag))
                    {
                        label += $" {_ccds[c].Tag}";
                    }
                    _rawConsole.Write($"{label,-35} | ");
                }
                _rawConsole.WriteLine("\n" + new string('-', 38 * _ccds.Count));
                Console.ResetColor();
                
                for (int row = 0; row < maxRows; row++)
                {
                    for (int ccdIdx = 0; ccdIdx < _ccds.Count; ccdIdx++)
                    {
                        int coreIdx = GetCoreIndexInCcd(ccdIdx, row);
                        if (coreIdx != -1)
                        {
                            PdhGetFormattedCounterValue(_pdhUsageCounters![coreIdx], 0x00000200, out _, out var utilVal);
                            double util = utilVal.doubleValue;
                            double mhz = (util / 100.0) * _baseClockMHz;
                            double displayLoad = Math.Clamp(util, 0, 100);
                            
                            ConsoleColor color = ConsoleColor.Green;
                            if (displayLoad > 80) color = ConsoleColor.Red;
                            else if (displayLoad > 40) color = ConsoleColor.Yellow;
                            bool isSet = (_config.AffinityMask & (1L << coreIdx)) != 0;
                            if (isSet) { Console.ForegroundColor = ConsoleColor.Cyan; _rawConsole.Write("[X] "); }
                            else { Console.ForegroundColor = ConsoleColor.DarkGray; _rawConsole.Write("[ ] "); }
                            
                            Console.ForegroundColor = ConsoleColor.Gray;
                            _rawConsole.Write($"C{coreIdx:D2}[");
                            
                            Console.ForegroundColor = color;
                            int filled = (int)Math.Round(displayLoad / 10.0);
                            _rawConsole.Write(new string('|', filled).PadRight(10, '.'));
                            
                            Console.ForegroundColor = ConsoleColor.Gray;
                            _rawConsole.Write($"] ");

                            Console.ForegroundColor = color;
                            _rawConsole.Write($"{displayLoad,5:F1}% ");
                            
                            Console.ForegroundColor = ConsoleColor.White;
                            _rawConsole.Write($"{Math.Clamp(mhz, 0, 9999),4:F0} MHz");
                            
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            _rawConsole.Write(" | ");
                        }
                        else { _rawConsole.Write(new string(' ', 38) + " | "); }
                    }
                    _rawConsole.WriteLine();
                    Console.ResetColor();
                }
                _rawConsole.WriteLine(new string('-', 38 * _ccds.Count));
                
                Console.ForegroundColor = _config.UsePowerOptimization ? ConsoleColor.Green : ConsoleColor.Red;
                _rawConsole.WriteLine($"  [P] Modify Power Plan: {(_config.UsePowerOptimization ? "ON " : "OFF")}");
                Console.ForegroundColor = IsAutostartEnabled() ? ConsoleColor.Green : ConsoleColor.Red;
                _rawConsole.WriteLine($"  [A] Toggle Start with Windows: {(IsAutostartEnabled() ? "ENABLED " : "DISABLED")}");
                Console.ForegroundColor = IsCustomRegistrySet() ? ConsoleColor.Green : ConsoleColor.Red;
                _rawConsole.WriteLine($"  [R] Toggle Registry Edits: {( IsCustomRegistrySet() ? "ENABLED " : "DISABLED")}");
                Console.ForegroundColor = ConsoleColor.White;
                _rawConsole.WriteLine($"  [G] Manage Game List ({_config.Apps.Count} apps)");
                _rawConsole.WriteLine($"  [B] Manage Background App List ({_config.BackgroundApps.Count} apps)");
                _rawConsole.WriteLine("  [0-15]   - Set specific core range");
                _rawConsole.WriteLine("  [Number] - Toggle specific core index");
                _rawConsole.WriteLine("  [S/Ent]  - Save and Return to Background");
                _rawConsole.WriteLine("" + new string('-', 38 * _ccds.Count));
                
                _rawConsole.Write("> Input: " + _currentInput);
                
                DateTime frameEnd = DateTime.Now.AddMilliseconds(500);
                while (DateTime.Now < frameEnd)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Enter)
                        {
                            string cmd = _currentInput.ToUpper().Trim();
                            _currentInput = ""; 
                            
                            if (cmd == "S" || (cmd == "" && _currentInput == "")) goto ExitLoop;
                            
                            if (HandleMenuCommand(cmd)) 
                            {
                                lock (_lock) _alreadySetStates.Clear();
                                RebuildCpuSetCache();
                                SaveConfig();
                            }
                            break; 
                        }
                        else if (key.Key == ConsoleKey.Backspace)
                        {
                            if (_currentInput.Length > 0) _currentInput = _currentInput.Substring(0, _currentInput.Length - 1);

                            Console.SetCursorPosition(0, Console.CursorTop);
                            _rawConsole.Write(("> Input: " + _currentInput).PadRight(Console.WindowWidth - 1));
                        }
                        else if (!char.IsControl(key.KeyChar))
                        {
                            _currentInput += key.KeyChar;
                            Console.SetCursorPosition(0, Console.CursorTop);
                            _rawConsole.Write("> Input: " + _currentInput);
                        }
                    }
                    Thread.Sleep(5); 
                }
            }

            ExitLoop:
            _isMenuOpen = false;
            SaveConfig();
            _currentInput = "";
            Console.CursorVisible = true;
            Console.Clear();
            ShowWindow(hConsole, 0);
            if (hInput != IntPtr.Zero) SetConsoleMode(hInput, prevMode);
        }

        private static bool HandleMenuCommand(string input)
        {
            if (input == "G") { ManageGameList(); return true; }
            if (input == "B") { ManageBackgroundList(); return true; }
            if (input == "A") { ToggleAutostart(); return true; }
            if (input == "R") { SetRegistryTweaks(!IsCustomRegistrySet()); return true; }
            if (input == "P") { 
                _config.UsePowerOptimization = !_config.UsePowerOptimization; 
                if (!_config.UsePowerOptimization) DeleteCustomPowerPlan(); 
                return true; 
            }
            if (input.Contains("-"))
            {
                long newMask = ParseRange(input, Environment.ProcessorCount);
                if(newMask == _config.AffinityMask)
                    _config.AffinityMask = 0;
                else
                    _config.AffinityMask = ParseRange(input, Environment.ProcessorCount);
                return true;
            }

            if (int.TryParse(input, out int idx) && idx >= 0 && idx < Environment.ProcessorCount)
            {
                _config.AffinityMask ^= (1L << idx);
                return true;
            }
            return false;
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
        
        private static void AdjustConsoleWindowSize()
        {
            try
            {
                int targetWidth = 110;
                int targetHeight = 40;

                if (OperatingSystem.IsWindows())
                {
                    targetWidth = Math.Min(targetWidth, Console.LargestWindowWidth);
                    targetHeight = Math.Min(targetHeight, Console.LargestWindowHeight);
                    Console.SetWindowSize(1, 1);
                    Console.SetBufferSize(targetWidth, targetHeight);
                    Console.SetWindowSize(targetWidth, targetHeight);
                    
                    IntPtr hConsole = GetConsoleWindow();
                    ShowWindow(hConsole, SW_HIDE);
                    ShowWindow(hConsole, SW_SHOW);
                }
            }catch {}
        }
        
        private static void LoadConfig() { if (File.Exists(ConfigPath)) try { _config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), SourceGenerationContext.Default.ConfigData) ?? new(); } catch { } }
        private static void SaveConfig() { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, SourceGenerationContext.Default.ConfigData)); }
    }

    public class ConfigData {
        public Dictionary<string, OptimizeMethod> Apps { get; set; } = new(); 
        public HashSet<string> BackgroundApps { get; set; } = new();
        public long AffinityMask { get; set; } = 0; 
        public bool UsePowerOptimization { get; set; } = false;
        public bool EnableLogging { get; set; } = false;
    }
    [JsonSourceGenerationOptions(WriteIndented = true)] [JsonSerializable(typeof(ConfigData))] internal partial class SourceGenerationContext : JsonSerializerContext { }
}