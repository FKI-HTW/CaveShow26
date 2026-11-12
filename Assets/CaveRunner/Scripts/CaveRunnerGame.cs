using System;
using System.Collections.Generic;
using UnityEngine;

namespace CaveRunner
{
    public enum GameState { Start, Countdown, Running, Won, Lost }

    /// <summary>
    /// DEMO-HAUPTSKRIPT. Einfach auf ein leeres GameObject ziehen und Play drücken –
    /// es baut Tunnel, Figur, Wort-Tafeln und Anzeige komplett selbst.
    ///
    /// Ablauf (Handprototyp): Start -> 3,2,1 -> 90 Sekunden laufen -> Gewonnen/Verloren.
    /// Gewonnen: 90 s geschafft und mindestens 8 von 10 Wörtern richtig.
    /// Verloren: beim 3. Wortfehler.
    ///
    /// SCHNITTSTELLE FÜR WORTERKENNUNG (Person 3):
    ///   game.WordAppeared += wort => { ... Mikrofon/Erkennung starten ... };
    ///   game.SubmitRecognizedWord("small");   // erkanntes Wort übergeben
    ///   game.CurrentWord                       // Wort, das gerade gelesen werden soll
    /// Zum Testen ohne Mikrofon: Enter = Wort richtig, Backspace = Wort falsch.
    /// </summary>
    public class CaveRunnerGame : MonoBehaviour
    {
        [Header("Runde")]
        public float roundDuration = 90f;
        public float countdownDuration = 3f;
        public int wordsToWin = 8;
        public int maxWordErrors = 3;

        [Header("Wörter")]
        public string[] words = { "SMALL", "HOUSE", "GREEN", "WATER", "APPLE", "TIGER", "CLOUD", "LIGHT", "STONE", "RIVER" };
        public bool shuffleWords = true;
        [Tooltip("Wo die Tafel erscheint (Meter vor der Person).")]
        public float wordSpawnZ = 60f;
        [Tooltip("Lesegrenze: bis hier muss das Wort gelesen sein.")]
        public float readLimitZ = 12f;
        [Tooltip("Tafeln sind langsamer als die Hindernisse.")]
        public float wordSpeed = 8f;
        public float wordHeight = 3.1f;
        [Tooltip("Mögliche Pausen zwischen zwei Wörtern (s). Werden gekürzt, falls sonst nicht alle Wörter in die Runde passen.")]
        public float[] pauseOptions = { 0.5f, 0.5f, 3f, 10f, 15f, 20f };

        [Header("Test ohne Tracking / Mikrofon")]
        public bool keyboardWordInput = true;

        [Header("Kamera")]
        [Tooltip("Wenn kein CAVE-Prefab in der Szene ist, wird eine normale Kamera zum Testen am PC benutzt.")]
        public bool setupDesktopCamera = true;
        public float playerZ = 4f;

        // ---------- Events für die anderen Teile ----------
        public event Action<GameState> StateChanged;
        public event Action<string> WordAppeared;
        public event Action<string, bool> WordFinished; // (Wort, richtig?)

        public GameState State { get; private set; } = GameState.Start;
        public float TimeLeft { get; private set; }
        public int CorrectWords { get; private set; }
        public int WordErrors { get; private set; }
        public int Hits { get; private set; }
        public string CurrentWord => activeBoard != null ? activeBoard.Word : null;

        public RunnerPlayer Player { get; private set; }
        public CaveRunnerWorld World { get; private set; }

        TextMesh hudText, bigText, actionText;
        Transform wordRoot;
        readonly List<WordBoard> boards = new List<WordBoard>();
        WordBoard activeBoard;
        List<string> wordQueue;
        int wordsShown;
        float nextWordTime;
        float roundTime;
        float countdown;
        float actionTextTimer;

        void Awake()
        {
            // Spielwelt
            World = new GameObject("CaveRunnerWorld").AddComponent<CaveRunnerWorld>();
            World.transform.SetParent(transform, false);

            // Figur
            Player = new GameObject("Player").AddComponent<RunnerPlayer>();
            Player.transform.SetParent(transform, false);
            Player.transform.localPosition = new Vector3(0, 0, playerZ);
            Player.ActionPerformed += a => { actionText.text = a; actionTextTimer = 0.6f; };

            World.Init(Player);
            World.ObstacleHit += _ => Hits++;

            wordRoot = new GameObject("WordBoards").transform;
            wordRoot.SetParent(transform, false);

            BuildReadLimit();
            BuildHud();
            SetupCamera();
            EnterState(GameState.Start);
        }

        void BuildReadLimit()
        {
            // Orange "Lesegrenze" quer über den Tunnel (steht still, bewegt sich NICHT mit)
            var limit = new GameObject("Lesegrenze").transform;
            limit.SetParent(transform, false);
            limit.localPosition = new Vector3(0, 0, readLimitZ);
            var mat = RunnerUtil.Mat(new Color(1f, 0.55f, 0.1f), 1.5f);
            float w = World.tunnelWidth;
            RunnerUtil.Box("Linie Boden", limit, new Vector3(0, 0.02f, 0), new Vector3(w, 0.03f, 0.12f), mat);
            RunnerUtil.Box("Linie oben", limit, new Vector3(0, wordHeight + 0.9f, 0), new Vector3(w, 0.06f, 0.06f), mat);
            RunnerUtil.Box("Linie L", limit, new Vector3(-w * 0.5f + 0.05f, 2.2f, 0), new Vector3(0.06f, 4.4f, 0.06f), mat);
            RunnerUtil.Box("Linie R", limit, new Vector3(w * 0.5f - 0.05f, 2.2f, 0), new Vector3(0.06f, 4.4f, 0.06f), mat);
        }

        void BuildHud()
        {
            // Text in der 3D-Welt, damit er auch auf den CAVE-Wänden sichtbar ist
            var hud = new GameObject("HUD").transform;
            hud.SetParent(transform, false);
            hudText = RunnerUtil.Text("HUD", hud, new Vector3(0, 4.05f, 7f), 0.035f, Color.white);
            bigText = RunnerUtil.Text("Info", hud, new Vector3(0, 2.3f, 8f), 0.07f, new Color(1f, 0.85f, 0.3f));
            actionText = RunnerUtil.Text("Aktion", hud, new Vector3(0, 0.6f, 6f), 0.03f, new Color(0.6f, 0.9f, 1f));
        }

        void SetupCamera()
        {
            // Ist ein CAVE-Prefab in der Szene, kümmert sich das CAVE-Paket um die Kameras.
            if (!setupDesktopCamera || GameObject.Find("CAVE") != null) return;

            var cam = Camera.main;
            if (cam == null)
            {
                cam = new GameObject("Desktop Camera").AddComponent<Camera>();
                cam.tag = "MainCamera";
                cam.gameObject.AddComponent<AudioListener>();
            }
            cam.transform.position = new Vector3(0f, 2.4f, -1.5f);
            cam.transform.rotation = Quaternion.Euler(10f, 0f, 0f);
            cam.fieldOfView = 70f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = RenderSettings.fogColor;
        }

        // ---------- Zustände ----------

        void EnterState(GameState s)
        {
            State = s;
            Player.ControlsEnabled = s == GameState.Countdown || s == GameState.Running;
            World.Running = s == GameState.Running;
            foreach (var b in boards) if (b != null) b.enabled = s == GameState.Running;

            switch (s)
            {
                case GameState.Start:
                    bigText.text = "CAVE-RUNNER\n\nLaufen + Wörter lesen\nEnter / Leertaste = Start";
                    break;
                case GameState.Countdown:
                    countdown = countdownDuration;
                    break;
                case GameState.Running:
                    bigText.text = "";
                    break;
                case GameState.Won:
                    bigText.text = $"GEWONNEN!\n{CorrectWords} von {words.Length} richtig\n\nR = Neustart";
                    break;
                case GameState.Lost:
                    bigText.text = (WordErrors >= maxWordErrors ? "VERLOREN!\n3 Wortfehler" : $"VERLOREN!\nnur {CorrectWords} von {words.Length} richtig")
                                   + "\n\nR = Neustart";
                    break;
            }
            StateChanged?.Invoke(s);
        }

        /// <summary>Kann auch von außen aufgerufen werden (z.B. Start per Geste).</summary>
        public void StartGame()
        {
            if (State == GameState.Countdown || State == GameState.Running) return;
            ResetRound();
            EnterState(GameState.Countdown);
        }

        void ResetRound()
        {
            foreach (var b in boards) if (b != null) Destroy(b.gameObject);
            boards.Clear();
            activeBoard = null;
            World.ClearObstacles();
            Player.ResetPlayer();

            CorrectWords = WordErrors = Hits = wordsShown = 0;
            roundTime = 0f;
            TimeLeft = roundDuration;

            wordQueue = new List<string>(words);
            if (shuffleWords)
                for (int i = 0; i < wordQueue.Count; i++)
                {
                    int j = UnityEngine.Random.Range(i, wordQueue.Count);
                    (wordQueue[i], wordQueue[j]) = (wordQueue[j], wordQueue[i]);
                }
            nextWordTime = 2f;
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit(); // Build beenden

            switch (State)
            {
                case GameState.Start:
                case GameState.Won:
                case GameState.Lost:
                    if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.R))
                        StartGame();
                    break;

                case GameState.Countdown:
                    countdown -= Time.deltaTime;
                    bigText.text = countdown > 0f ? Mathf.CeilToInt(countdown).ToString() : "LOS!";
                    if (countdown <= 0f) EnterState(GameState.Running);
                    break;

                case GameState.Running:
                    UpdateRunning();
                    break;
            }

            if (actionTextTimer > 0f && (actionTextTimer -= Time.deltaTime) <= 0f) actionText.text = "";
            UpdateHud();
        }

        void UpdateRunning()
        {
            roundTime += Time.deltaTime;
            TimeLeft = Mathf.Max(0f, roundDuration - roundTime);

            // Nächstes Wort
            if (activeBoard == null && wordsShown < wordQueue.Count && roundTime >= nextWordTime)
                SpawnWord(wordQueue[wordsShown++]);

            // Lesegrenze überschritten?
            if (activeBoard != null && activeBoard.transform.localPosition.z <= readLimitZ)
                FinishWord(false);

            if (keyboardWordInput && activeBoard != null)
            {
                if (Input.GetKeyDown(KeyCode.Return)) MarkCurrentWordCorrect();
                else if (Input.GetKeyDown(KeyCode.Backspace)) FinishWord(false);
            }

            // Alte Tafeln aufräumen
            for (int i = boards.Count - 1; i >= 0; i--)
                if (boards[i].transform.localPosition.z < World.despawnZ) { Destroy(boards[i].gameObject); boards.RemoveAt(i); }

            if (State != GameState.Running) return; // evtl. durch 3. Fehler verloren
            if (TimeLeft <= 0f)
                EnterState(CorrectWords >= wordsToWin ? GameState.Won : GameState.Lost);
        }

        void SpawnWord(string word)
        {
            activeBoard = WordBoard.Create(wordRoot, word, new Vector3(0f, wordHeight, wordSpawnZ), wordSpeed);
            boards.Add(activeBoard);
            WordAppeared?.Invoke(word);
        }

        void FinishWord(bool correct)
        {
            if (activeBoard == null) return;
            var word = activeBoard.Word;
            activeBoard.SetState(correct ? WordBoard.BoardState.Correct : WordBoard.BoardState.Missed);
            activeBoard = null;

            if (correct) CorrectWords++;
            else WordErrors++;
            WordFinished?.Invoke(word, correct);

            nextWordTime = roundTime + NextPause();

            if (WordErrors >= maxWordErrors) EnterState(GameState.Lost);
        }

        /// <summary>
        /// Zufällige Pause (direkt danach / 10 / 15 / 20 s), aber nur so lang,
        /// dass alle restlichen Wörter noch vor Rundenende gelesen werden können.
        /// </summary>
        float NextPause()
        {
            int remaining = wordQueue.Count - wordsShown;
            if (remaining <= 0) return 0f;
            float travelTime = (wordSpawnZ - readLimitZ) / wordSpeed;
            float budget = (roundDuration - roundTime) - remaining * travelTime - 1f;
            float maxPause = Mathf.Max(0.5f, budget / remaining);
            float pause = pauseOptions[UnityEngine.Random.Range(0, pauseOptions.Length)];
            return Mathf.Min(pause, maxPause);
        }

        // ---------- Schnittstelle Worterkennung ----------

        /// <summary>Erkanntes Wort übergeben. Passt es zur aktuellen Tafel, zählt es als richtig.</summary>
        public bool SubmitRecognizedWord(string recognized)
        {
            if (State != GameState.Running || activeBoard == null || string.IsNullOrEmpty(recognized)) return false;
            bool match = string.Equals(recognized.Trim(), activeBoard.Word, StringComparison.OrdinalIgnoreCase);
            if (match) FinishWord(true);
            return match;
        }

        public void MarkCurrentWordCorrect() => FinishWord(true);

        // ---------- Anzeige ----------

        void UpdateHud()
        {
            if (State == GameState.Start) { hudText.text = ""; return; }
            hudText.text = $"Zeit: {Mathf.CeilToInt(TimeLeft)} s      Richtig: {CorrectWords}/{words.Length}      " +
                           $"Fehler: {WordErrors}/{maxWordErrors}      Treffer: {Hits}";
        }
    }
}
