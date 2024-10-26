using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vmmsharp;

namespace liars_bar
{
    internal static class Memory
    {
        private static Vmm vmmInstance;
        private static volatile bool _running = false;
        private static volatile bool _restart = false;
        private static volatile bool _ready = false;
        private static Thread _workerThread;
        private static CancellationTokenSource _workerCancellationTokenSource;
        private static VmmProcess _process;
        private static ulong _unityBase;
        private static int _ticksCounter = 0;
        private static volatile int _ticks = 0;
        private static readonly Stopwatch _tickSw = new();
        private static Game _game;

        public static Game.GameStatus GameStatus = Game.GameStatus.NotFound;

        #region Getters
        public static int Ticks
        {
            get => _ticks;
        }
        public static bool InGame
        {
            get => _game?.InGame ?? false;
        }
        #endregion

        #region Startup
        /// <summary>
        /// Constructor
        /// </summary>
        static Memory()
        {
            try
            {
                App.Log("Loading memory module...");

                if (!File.Exists("mmap.txt"))
                {
                    App.Log("No MemMap, attempting to generate...");
                    Memory.GenerateMMap();
                }
                else
                {
                    App.Log("MemMap found, loading...");
                    vmmInstance = new Vmm("-printf", "-device", "fpga://algo=0", "-memmap", "mmap.txt");
                }

                Memory.InitiateMemoryWorker();
            }
            catch (Exception ex)
            {
                try
                {
                    App.Log("attempting to regenerate mmap...");

                    if (File.Exists("mmap.txt"))
                        File.Delete("mmap.txt");

                    Memory.GenerateMMap();
                    Memory.InitiateMemoryWorker();
                }
                catch
                {
                    App.Log(ex.ToString() + " DMA Init");
                    Environment.Exit(-1);
                }
            }
        }

        private static void InitiateMemoryWorker()
        {
            App.Log("Starting Memory worker thread...");
            Memory.StartMemoryWorker();
            Memory._tickSw.Start();
        }

        private static void GenerateMMap()
        {
            vmmInstance = new Vmm("-printf", "-device", "fpga://algo=0", "-waitinitialize");
            Memory.GetMemMap();
        }

        /// <summary>
        /// Generates a Physical Memory Map (mmap.txt) to enhance performance/safety.
        /// </summary>
        private static void GetMemMap()
        {
            try
            {
                var map = vmmInstance.MapMemory();
                if (map.Length == 0) throw new Exception("Map_GetPhysMem() returned no entries!");
                var sb = new StringBuilder();
                for (int i = 0; i < map.Length; i++)
                {
                    sb.AppendLine($"{i.ToString("D4")}  {map[i].pa.ToString("x")}  -  {(map[i].pa + map[i].cb - 1).ToString("x")}  ->  {map[i].pa.ToString("x")}");
                }
                File.WriteAllText("mmap.txt", sb.ToString());
            }
            catch (Exception ex)
            {
                throw new DMAException("Unable to generate MemMap!", ex);
            }
        }

        /// <summary>
        /// Gets Liar's Bar Process ID.
        /// </summary>
        private static bool GetPid()
        {
            try
            {
                ThrowIfDMAShutdown();
                _process = vmmInstance.Process("Liar's Bar.exe");

                if (_process is null)
                    throw new DMAException("Unable to obtain PID. Game is not running.");
                else
                {
                    App.Log($"Liar's Bar.exe is running at PID {_process.PID}");
                    return true;
                }
            }
            catch (DMAShutdown) { throw; }
            catch (Exception ex)
            {
                App.Log($"ERROR getting PID: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Gets module base entry address for UnityPlayer.dll
        /// </summary>
        private static bool GetModuleBase()
        {
            try
            {
                ThrowIfDMAShutdown();

                _unityBase = _process.GetModuleBase("UnityPlayer.dll");

                if (_unityBase == 0)
                    throw new DMAException("Unable to obtain Base Module Address. Game may not be running");
                else
                {
                    App.Log($"Found UnityPlayer.dll at 0x{_unityBase.ToString("x")}");
                    return true;
                }
            }
            catch (DMAShutdown) { throw; }
            catch (Exception ex)
            {
                App.Log($"ERROR getting module base: {ex}");
                return false;
            }
        }
        #endregion

        #region MemoryThread
        private static void StartMemoryWorker()
        {
            if (Memory._workerThread is not null && Memory._workerThread.IsAlive)
                return;

            Memory._workerCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = Memory._workerCancellationTokenSource.Token;

            Memory._workerThread = new Thread(() => Memory.MemoryWorkerThread(cancellationToken))
            {
                Priority = ThreadPriority.BelowNormal,
                IsBackground = true
            };
            Memory._running = true;
            Memory._workerThread.Start();
        }

        public static async void StopMemoryWorker()
        {
            await Task.Run(() =>
            {
                if (Memory._workerCancellationTokenSource is not null)
                {
                    Memory._workerCancellationTokenSource.Cancel();
                    Memory._workerCancellationTokenSource.Dispose();
                    Memory._workerCancellationTokenSource = null;
                }

                if (Memory._workerThread is not null)
                {
                    Memory._workerThread.Join();
                    Memory._workerThread = null;
                }
            });
        }

        private static void MemoryWorkerThread(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    Memory.MemoryWorker();
                }
                catch { }

            }
            App.Log("[Memory] Refresh thread stopped.");
        }

        /// <summary>
        /// Main worker to perform DMA Reads on.
        /// </summary>
        private static void MemoryWorker()
        {
            try
            {
                while (true)
                {
                    App.Log("Attempting to find Liar's Bar Process...");

                    while (!Memory.GetPid() || !Memory.GetModuleBase())
                    {
                        App.Log("Liar's Bar startup failed, trying again in 15 seconds...");
                        Memory.GameStatus = Game.GameStatus.NotFound;
                        Thread.Sleep(15000);
                    }
                    App.Log("Liar's Bar process located! Startup successful.");
                    while (true)
                    {
                        Memory._game = new Game(Memory._unityBase);
                        try
                        {
                            App.Log("Ready -- Waiting for game...");
                            Memory.GameStatus = Game.GameStatus.Menu;
                            Memory._ready = true;
                            Memory._game.WaitForGame();
                            while (Memory.GameStatus == Game.GameStatus.InGame)
                            {
                                if (Memory._tickSw.ElapsedMilliseconds >= 1000)
                                {
                                    Memory._ticks = _ticksCounter;
                                    Memory._ticksCounter = 0;
                                    Memory._tickSw.Restart();
                                }
                                else
                                {
                                    Memory._ticksCounter++;
                                }

                                if (Memory._restart)
                                {
                                    Memory.GameStatus = Game.GameStatus.Menu;
                                    App.Log("Restarting game... getting fresh GameWorld instance");
                                    Memory._restart = false;
                                    break;
                                }

                                Memory._game.GameLoop();
                                Thread.Sleep(1000);
                            }
                        }
                        catch (GameNotRunningException) { break; }
                        catch (ThreadInterruptedException) { throw; }
                        catch (DMAShutdown) { throw; }
                        catch (Exception ex)
                        {
                            App.Log($"CRITICAL ERROR in Game Loop: {ex}");
                        }
                        finally
                        {
                            Memory._ready = false;
                            Thread.Sleep(100);
                        }
                    }
                    App.Log("Game is no longer running! Attempting to restart...");
                }
            }
            catch (ThreadInterruptedException) { }
            catch (DMAShutdown) { }
            catch (Exception ex)
            {
                Environment.FailFast($"FATAL ERROR on Memory Thread: {ex}");
            }
            finally
            {
                App.Log("Uninitializing DMA Device...");
                Memory.vmmInstance.Dispose();
                App.Log("Memory Thread closing down gracefully...");
            }
        }
        #endregion

        #region ScatterRead
        /// <summary>
        /// (Base)
        /// Performs multiple reads in one sequence, significantly faster than single reads.
        /// Designed to run without throwing unhandled exceptions, which will ensure the maximum amount of
        /// reads are completed OK even if a couple fail.
        /// </summary>
        /// <param name="pid">Process ID to read from.</param>
        /// <param name="entries">Scatter Read Entries to read from for this round.</param>
        /// <param name="useCache">Use caching for this read (recommended).</param>
        internal static void ReadScatter(ReadOnlySpan<IScatterEntry> entries)
        {
            var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE);
            if (scatter == null)
            {
                throw new DMAException("Failed to initialize scatter handle");
            }

            try
            {
                foreach (var entry in entries)
                {
                    if (entry is null)
                        continue;

                    ulong addr = entry.ParseAddr();
                    uint size = (uint)entry.ParseSize();

                    if (addr == 0x0 || size == 0)
                    {
                        entry.IsFailed = true;
                        continue;
                    }

                    ulong readAddress = addr + entry.Offset;
                    scatter.Prepare(readAddress, size);
                }

                scatter.Execute();

                foreach (var entry in entries)
                {
                    if (entry is null || entry.IsFailed)
                        continue;

                    ulong readAddress = (ulong)entry.Addr + entry.Offset;
                    uint size = (uint)(int)entry.Size;

                    byte[] buffer = scatter.Read(readAddress, size);

                    if (buffer == null || buffer.Length != size)
                    {
                        entry.IsFailed = true;
                    }
                    else
                    {
                        entry.SetResult(buffer);
                    }
                }
            }
            finally
            {
                scatter.Close();
            }
        }
        #endregion

        #region ReadMethods
        /// <summary>
        /// Read memory into a Span.
        /// </summary>
        public static Span<byte> ReadBuffer(ulong addr, int size)
        {
            if ((uint)size > PAGE_SIZE * 1500) throw new DMAException("Buffer length outside expected bounds!");
            ThrowIfDMAShutdown();
            var buf = _process.MemRead(addr, (uint)size, Vmm.FLAG_NOCACHE);
            if (buf.Length != size) throw new DMAException("Incomplete memory read!");
            return new Span<byte>(buf);
        }

        /// <summary>
        /// Read a chain of pointers and get the final result.
        /// </summary>
        public static ulong ReadPtrChain(ulong ptr, uint[] offsets)
        {
            ulong addr = 0;
            try { addr = ReadPtr(ptr + offsets[0]); }
            catch (Exception ex) { throw new DMAException($"ERROR reading pointer chain at index 0, addr 0x{ptr:X} + 0x{offsets[0]:X}", ex); }
            for (int i = 1; i < offsets.Length; i++)
            {
                try { addr = ReadPtr(addr + offsets[i]); }
                catch (Exception ex) { throw new DMAException($"ERROR reading pointer chain at index {i}, addr 0x{addr:X} + 0x{offsets[i]:X}", ex); }
            }
            return addr;
        }

        /// <summary>
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        public static ulong ReadPtr(ulong ptr)
        {
            var addr = ReadValue<ulong>(ptr);
            if (addr == 0x0) throw new NullPtrException();
            else return addr;
        }

        /// <summary>
        /// Resolves a pointer and returns the memory address it points to.
        /// </summary>
        public static ulong ReadPtrNullable(ulong ptr)
        {
            return ReadValue<ulong>(ptr);
        }

        /// <summary>
        /// Read value type/struct from specified address.
        /// </summary>
        /// <typeparam name="T">Specified Value Type.</typeparam>
        /// <param name="addr">Address to read from.</param>
        public static T ReadValue<T>(ulong addr) where T : struct
        {
            try
            {
                int size = Marshal.SizeOf(typeof(T));
                ThrowIfDMAShutdown();
                var buf = _process.MemRead(addr, (uint)size, Vmm.FLAG_NOCACHE);
                return MemoryMarshal.Read<T>(buf);
            }
            catch (Exception ex)
            {
                throw new DMAException($"ERROR reading {typeof(T)} value at 0x{addr:X}", ex);
            }
        }

        /// <summary>
        /// Read null terminated string.
        /// </summary>
        /// <param name="length">Number of bytes to read.</param>
        /// <exception cref="DMAException"></exception>
        public static string ReadString(ulong addr, uint length = 256)
        {
            try
            {
                if (length > PAGE_SIZE)
                    throw new DMAException("String length outside expected bounds!");

                ThrowIfDMAShutdown();
                var buf = _process.MemRead(addr, length, Vmm.FLAG_NOCACHE);
                int nullTerminator = Array.IndexOf<byte>(buf, 0);

                return nullTerminator != -1
                    ? Encoding.Default.GetString(buf, 0, nullTerminator)
                    : Encoding.Default.GetString(buf);
            }
            catch (Exception ex)
            {
                throw new DMAException($"ERROR reading string at 0x{addr:X}", ex);
            }
        }

        /// <summary>
        /// Read UnityEngineString structure
        /// </summary>
        public static string ReadUnityString(ulong addr)
        {
            try
            {
                var length = (uint)ReadValue<int>(addr + Offsets.UnityString.Length);

                if (length > PAGE_SIZE)
                    throw new DMAException("String length outside expected bounds!");

                ThrowIfDMAShutdown();
                var buf = _process.MemRead(addr + Offsets.UnityString.Value, length * 2, Vmm.FLAG_NOCACHE);
                return Encoding.Unicode.GetString(buf).TrimEnd('\0'); ;
            }
            catch (Exception ex)
            {
                throw new DMAException($"ERROR reading UnityString at 0x{addr.ToString("X")}", ex);
            }
        }
        #endregion

        #region WriteMethods
        /// <summary>
        /// (Base)
        /// Write value type/struct to specified address.
        /// </summary>
        /// <typeparam name="T">Value Type to write.</typeparam>
        /// <param name="pid">Process ID to write to.</param>
        /// <param name="addr">Virtual Address to write to.</param>
        /// <param name="value"></param>
        /// <exception cref="DMAException"></exception>
        public static void WriteValue<T>(ulong addr, T value)
            where T : unmanaged
        {
            // try
            // {
            //     if (!_process.MemWriteStruct(addr, value))
            //         throw new Exception("Memory Write Failed!");
            // }
            // catch (Exception ex)
            // {
            //     throw new DMAException($"[DMA] ERROR writing {typeof(T)} value at 0x{addr.ToString("X")}", ex);
            // }
        }

        /// <summary>
        /// Performs multiple memory write operations in a single call
        /// </summary>
        /// <param name="entries">A collection of entries defining the memory writes.</param>
        public static void WriteScatter(IEnumerable<IScatterWriteEntry> entries)
        {
            // using (var scatter = _process.Scatter_Initialize(Vmm.FLAG_NOCACHE))
            // {
            //     if (scatter == null)
            //         throw new InvalidOperationException("Failed to initialize scatter.");

            //     foreach (var entry in entries)
            //     {
            //         bool success = entry switch
            //         {
            //             IScatterWriteDataEntry<int> intEntry => scatter.PrepareWriteStruct(intEntry.Address, intEntry.Data),
            //             IScatterWriteDataEntry<float> floatEntry => scatter.PrepareWriteStruct(floatEntry.Address, floatEntry.Data),
            //             IScatterWriteDataEntry<ulong> ulongEntry => scatter.PrepareWriteStruct(ulongEntry.Address, ulongEntry.Data),
            //             IScatterWriteDataEntry<bool> boolEntry => scatter.PrepareWriteStruct(boolEntry.Address, boolEntry.Data),
            //             IScatterWriteDataEntry<byte> byteEntry => scatter.PrepareWriteStruct(byteEntry.Address, byteEntry.Data),
            //             _ => throw new NotSupportedException($"Unsupported data type: {entry.GetType()}")
            //         };

            //         if (!success)
            //         {
            //             App.Log($"Failed to prepare scatter write for address: {entry.Address}");
            //             continue;
            //         }
            //     }

            //     if (!scatter.Execute())
            //         throw new Exception("Scatter write execution failed.");

            //     scatter.Close();
            // }
        }
        #endregion

        #region Methods
        /// <summary>
        /// Sets restart flag to re-initialize the game/pointers from the bottom up.
        /// </summary>
        public static void Restart()
        {
            _restart = true;
        }
        /// <summary>
        /// Close down DMA Device Connection.
        /// </summary>
        public static void Shutdown()
        {
            if (_running)
            {
                App.Log("Closing down Memory Thread...");
                _running = false;
                Memory.StopMemoryWorker();
            }
        }

        private static void ThrowIfDMAShutdown()
        {
            if (!_running) throw new DMAShutdown("Memory Thread/DMA is shutting down!");
        }


        /// Mem Align Functions Ported from Win32 (C Macros)
        private const ulong PAGE_SIZE = 0x1000;
        private const int PAGE_SHIFT = 12;

        /// <summary>
        /// The PAGE_ALIGN macro takes a virtual address and returns a page-aligned
        /// virtual address for that page.
        /// </summary>
        private static ulong PAGE_ALIGN(ulong va)
        {
            return (va & ~(PAGE_SIZE - 1));
        }
        /// <summary>
        /// The ADDRESS_AND_SIZE_TO_SPAN_PAGES macro takes a virtual address and size and returns the number of pages spanned by the size.
        /// </summary>
        private static uint ADDRESS_AND_SIZE_TO_SPAN_PAGES(ulong va, uint size)
        {
            return (uint)((BYTE_OFFSET(va) + (size) + (PAGE_SIZE - 1)) >> PAGE_SHIFT);
        }

        /// <summary>
        /// The BYTE_OFFSET macro takes a virtual address and returns the byte offset
        /// of that address within the page.
        /// </summary>
        private static uint BYTE_OFFSET(ulong va)
        {
            return (uint)(va & (PAGE_SIZE - 1));
        }
        #endregion
    }

    #region Exceptions
    public class DMAException : Exception
    {
        public DMAException()
        {
        }

        public DMAException(string message)
            : base(message)
        {
        }

        public DMAException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public class NullPtrException : Exception
    {
        public NullPtrException()
        {
        }

        public NullPtrException(string message)
            : base(message)
        {
        }

        public NullPtrException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public class DMAShutdown : Exception
    {
        public DMAShutdown()
        {
        }

        public DMAShutdown(string message)
            : base(message)
        {
        }

        public DMAShutdown(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
    #endregion
}