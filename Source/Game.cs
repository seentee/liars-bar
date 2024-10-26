using System.IO;

namespace liars_bar
{
    /// <summary>
    /// Class containing Game instance.
    /// </summary>
    public class Game
    {
        private GameObjectManager _gom;
        private readonly ulong _unityBase;
        private volatile bool _inGame = false;
        private ulong _manager;
        private static GameData _gameData;

        public enum GameStatus
        {
            NotFound,
            Found,
            Menu,
            InGame,
            Error
        }

        #region Getters
        public bool InGame
        {
            get => _inGame;
        }
        #endregion

        #region Setters
        public static GameData GameData
        {
            set => _gameData = value;
        }
        #endregion

        /// <summary>
        /// Game Constructor.
        /// </summary>
        private static readonly object logLock = new();
        private readonly StreamWriter debuglog;
        public Game(ulong unityBase)
        {
            _unityBase = unityBase;
        }

        #region GameLoop
        /// <summary>
        /// Main Game Loop executed by Memory Worker Thread.
        /// It manages the updating of player list and game environment elements like loot, grenades, and exfils.
        /// </summary>
        public void GameLoop()
        {
            try
            {
                if (!this._inGame)
                    throw new GameEnded("Game has ended!");

                Tuple<string, string>[] info = this.GetDiceInfo();
                if (info == null) {
                    info = this.GetDeckInfo();
                }

                if (info != null && _gameData != null) {
                    App.Current.Dispatcher.Invoke(() => {
                        for (int i = 0; i < 5; i++) {
                            if (info[i] == null) {
                                _gameData.CaptionRuns[i].Text = "";
                                _gameData.ValueRuns[i].Text = "";
                                continue;
                            }
                            _gameData.CaptionRuns[i].Text = info[i].Item1;
                            _gameData.ValueRuns[i].Text = info[i].Item2;
                        }
                    });
                }
            }
            catch (DMAShutdown)
            {
                HandleDMAShutdown();
            }
            catch (GameEnded e)
            {
                HandleGameEnded(e);
            }
            catch (Exception ex)
            {
                HandleUnexpectedException(ex);
            }
        }
        #endregion

        #region Methods

        /// <summary>
        /// Handles the scenario when DMA shutdown occurs.
        /// </summary>
        private void HandleDMAShutdown()
        {
            //this._inGame = false;
            Memory.Restart();
        }

        /// <summary>
        /// Handles the scenario when the game ends.
        /// </summary>
        /// <param name="e">The GameEnded exception instance containing details about the game end.</param>
        private void HandleGameEnded(GameEnded e)
        {
            App.Log("Game has ended!");
            Memory.Restart();
        }

        /// <summary>
        /// Handles unexpected exceptions that occur during the game loop.
        /// </summary>
        /// <param name="ex">The exception instance that was thrown.</param>
        private void HandleUnexpectedException(Exception ex)
        {
            App.Log($"CRITICAL ERROR - Game ended due to unhandled exception: {ex}");
            this._inGame = false;
            Memory.Restart();
        }

        /// <summary>
        /// Waits until Game has started before returning to caller.
        /// </summary>
        /// 
        public void WaitForGame()
        {
            while (!this.GetGOM() || !this.GetManager())
            {
                Thread.Sleep(1500);
            }
            Thread.Sleep(1000);
            App.Log("Game has started!!");
            this._inGame = true;
            Thread.Sleep(1500);
        }

        /// <summary>
        /// Gets Game Object Manager structure.
        /// </summary>
        private bool GetGOM()
        {
            try
            {
                var addr = Memory.ReadPtr(_unityBase + Offsets.ModuleBase.GameObjectManager);
                _gom = Memory.ReadValue<GameObjectManager>(addr);
                App.Log($"Found Game Object Manager at 0x{addr.ToString("X")}");
                return true;
            }
            catch (DMAShutdown) { throw; }
            catch (Exception ex)
            {
                App.Log(ex.ToString());
                throw new GameNotRunningException($"ERROR getting Game Object Manager, game may not be running: {ex}");
            }
        }

        /// <summary>
        /// Gets Manager
        /// </summary>
        private bool GetManager()
        {
            var found = false;
            try
            {
                ulong manager;
                ulong activeNodes;
                ulong lastActiveNode;
                try
                {
                    activeNodes = Memory.ReadPtr(_gom.ActiveNodes);
                    lastActiveNode = Memory.ReadPtr(_gom.LastActiveNode);
                    manager = this.GetObjectFromList(activeNodes, lastActiveNode, "Manager");
                }
                catch
                {
                    return found;
                }
                if (manager == 0)
                {
                    App.Log("Unable to find Manager Object, likely not in game.");
                }
                else
                {
                    try
                    {
                        this._manager = Memory.ReadPtrChain(manager, Offsets.UnityComponent.To_Component);
                        App.Log($"Found Manager at 0x{this._manager:X}");
                    }
                    catch
                    {
                        App.Log("Couldnt find Manager pointer");
                        Memory.GameStatus = Game.GameStatus.Menu;
                    }

                    if (this._manager == 0)
                    {
                        App.Log("Manager found but is 0");
                    }
                    else
                    {
                        if (!Memory.ReadValue<bool>(this._manager + Offsets.Manager.GameStarted))
                        {
                            App.Log("Game hasn't started!");
                        }
                        else
                        {
                            App.Log("Game started");
                            found = true;
                            Memory.GameStatus = Game.GameStatus.InGame;
                        }
                    }
                }
            }
            catch (DMAShutdown)
            {
                throw; // Propagate the DMAShutdown exception upwards
            }
            catch (Exception ex)
            {
                App.Log($"ERROR getting Manager: {ex}. Retrying...");
            }

            return found;
        }


        /// <summary>
        /// Helper method to locate Game object.
        /// </summary>
        private ulong GetObjectFromList(ulong activeObjectsPtr, ulong lastObjectPtr, string objectName)
        {
            var activeObject = Memory.ReadValue<BaseObject>(Memory.ReadPtr(activeObjectsPtr));
            var lastObject = Memory.ReadValue<BaseObject>(Memory.ReadPtr(lastObjectPtr));

            if (activeObject.obj != 0x0 && lastObject.obj == 0x0)
            {
                App.Log("Waiting for lastObject to be populated...");
                while (lastObject.obj == 0x0)
                {
                    lastObject = Memory.ReadValue<BaseObject>(Memory.ReadPtr(lastObjectPtr));
                    Thread.Sleep(1000);
                }
            }

            while (activeObject.obj != 0x0 && activeObject.obj != lastObject.obj)
            {
                ulong objectNamePtr = Memory.ReadPtr(activeObject.obj + Offsets.GameObject.ObjectName);
                string objectNameStr = Memory.ReadString(objectNamePtr, 64);

                if (string.Equals(objectNameStr, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    App.Log($"Found object {objectNameStr}");
                    return activeObject.obj;
                }

                activeObject = Memory.ReadValue<BaseObject>(activeObject.nextObjectLink);
            }

            if (lastObject.obj != 0x0)
            {
                ulong objectNamePtr = Memory.ReadPtr(lastObject.obj + Offsets.GameObject.ObjectName);
                string objectNameStr = Memory.ReadString(objectNamePtr, 64);

                if (string.Equals(objectNameStr, objectName, StringComparison.OrdinalIgnoreCase))
                {
                    App.Log($"Found object {objectNameStr}");
                    return lastObject.obj;
                }
            }

            App.Log($"Couldn't find object {objectName}");
            return 0;
        }

        public ulong GetComponentFromGameObject(ulong gameObject, string componentName)
        {
            try
            {
                int count = Math.Min(Memory.ReadValue<int>(gameObject + 0x40), 500);
                var scatterReadMap = new ScatterReadMap(count);
                var round1 = scatterReadMap.AddRound();
                var round2 = scatterReadMap.AddRound();
                var round3 = scatterReadMap.AddRound();
                var round4 = scatterReadMap.AddRound();
                var round5 = scatterReadMap.AddRound();

                var component = Memory.ReadPtr(gameObject + Offsets.GameObject.ObjectClass);

                for (int i = 1; i < count; i++)
                {
                    var componentPtr = round1.AddEntry<ulong>(i, 0, component, null, (uint) (0x8 + i * 0x10));
                    var fieldsPtr = round2.AddEntry<ulong>(i, 1, componentPtr, null, 0x28);
                    var classNamePtr1 = round3.AddEntry<ulong>(i, 2, fieldsPtr, null, Offsets.UnityClass.Name[0]);
                    var classNamePtr2 = round4.AddEntry<ulong>(i, 3, classNamePtr1, null, Offsets.UnityClass.Name[1]);
                    var classNamePtr3 = round5.AddEntry<ulong>(i, 4, classNamePtr2, null, Offsets.UnityClass.Name[2]);
                }

                scatterReadMap.Execute();

                for (int i = 1; i < count; i++)
                {
                    if (!scatterReadMap.Results[i][0].TryGetResult<ulong>(out var componentPtr))
                        continue;
                    if (!scatterReadMap.Results[i][1].TryGetResult<ulong>(out var fieldsPtr))
                        continue;
                    if (!scatterReadMap.Results[i][4].TryGetResult<ulong>(out var classNamePtr))
                        continue;

                    var className = Memory.ReadString(classNamePtr, 64).Replace("\0", string.Empty);

                    if (string.IsNullOrEmpty(className))
                        continue;

                    if (className.StartsWith(componentName, StringComparison.OrdinalIgnoreCase)) {
                        // App.Log(className);
                        return fieldsPtr;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log($"(GetComponentFromGameObject) {ex.Message}\n{ex.StackTrace}");
            }


            return 0;
        }
        #endregion

        #region Dice
        private Tuple<string, string>[] GetDiceInfo() {
            if (Memory.ReadValue<int>(this._manager + Offsets.Manager.GameMode) != 1) {
                return null;
            }
            ulong playerListPtr = Memory.ReadValue<ulong>(this._manager + Offsets.Manager.Players);
            int playerCount = Memory.ReadValue<int>(playerListPtr + Offsets.UnityList.Count);
            ulong playerListBase = Memory.ReadValue<ulong>(playerListPtr + Offsets.UnityList.Base);
            int[] diceResults = [0, 0, 0, 0, 0, 0];
            Tuple<string, string>[] result = new Tuple<string, string>[5];
            for (int i = 0; i < playerCount; i++) {
                ulong player = Memory.ReadPtr(playerListBase + Offsets.UnityListBase.Start + (uint) i * 0x8);
                ulong playerNamePtr = Memory.ReadPtr(player + Offsets.PlayerStats.PlayerName);
                var playerUnityName = Memory.ReadUnityString(playerNamePtr);
                
                if (Memory.ReadValue<bool>(player + Offsets.PlayerStats.Dead)) {
                    continue;
                }

                // App.Log($"{playerUnityName}");

                ulong playerGameObject = Memory.ReadPtrChain(player, Offsets.UnityComponent.To_GameObject);
                ulong diceGamePlay = GetComponentFromGameObject(playerGameObject, "DiceGamePlay");
                ulong diceList = Memory.ReadPtr(Memory.ReadPtr(diceGamePlay + Offsets.DiceGamePlay.DiceValues) + Offsets.MirrorSyncList.ToList);
                ulong diceValuesPtr = Memory.ReadPtr(diceList + Offsets.UnityList.Base);
                int diceCount = Memory.ReadValue<int>(diceList + Offsets.UnityList.Count);
                string res = "";
                for (uint j = 0; j < diceCount; j++) {
                    var v = Memory.ReadValue<int>(diceValuesPtr + Offsets.UnityListBase.Start + j * 0x4);
                    res += v + " ";
                    diceResults[v - 1]++;
                }

                result[i] = new Tuple<string, string>(playerUnityName, res);
                // App.Log(res + "\n");
            }
            
            ulong diceGamePlayManager = Memory.ReadPtr(this._manager + Offsets.Manager.DiceGamePlayManager);
            if (Memory.ReadValue<int>(diceGamePlayManager + Offsets.DiceGamePlayManager.DiceMode) == 1) {
                for (int i = 1; i < 6; i++) {
                    diceResults[i] += diceResults[0];
                }
            }

            result[4] = new Tuple<string, string>("Summary\n1 2 3 4 5 6", $"{diceResults[0]} {diceResults[1]} {diceResults[2]} {diceResults[3]} {diceResults[4]} {diceResults[5]}");
            // App.Log("1 2 3 4 5 6");
            // App.Log($"{diceResults[0]} {diceResults[1]} {diceResults[2]} {diceResults[3]} {diceResults[4]} {diceResults[5]}");

            return result;
        }
        #endregion

        #region Deck
        private Tuple<string, string>[] GetDeckInfo() {
            if (Memory.ReadValue<int>(this._manager + Offsets.Manager.GameMode) != 0) {
                return null;
            }
            ulong playerListPtr = Memory.ReadValue<ulong>(this._manager + Offsets.Manager.Players);
            int playerCount = Memory.ReadValue<int>(playerListPtr + Offsets.UnityList.Count);
            ulong playerListBase = Memory.ReadValue<ulong>(playerListPtr + Offsets.UnityList.Base);
            Tuple<string, string>[] result = new Tuple<string, string>[5];
            for (int i = 0; i < playerCount; i++) {
                ulong player = Memory.ReadPtr(playerListBase + Offsets.UnityListBase.Start + (uint) i * 0x8);
                ulong playerNamePtr = Memory.ReadPtr(player + Offsets.PlayerStats.PlayerName);
                var playerUnityName = Memory.ReadUnityString(playerNamePtr);
                
                if (Memory.ReadValue<bool>(player + Offsets.PlayerStats.Dead)) {
                    continue;
                }

                // App.Log($"{playerUnityName}");

                ulong playerGameObject = Memory.ReadPtrChain(player, Offsets.UnityComponent.To_GameObject);
                ulong blorfGamePlay = GetComponentFromGameObject(playerGameObject, "BlorfGamePlay");
                ulong blorfCardList = Memory.ReadPtr(blorfGamePlay + Offsets.BlorfGamePlay.CardTypes);
                ulong blorfValuesPtr = Memory.ReadPtr(blorfCardList + Offsets.UnityList.Base);
                int blorfCount = Memory.ReadValue<int>(blorfCardList + Offsets.UnityList.Count);
                string res = "";
                for (uint j = 0; j < blorfCount; j++) {
                    var v = Memory.ReadValue<int>(blorfValuesPtr + Offsets.UnityListBase.Start + j * 0x4);
                    res += Offsets.BlorfGamePlay.CardMarking[v + 1] + " ";
                }
                int currentRevolver = Memory.ReadValue<int>(blorfGamePlay + Offsets.BlorfGamePlay.CurrentRevolver);
                int revolverBullet = Memory.ReadValue<int>(blorfGamePlay + Offsets.BlorfGamePlay.RevolverBullet) + 1;

                result[i] = new Tuple<string, string>($"{playerUnityName} - {currentRevolver}/{revolverBullet}", res);
                // App.Log($"{res}\n{currentRevolver}/{revolverBullet}\n");
            }

            // App.Log("Last Round:");
            ulong blorfGamePlayManager = Memory.ReadPtr(this._manager + Offsets.Manager.BlorfGamePlayManager);
            ulong lastRoundList = Memory.ReadPtr(Memory.ReadPtr(blorfGamePlayManager + Offsets.BlorfGamePlayManager.LastRound) + Offsets.MirrorSyncList.ToList);
            ulong lastRoundValuesPtr = Memory.ReadPtr(lastRoundList + Offsets.UnityList.Base);
            int lastRoundCount = Memory.ReadValue<int>(lastRoundList + Offsets.UnityList.Count);
            string lastRoundRes = "";
            for (uint j = 0; j < lastRoundCount; j++) {
                var v = Memory.ReadValue<int>(lastRoundValuesPtr + Offsets.UnityListBase.Start + j * 0x4);
                lastRoundRes += Offsets.BlorfGamePlay.CardMarking[v + 1] + " ";
            }
            result[4] = new Tuple<string, string>("Last Round", lastRoundRes);
            // App.Log(lastRoundRes);

            return result;
        }
        #endregion
    }

    #region Exceptions
    public class GameNotRunningException : Exception
    {
        public GameNotRunningException()
        {
        }

        public GameNotRunningException(string message)
            : base(message)
        {
        }

        public GameNotRunningException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    public class GameEnded : Exception
    {
        public GameEnded()
        {

        }

        public GameEnded(string message)
            : base(message)
        {
        }

        public GameEnded(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
    #endregion
}
