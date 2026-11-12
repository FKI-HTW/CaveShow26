// Windows-Spracherkennung nur dort mitkompilieren, wo es sie gibt:
// im Windows-Build (UNITY_STANDALONE_WIN) und im Windows-Editor – aber nie im macOS-/Linux-Editor,
// auch wenn dort als Build-Ziel "Windows" eingestellt ist (dann ist UNITY_STANDALONE_WIN nämlich auch gesetzt).
#if (UNITY_STANDALONE_WIN && !UNITY_EDITOR_OSX && !UNITY_EDITOR_LINUX) || UNITY_EDITOR_WIN
#define WINDOWS_SPEECH
#endif

using System;
using System.Collections.Generic;
using UnityEngine;
#if WINDOWS_SPEECH
using UnityEngine.Windows.Speech;
#endif

/// <summary>
/// CAVE-RUNNER – das Spiel (First Person): Tunnel, Hindernisse, Wörter, Anzeige.
/// Die Steuerung über das Kinect-Tracking macht <see cref="CaveRunnerTracking"/> auf demselben Objekt.
///
/// SZENE: Assets/CaveRunner/CaveRunner.unity (Menü "CaveShow > Runner-Szene neu erstellen").
///   Sie enthält das CAVE-Prefab und ein Objekt mit CaveRunner + CaveRunnerTracking
///   und ist unter File > Build Settings eingetragen -> File > Build And Run.
///
/// ANSICHT: Sind mehrere Bildschirme angeschlossen (CAVE), wird auf die CAVE-Wände ausgegeben.
///   An einem einzelnen Bildschirm (PC, Editor) gibt es automatisch die normale First-Person-Ansicht.
///   Taste V schaltet um, im Inspector lässt sich die Ansicht bei "View" fest einstellen.
///
/// PRINZIP: Die Person steht in der Mitte der CAVE, die Welt fährt auf sie zu (Laufband).
///          Beim Spurwechsel wird die Welt seitlich verschoben. Die CAVE selbst bleibt stehen,
///          sonst käme das Kopf-Tracking (Perspektive und Gesten) durcheinander.
///
/// WÖRTER: Im Windows-Build hört die Spracherkennung (VoiceRecognizer, unten in dieser Datei) über das Mikrofon mit.
///         Auf dem Mac gibt es nur den Simulationsmodus -> Wörter mit R / F bewerten.
///
/// TASTEN (Betreuung/Test): Enter/Leertaste/R = Start, ←/→ = Spur, Leertaste/↑ = Springen, ↓ = Ducken,
///                          R oder Enter = Wort richtig, F oder Backspace = Wortfehler,
///                          P = Pause (z. B. bei Mikrofon-Problemen, zählt keinen Fehler), V = Ansicht, Esc = Beenden.
///
/// SCHNITTSTELLEN
///   Steuerung:      runner.StartGame(), SetLane(0..2), MoveLeft(), MoveRight(), Jump(), Duck(), TogglePause()
///   Worterkennung:  runner.WordAppeared += wort => ...;   runner.SubmitRecognizedWord("small");
/// </summary>
public class CaveRunner : MonoBehaviour
{
    // =====================================================================
    //  Einstellungen (Inspector)
    // =====================================================================

    [Header("Runde")]
    public float roundDuration = 90f;
    public int wordsToWin = 8;
    public int maxWordErrors = 3;

    [Header("Tunnel")]
    public float tunnelWidth = 6.5f;
    public float tunnelHeight = 4.5f;
    public float segmentLength = 10f;
    public int segmentCount = 10;

    [Header("Tempo und Hindernisse")]
    [Tooltip("Meter pro Sekunde, mit der die Welt auf die Person zukommt.")]
    public float speed = 6f;
    public float obstacleMinGap = 20f;
    public float obstacleMaxGap = 32f;
    public float boxHeight = 0.6f;        // Kiste -> ausweichen
    public float hurdleHeight = 0.3f;     // niedriger Balken -> springen
    public float barBottomHeight = 1.3f;  // Stange -> ducken
    [Tooltip("Toleranz (m) vor und hinter der Person: Wer irgendwann in diesem Bereich in der Luft bzw. geduckt ist, hat das Hindernis geschafft.")]
    public float avoidTolerance = 0.9f;

    public enum CollisionMode { InstantGameOver, SlowDownAndShake, IgnoreForTesting }
    [Header("Kollision")]
    [Tooltip("Was passiert bei einem Treffer? InstantGameOver = Runde verloren, SlowDownAndShake = kurz langsamer + Wackeln, IgnoreForTesting = nur mitzählen.")]
    public CollisionMode collisionMode = CollisionMode.SlowDownAndShake;
    [Tooltip("Nur SlowDownAndShake: Tempo-Faktor und Dauer (s) nach einem Treffer.")]
    public float slowFactor = 0.4f;
    public float slowDuration = 1.5f;

    [Header("Wörter")]
    public string[] words = { "SMALL", "HOUSE", "GREEN", "WATER", "APPLE", "TIGER", "CLOUD", "LIGHT", "STONE", "RIVER" };
    public float wordSpawnZ = 45f;
    [Tooltip("Lesegrenze: bis hier muss das Wort gelesen sein.")]
    public float readLimitZ = 8f;
    [Tooltip("Tafeln sind langsamer als die Hindernisse.")]
    public float wordSpeed = 4.5f;
    public float wordHeight = 2.6f;
    public float[] pauseOptions = { 0.5f, 0.5f, 3f, 10f, 15f, 20f };
    public VoiceRecognizer voice = new VoiceRecognizer();

    [Header("Steuerung / Kamera")]
    [Tooltip("Tasten für Betreuung und Test (siehe Kommentar oben).")]
    public bool keyboardInput = true;
    public enum ViewMode { Auto, Cave, FirstPerson }
    [Tooltip("Auto = CAVE-Wände, wenn mehrere Bildschirme angeschlossen sind, sonst First-Person-Ansicht (PC, Editor).")]
    public ViewMode view = ViewMode.Auto;
    public float jumpHeight = 0.9f;
    public float eyeHeight = 1.7f;        // nur First-Person-Kamera
    public float duckEyeHeight = 1.0f;    // nur First-Person-Kamera

    [Header("Materialien")]
    [Tooltip("Vorlagen für alle Flächen. Müssen als Asset in der Szene hängen, sonst fehlt der Shader im Build (alles pink).")]
    public Material surfaceMaterial;
    [Tooltip("Vorlage für leuchtende Flächen (Emission eingeschaltet).")]
    public Material glowMaterial;

    // =====================================================================
    //  Öffentlicher Zustand + Events
    // =====================================================================

    public enum State { Start, Countdown, Running, Won, Lost }
    public State GameState { get; private set; }
    /// <summary>Countdown oder Runde läuft -> Spur, Springen und Ducken sind aktiv.</summary>
    public bool IsPlaying => GameState == State.Countdown || GameState == State.Running;
    /// <summary>Pause (Taste P): alles steht, Steuerung und Wörter werden ignoriert.</summary>
    public bool Paused { get; private set; }
    public int CurrentLane { get; private set; } = 1;
    public int CorrectWords { get; private set; }
    public int WordErrors { get; private set; }
    public int Hits { get; private set; }
    public float TimeLeft => Mathf.Max(0f, roundDuration - roundTime);
    public string CurrentWord => activeBoard?.word;

    /// <summary>Zeile unter Titel/Ergebnis: wie die nächste Runde gestartet wird.</summary>
    public string StartHint { get; set; } = "Enter = Start";
    /// <summary>Zusätzliche Zeile unter der Anzeige (z. B. Tracking-Werte zum Einstellen).</summary>
    public string InfoLine { get; set; } = "";

    public event Action<string> WordAppeared;
    public event Action<string, bool> WordFinished; // (Wort, richtig?)
    public event Action<State> StateChanged;

    static readonly float[] Lanes = { -1.6f, 0f, 1.6f };
    const float DespawnZ = -12f, SpawnZ = 80f, JumpDuration = 0.8f, DuckDuration = 0.9f;

    enum ObstacleType { Box, Hurdle, Bar }
    class Obstacle { public Transform t; public ObstacleType type; public int lane; public bool passed, laneChecked, inLane, avoided; }
    class Board { public Transform t; public string word; public Renderer frame; public bool done; }

    // =====================================================================
    //  Interne Variablen
    // =====================================================================

    Transform world;
    Camera pcCam;
    Camera[] caveCams;
    bool firstPerson;
    TextMesh hudText, bigText, actionText;
    readonly List<Transform> segments = new List<Transform>();
    readonly List<Obstacle> obstacles = new List<Obstacle>();
    readonly List<Board> boards = new List<Board>();
    readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();
    Material baseMat;
    Board activeBoard;
    List<string> wordQueue = new List<string>();
    int wordsShown;
    float roundTime, countdown, nextWordTime, nextObstacleIn = 30f;
    float laneX, jumpTime = -1f, jumpOffset, duckTimer, duckAmount, actionTimer, shake, slowTimer;
    string lostReason = "";

    bool IsDucking => duckTimer > 0f;
    bool IsAirborne => jumpTime >= 0f && jumpOffset > 0.15f;

    // =====================================================================
    //  Steuerung (Tracking und Tasten)
    // =====================================================================

    public void MoveLeft() => SetLane(CurrentLane - 1);
    public void MoveRight() => SetLane(CurrentLane + 1);

    /// <summary>Spur wählen: 0 = links, 1 = Mitte, 2 = rechts.</summary>
    public void SetLane(int lane)
    {
        lane = Mathf.Clamp(lane, 0, 2);
        if (!IsPlaying || Paused || lane == CurrentLane) return;
        ShowAction(lane < CurrentLane ? "Links" : "Rechts");
        CurrentLane = lane;
    }

    public void Jump()
    {
        if (!IsPlaying || Paused || jumpTime >= 0f) return;
        duckTimer = 0f; jumpTime = 0f; ShowAction("Sprung");
    }

    /// <summary>Ducken. Solange die Person unten bleibt, einfach weiter aufrufen.</summary>
    public void Duck()
    {
        if (!IsPlaying || Paused) return;
        if (!IsDucking) ShowAction("Ducken");
        jumpTime = -1f; jumpOffset = 0f; duckTimer = DuckDuration;
    }

    /// <summary>Pause ein/aus. Friert das Spiel ein (Time.timeScale = 0), es wird kein Wortfehler gezählt.</summary>
    public void TogglePause()
    {
        if (!IsPlaying) return;
        Paused = !Paused;
        Time.timeScale = Paused ? 0f : 1f;
    }

    // =====================================================================
    //  Worterkennung
    // =====================================================================

    /// <summary>Erkanntes Wort übergeben. Gibt true zurück, wenn es zur aktuellen Tafel passt.</summary>
    public bool SubmitRecognizedWord(string recognized)
    {
        if (Paused || GameState != State.Running || activeBoard == null || string.IsNullOrEmpty(recognized)) return false;
        bool match = string.Equals(recognized.Trim(), activeBoard.word, StringComparison.OrdinalIgnoreCase);
        if (match) FinishWord(true);
        return match;
    }

    public void StartGame()
    {
        if (IsPlaying) return;
        foreach (var o in obstacles) Destroy(o.t.gameObject);
        foreach (var b in boards) Destroy(b.t.gameObject);
        obstacles.Clear(); boards.Clear(); activeBoard = null;
        CorrectWords = WordErrors = Hits = wordsShown = 0;
        roundTime = 0f; nextWordTime = 2f; nextObstacleIn = 30f;
        CurrentLane = 1; laneX = 0f; jumpTime = -1f; jumpOffset = 0f; duckTimer = 0f; slowTimer = 0f;

        wordQueue = new List<string>(words);
        for (int i = 0; i < wordQueue.Count; i++)
        {
            int j = UnityEngine.Random.Range(i, wordQueue.Count);
            (wordQueue[i], wordQueue[j]) = (wordQueue[j], wordQueue[i]);
        }
        countdown = 3f;
        SetState(State.Countdown);
    }

    // =====================================================================
    //  Aufbau
    // =====================================================================

    void Awake()
    {
        // Spiel genau auf die CAVE setzen (Ursprung = Mitte des CAVE-Bodens, Blick zur Frontwand).
        var cave = FindObjectOfType<HTW.CAVE.VirtualEnvironment>();
        if (cave != null)
        {
            transform.SetPositionAndRotation(cave.transform.position, cave.transform.rotation);
            caveCams = cave.GetComponentsInChildren<Camera>(true);
        }
        // CAVE-Wände nur, wenn es eine CAVE gibt und mehr als ein Bildschirm angeschlossen ist.
        SetFirstPerson(view == ViewMode.FirstPerson || (view == ViewMode.Auto && Display.displays.Length <= 1));

        world = new GameObject("World (bewegt sich)").transform;
        world.SetParent(transform, false);

        // Atmosphäre: dunkel + Nebel (versteckt das "Aufploppen" hinten)
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.35f, 0.32f, 0.3f);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.08f, 0.07f, 0.07f);
        RenderSettings.fogStartDistance = 35f;
        RenderSettings.fogEndDistance = SpawnZ + 5f;
        if (FindObjectOfType<Light>() == null)
        {
            var sun = new GameObject("Directional Light").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.5f;
            sun.transform.rotation = Quaternion.Euler(50f, -20f, 0f);
        }

        for (int i = 0; i < segmentCount; i++) segments.Add(BuildSegment(i, DespawnZ + i * segmentLength));
        BuildReadLimit();

        // Text in der 3D-Welt (sichtbar auf der Frontwand der CAVE), bewegt sich nicht mit
        hudText = MakeText(transform, new Vector3(0, 2.9f, 6f), 0.03f, Color.white);
        bigText = MakeText(transform, new Vector3(0, 1.8f, 6f), 0.05f, new Color(1f, 0.85f, 0.3f));
        actionText = MakeText(transform, new Vector3(0, 1.0f, 4f), 0.025f, new Color(0.6f, 0.9f, 1f));

        SetState(State.Start);
    }

    void Start()
    {
        foreach (var cam in FindObjectsOfType<Camera>())
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = RenderSettings.fogColor;
        }
        voice.WordRecognized += word => SubmitRecognizedWord(word);
        voice.StartListening(words);
    }

    void OnDestroy()
    {
        voice.Stop();
        Time.timeScale = 1f;
    }

    /// <summary>Zwischen First-Person-Ansicht (ein Bildschirm) und Ausgabe auf die CAVE-Wände umschalten.</summary>
    void SetFirstPerson(bool on)
    {
        firstPerson = on || caveCams == null; // ohne CAVE gibt es nur die First-Person-Ansicht
        if (firstPerson && pcCam == null) SetupPcCamera();
        if (pcCam != null) pcCam.enabled = firstPerson;
        if (caveCams != null) foreach (var cam in caveCams) cam.enabled = !firstPerson;
    }

    void SetupPcCamera()
    {
        if (caveCams == null) pcCam = Camera.main;
        if (pcCam == null)
        {
            pcCam = new GameObject("First Person Camera").AddComponent<Camera>();
            if (FindObjectOfType<AudioListener>() == null) pcCam.gameObject.AddComponent<AudioListener>();
        }
        pcCam.depth = 100f; // liegt über den CAVE-Kameras
        pcCam.fieldOfView = 75f;
        pcCam.nearClipPlane = 0.05f;
        pcCam.clearFlags = CameraClearFlags.SolidColor;
        pcCam.backgroundColor = RenderSettings.fogColor;
        pcCam.transform.rotation = transform.rotation * Quaternion.Euler(4f, 0f, 0f);
    }

    Transform BuildSegment(int index, float z)
    {
        var seg = new GameObject("Segment " + index).transform;
        seg.SetParent(world, false);
        seg.localPosition = new Vector3(0, 0, z);
        float L = segmentLength, W = tunnelWidth, H = tunnelHeight;
        var wall = index % 2 == 0 ? new Color(0.36f, 0.3f, 0.26f) : new Color(0.3f, 0.25f, 0.22f);
        var rib = new Color(0.45f, 0.33f, 0.2f);
        var line = Mat(new Color(0.95f, 0.9f, 0.6f), 0.6f);

        Box(seg, new Vector3(0, -0.05f, 0), new Vector3(W, 0.1f, L), Mat(new Color(0.22f, 0.2f, 0.19f)));
        Box(seg, new Vector3(-0.8f, 0.01f, 0), new Vector3(0.08f, 0.02f, L * 0.45f), line);
        Box(seg, new Vector3(0.8f, 0.01f, 0), new Vector3(0.08f, 0.02f, L * 0.45f), line);
        Box(seg, new Vector3(-W / 2 - 0.1f, H / 2, 0), new Vector3(0.2f, H, L), Mat(wall));
        Box(seg, new Vector3(W / 2 + 0.1f, H / 2, 0), new Vector3(0.2f, H, L), Mat(wall));
        Box(seg, new Vector3(0, H + 0.1f, 0), new Vector3(W + 0.4f, 0.2f, L), Mat(new Color(0.18f, 0.16f, 0.15f)));

        float zr = -L / 2 + 0.2f; // Holzbogen
        Box(seg, new Vector3(-W / 2 + 0.15f, H / 2, zr), new Vector3(0.3f, H, 0.3f), Mat(rib));
        Box(seg, new Vector3(W / 2 - 0.15f, H / 2, zr), new Vector3(0.3f, H, 0.3f), Mat(rib));
        Box(seg, new Vector3(0, H - 0.15f, zr), new Vector3(W, 0.3f, 0.3f), Mat(rib));

        if (index % 2 == 0) // Lampe
        {
            Box(seg, new Vector3(0, H - 0.05f, 0), new Vector3(0.6f, 0.1f, 1.2f), Mat(new Color(1f, 0.8f, 0.45f), 2f));
            var light = new GameObject("Light").AddComponent<Light>();
            light.transform.SetParent(seg, false);
            light.transform.localPosition = new Vector3(0, H - 0.6f, 0);
            light.type = LightType.Point;
            light.range = 14f;
            light.intensity = 1.6f;
            light.color = new Color(1f, 0.8f, 0.55f);
        }
        return seg;
    }

    void BuildReadLimit()
    {
        var t = new GameObject("Lesegrenze").transform;
        t.SetParent(world, false);
        t.localPosition = new Vector3(0, 0, readLimitZ);
        var m = Mat(new Color(1f, 0.55f, 0.1f), 1.5f);
        float W = tunnelWidth;
        Box(t, new Vector3(0, 0.02f, 0), new Vector3(W, 0.03f, 0.12f), m);
        Box(t, new Vector3(0, wordHeight + 0.9f, 0), new Vector3(W, 0.06f, 0.06f), m);
        Box(t, new Vector3(-W / 2 + 0.05f, 2.2f, 0), new Vector3(0.06f, 4.4f, 0.06f), m);
        Box(t, new Vector3(W / 2 - 0.05f, 2.2f, 0), new Vector3(0.06f, 4.4f, 0.06f), m);
    }

    void SpawnObstacle(ObstacleType type, int lane)
    {
        var t = new GameObject(type + " Spur " + lane).transform;
        t.SetParent(world, false);
        t.localPosition = new Vector3(Lanes[lane], 0, SpawnZ);
        var post = Mat(new Color(0.5f, 0.35f, 0.2f));

        if (type == ObstacleType.Box)
            Box(t, new Vector3(0, boxHeight / 2, 0), new Vector3(1.1f, boxHeight, 1.1f), Mat(new Color(0.85f, 0.55f, 0.25f)));
        else if (type == ObstacleType.Hurdle)
        {
            float legH = hurdleHeight - 0.12f;
            Box(t, new Vector3(0, hurdleHeight - 0.06f, 0), new Vector3(1.4f, 0.12f, 0.15f), Mat(new Color(1f, 0.5f, 0.1f), 0.4f));
            Box(t, new Vector3(-0.6f, legH / 2, 0), new Vector3(0.08f, legH, 0.08f), post);
            Box(t, new Vector3(0.6f, legH / 2, 0), new Vector3(0.08f, legH, 0.08f), post);
        }
        else
        {
            float postH = barBottomHeight + 0.2f;
            Box(t, new Vector3(0, barBottomHeight + 0.1f, 0), new Vector3(1.5f, 0.2f, 0.2f), Mat(new Color(0.9f, 0.2f, 0.25f), 0.4f));
            Box(t, new Vector3(-0.72f, postH / 2, 0), new Vector3(0.1f, postH, 0.1f), post);
            Box(t, new Vector3(0.72f, postH / 2, 0), new Vector3(0.1f, postH, 0.1f), post);
        }
        obstacles.Add(new Obstacle { t = t, type = type, lane = lane });
    }

    void SpawnWord(string word)
    {
        var t = new GameObject("Wort " + word).transform;
        t.SetParent(world, false);
        t.localPosition = new Vector3(0, wordHeight, wordSpawnZ);
        var frame = Box(t, Vector3.zero, new Vector3(3.2f, 1.1f, 0.1f), Mat(new Color(1f, 0.6f, 0.15f), 1.2f));
        Box(t, new Vector3(0, 0, -0.06f), new Vector3(3f, 0.9f, 0.05f), Mat(new Color(0.98f, 0.95f, 0.88f), 0.3f));
        var text = MakeText(t, new Vector3(0, 0, -0.1f), 0.11f, new Color(0.1f, 0.1f, 0.1f));
        text.text = word;
        text.fontStyle = FontStyle.Bold;

        activeBoard = new Board { t = t, word = word, frame = frame.GetComponent<Renderer>() };
        boards.Add(activeBoard);
        WordAppeared?.Invoke(word);
    }

    // =====================================================================
    //  Spielablauf
    // =====================================================================

    void SetState(State s)
    {
        GameState = s;
        StateChanged?.Invoke(s);
    }

    string BigText()
    {
        if (Paused) return "PAUSE\n\nP = weiter";
        switch (GameState)
        {
            case State.Start: return "CAVE-RUNNER\n\n" + StartHint;
            case State.Countdown: return Mathf.CeilToInt(countdown).ToString();
            case State.Won: return $"GEWONNEN!\n{CorrectWords} von {words.Length} richtig\n\n{StartHint}";
            case State.Lost:
                return "VERLOREN!\n" + lostReason + "\n\n" + StartHint;
            default: return "";
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        if (keyboardInput && Input.GetKeyDown(KeyCode.P)) TogglePause();
        if (Paused) { bigText.text = BigText(); return; }
        if (keyboardInput) ReadKeyboard();
        float dt = Time.deltaTime;

        if (GameState == State.Countdown)
        {
            countdown -= dt;
            if (countdown <= 0f) SetState(State.Running);
        }
        else if (GameState == State.Running)
        {
            roundTime += dt;
            if (slowTimer > 0f) slowTimer -= dt;
            MoveWorld(speed * (slowTimer > 0f ? slowFactor : 1f) * dt);
            if (GameState == State.Running) UpdateWords(dt);
            if (GameState == State.Running && TimeLeft <= 0f)
            {
                if (CorrectWords >= wordsToWin) SetState(State.Won);
                else Lose($"nur {CorrectWords} von {words.Length} richtig");
            }
        }

        // Spieler: Spur, Sprung, Ducken
        laneX = Mathf.MoveTowards(laneX, Lanes[CurrentLane], 8f * dt);
        if (jumpTime >= 0f)
        {
            jumpTime += dt;
            float t = jumpTime / JumpDuration;
            if (t >= 1f) { jumpTime = -1f; jumpOffset = 0f; }
            else jumpOffset = 4f * jumpHeight * t * (1f - t);
        }
        if (duckTimer > 0f) duckTimer -= dt;
        duckAmount = Mathf.MoveTowards(duckAmount, IsDucking ? 1f : 0f, 6f * dt);
        if (actionTimer > 0f && (actionTimer -= dt) <= 0f) actionText.text = "";

        bigText.text = BigText();
        hudText.text = GameState == State.Start ? "" :
            $"Zeit: {Mathf.CeilToInt(TimeLeft)} s     Richtig: {CorrectWords}/{words.Length}     Fehler: {WordErrors}/{maxWordErrors}     Treffer: {Hits}";
        if (!string.IsNullOrEmpty(InfoLine)) hudText.text += "\n" + InfoLine;
        if (!string.IsNullOrEmpty(voice.Status)) hudText.text += "\n" + voice.Status;
    }

    void Lose(string reason)
    {
        lostReason = reason;
        SetState(State.Lost);
    }

    void LateUpdate()
    {
        // Spurwechsel: die Welt rutscht zur Seite, Person und CAVE bleiben stehen. Bei einem Treffer wackelt sie kurz.
        Vector3 wobble = Vector3.zero;
        if (shake > 0f) { shake -= Time.deltaTime; wobble = UnityEngine.Random.insideUnitSphere * 0.06f; }
        world.localPosition = new Vector3(-laneX, 0, 0) + wobble;

        // In der CAVE springt und duckt sich die Person selbst, nur die First-Person-Kamera muss das nachmachen.
        if (firstPerson)
            pcCam.transform.position = transform.TransformPoint(new Vector3(0, Mathf.Lerp(eyeHeight, duckEyeHeight, duckAmount) + jumpOffset, 0));
    }

    void ReadKeyboard()
    {
        if (Input.GetKeyDown(KeyCode.V)) SetFirstPerson(!firstPerson);
        if (!IsPlaying)
        {
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.R)) StartGame();
            return;
        }
        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) MoveLeft();
        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) MoveRight();
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Jump();
        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Duck();
        if (GameState == State.Running && activeBoard != null)
        {
            // Betreuung / Test ohne Mikrofon: Wort von Hand bewerten
            if (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Return)) FinishWord(true);
            else if (Input.GetKeyDown(KeyCode.F) || Input.GetKeyDown(KeyCode.Backspace)) FinishWord(false);
        }
    }

    void MoveWorld(float move)
    {
        // Tunnel-Segmente: hinten raus -> vorne wieder anhängen
        foreach (var seg in segments)
        {
            var p = seg.localPosition;
            p.z -= move;
            if (p.z < DespawnZ - segmentLength) p.z += segmentCount * segmentLength;
            seg.localPosition = p;
        }

        // Hindernisse bewegen
        for (int i = obstacles.Count - 1; i >= 0; i--)
        {
            var o = obstacles[i];
            var p = o.t.localPosition;
            p.z -= move;
            o.t.localPosition = p;

            // Kollision rund um z = 0 (da steht die Person). Springen/Ducken muss nicht auf den Punkt genau sitzen:
            // es zählt, wenn es irgendwann zwischen +avoidTolerance und -avoidTolerance passiert.
            if (!o.passed && p.z <= avoidTolerance)
            {
                if (o.type == ObstacleType.Hurdle ? IsAirborne : o.type == ObstacleType.Bar && IsDucking) o.avoided = true;
                if (!o.laneChecked && p.z <= 0f) { o.laneChecked = true; o.inLane = o.lane == CurrentLane; }
                if (p.z <= -avoidTolerance)
                {
                    o.passed = true;
                    if (o.inLane && !o.avoided) OnCollision();
                }
            }
            if (p.z < DespawnZ) { Destroy(o.t.gameObject); obstacles.RemoveAt(i); }
        }

        nextObstacleIn -= move;
        if (nextObstacleIn <= 0f)
        {
            int lane = UnityEngine.Random.Range(0, 3);
            var type = (ObstacleType)UnityEngine.Random.Range(0, 3);
            SpawnObstacle(type, lane);
            if (type == ObstacleType.Box && UnityEngine.Random.value < 0.25f)
                SpawnObstacle(ObstacleType.Box, (lane + UnityEngine.Random.Range(1, 3)) % 3); // eine Spur bleibt frei
            nextObstacleIn = UnityEngine.Random.Range(obstacleMinGap, obstacleMaxGap);
        }
    }

    void OnCollision()
    {
        Hits++;
        switch (collisionMode)
        {
            case CollisionMode.InstantGameOver:
                shake = 0.3f;
                Lose("Hindernis getroffen");
                break;
            case CollisionMode.SlowDownAndShake:
                shake = 0.3f;
                slowTimer = slowDuration;
                ShowAction("TREFFER!", new Color(1f, 0.25f, 0.2f));
                break;
            case CollisionMode.IgnoreForTesting:
                break; // nur mitzählen (Anzeige "Treffer")
        }
    }

    void UpdateWords(float dt)
    {
        if (activeBoard == null && wordsShown < wordQueue.Count && roundTime >= nextWordTime)
            SpawnWord(wordQueue[wordsShown++]);

        for (int i = boards.Count - 1; i >= 0; i--)
        {
            var b = boards[i];
            b.t.localPosition += Vector3.back * wordSpeed * dt;
            if (b.t.localPosition.z < DespawnZ) { Destroy(b.t.gameObject); boards.RemoveAt(i); }
        }

        if (activeBoard != null && activeBoard.t.localPosition.z <= readLimitZ) FinishWord(false);
    }

    void FinishWord(bool correct)
    {
        if (activeBoard == null) return;
        activeBoard.done = true;
        activeBoard.frame.sharedMaterial = Mat(correct ? new Color(0.2f, 0.85f, 0.3f) : new Color(0.9f, 0.15f, 0.15f), 1.2f);
        string word = activeBoard.word;
        activeBoard = null;

        if (correct) CorrectWords++; else WordErrors++;
        WordFinished?.Invoke(word, correct);

        // Pause bis zum nächsten Wort – aber so kurz, dass alle restlichen Wörter noch reinpassen
        int remaining = wordQueue.Count - wordsShown;
        float travel = (wordSpawnZ - readLimitZ) / wordSpeed;
        float maxPause = remaining > 0 ? Mathf.Max(0.5f, (TimeLeft - remaining * travel - 1f) / remaining) : 0f;
        nextWordTime = roundTime + Mathf.Min(pauseOptions[UnityEngine.Random.Range(0, pauseOptions.Length)], maxPause);

        if (WordErrors >= maxWordErrors) Lose($"{WordErrors} Wortfehler");
    }

    // =====================================================================
    //  Hilfsfunktionen
    // =====================================================================

    /// <summary>Kurze Meldung unten in der Anzeige (z. B. "Sprung").</summary>
    public void ShowAction(string text) => ShowAction(text, new Color(0.6f, 0.9f, 1f));

    void ShowAction(string text, Color color)
    {
        actionText.text = text;
        actionText.color = color;
        actionTimer = 0.7f;
    }

    Material Mat(Color c, float emission = 0f)
    {
        var key = new Color(c.r, c.g, c.b, emission); // Emission im Alpha-Kanal als Schlüssel
        if (materials.TryGetValue(key, out var m)) return m;
        var template = emission > 0f && glowMaterial != null ? glowMaterial : surfaceMaterial;
        if (template == null)
        {
            // Notlösung ohne zugewiesene Materialien: funktioniert im Editor, im Build fehlt dann aber der Shader.
            if (baseMat == null)
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                baseMat = tmp.GetComponent<Renderer>().sharedMaterial;
                tmp.SetActive(false);
                Destroy(tmp);
                Debug.LogWarning("CaveRunner: Materialien nicht zugewiesen – Szene über \"CaveShow > Runner-Szene neu erstellen\" erzeugen.", this);
            }
            template = baseMat;
        }
        m = new Material(template) { color = c };
        if (emission > 0f) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * emission); }
        materials[key] = m;
        return m;
    }

    GameObject Box(Transform parent, Vector3 pos, Vector3 scale, Material m)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.GetComponent<Renderer>().sharedMaterial = m;
        Destroy(go.GetComponent<Collider>());
        return go;
    }

    TextMesh MakeText(Transform parent, Vector3 pos, float size, Color color)
    {
        var go = new GameObject("Text");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        var tm = go.AddComponent<TextMesh>();
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tm.fontSize = 64;
        tm.characterSize = size;
        tm.anchor = TextAnchor.MiddleCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = color;
        go.GetComponent<MeshRenderer>().sharedMaterial = tm.font.material;
        return tm;
    }
}

// =========================================================================
//  SPRACHERKENNUNG: Windows = KeywordRecognizer (offline, Mikrofon),
//  macOS = Simulationsmodus (Wörter mit den Tasten R / F bewerten).
// =========================================================================

[Serializable]
public class VoiceRecognizer
{
    [Tooltip("Aus = keine Spracherkennung, Wörter nur mit den Tasten R / F bewerten.")]
    public bool enabled = true;

    /// <summary>Ein Wort aus der Liste wurde gehört (kommt im Unity-Hauptthread an).</summary>
    public event Action<string> WordRecognized;
    /// <summary>Kurzer Zustand für die Anzeige.</summary>
    public string Status { get; private set; } = "";

    /// <summary>Zum Testen ohne Mikrofon: tut so, als wäre das Wort gehört worden.</summary>
    public void Simulate(string word) => WordRecognized?.Invoke(word);

#if WINDOWS_SPEECH
    KeywordRecognizer recognizer;

    public void StartListening(string[] words)
    {
        Stop();
        if (!enabled) { Status = "Sprache: aus (R = richtig, F = Fehler)"; return; }
        try
        {
            if (!PhraseRecognitionSystem.isSupported)
                throw new NotSupportedException("Windows-Spracherkennung wird auf diesem System nicht unterstützt.");

            var keywords = new List<string>();
            foreach (var w in words)
            {
                string k = (w ?? "").Trim().ToLowerInvariant();
                if (k.Length > 0 && !keywords.Contains(k)) keywords.Add(k);
            }
            // ConfidenceLevel.Low: lieber einmal zu großzügig als ein richtig gelesenes Wort verpassen
            recognizer = new KeywordRecognizer(keywords.ToArray(), ConfidenceLevel.Low);
            recognizer.OnPhraseRecognized += OnPhrase;
            PhraseRecognitionSystem.OnError += OnError;
            recognizer.Start();
            Status = "Sprache: Mikrofon aktiv";
            Debug.Log("VoiceRecognizer: Windows-Spracherkennung läuft (" + string.Join(", ", keywords) + ").");
        }
        catch (Exception e)
        {
            recognizer = null;
            Status = "Sprache: FEHLER (R = richtig, F = Fehler)";
            Debug.LogWarning("VoiceRecognizer: Spracherkennung konnte nicht gestartet werden – " + e.Message +
                             "\nPrüfen: Mikrofon angeschlossen? Windows-Einstellungen > Datenschutz > Spracherkennung/Mikrofon erlaubt? Englisches Sprachpaket installiert?");
        }
    }

    public void Stop()
    {
        if (recognizer == null) return;
        PhraseRecognitionSystem.OnError -= OnError;
        recognizer.OnPhraseRecognized -= OnPhrase;
        if (recognizer.IsRunning) recognizer.Stop();
        recognizer.Dispose();
        recognizer = null;
    }

    void OnPhrase(PhraseRecognizedEventArgs args) => WordRecognized?.Invoke(args.text);

    void OnError(SpeechError error)
    {
        Status = "Sprache: FEHLER " + error + " (P = Pause, R/F = von Hand)";
        Debug.LogWarning("VoiceRecognizer: Fehler der Spracherkennung: " + error);
    }
#else
    public void StartListening(string[] words)
    {
        Status = "Sprache: Simulation (R = richtig, F = Fehler)";
        Debug.Log("VoiceRecognizer: Mac-Simulationsmodus für Sprache aktiv – keine Windows-Spracherkennung. " +
                  "Taste R = Wort richtig, Taste F = Wortfehler.");
    }

    public void Stop() { }
#endif
}
