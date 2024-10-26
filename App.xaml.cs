using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace liars_bar
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static readonly Mutex _mutex;
        private static readonly bool _singleton;
        private static readonly object _logLock = new();
        private static readonly StreamWriter _log;
        static App() {
            _mutex = new Mutex(true, "FAE04EC0-301F-11D3-BF4B-00C04F79EFBC", out _singleton);
            _log = File.AppendText("log.txt");
            _log.AutoFlush = true;
        }

        #region Program Entry Point
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            Console.OutputEncoding = System.Text.Encoding.Unicode; // allow russian chars
            try
            {
                if (_singleton)
                {
                    GameData gameData = new GameData();
                    RuntimeHelpers.RunClassConstructor(typeof(Memory).TypeHandle); // invoke static constructor
                    Game.GameData = gameData;
                    var mainWindow = new MainWindow(gameData);
                    mainWindow.Show();
                }
                else
                {
                    throw new Exception("The Application Is Already Running!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString(), "Liar's bar");
            }
        }
        #endregion
        
        #region Methods
        /// <summary>
        /// Public logging method, writes to Debug Trace, and a Log File (if enabled in Config.Json)
        /// </summary>
        public static void Log(string msg)
        {
            Console.WriteLine(msg);
            // lock (_logLock) // Sync access to File IO
            // {
            //     _log.WriteLine($"{DateTime.Now}: {msg}");
            // }
        }
        #endregion
    }

}
