using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// CAVE-RUNNER – Demo-Umgebung (First Person), alles in einer Datei.
///
/// BENUTZUNG
///   1. Leeres GameObject in die Szene, dieses Skript draufziehen.
///   2. Für die CAVE: CAVE-Prefab (Samples/CAVE/.../Example CAVE Setup/Prefabs/CAVE) in die Szene ziehen,
///      Name muss "CAVE" bleiben (oder im Inspector bei "Cave Rig" eintragen).
///      Ohne CAVE wird automatisch eine normale First-Person-Kamera zum Testen am PC benutzt.
///   3. Szene unter File > Build Settings hinzufügen -> File > Build And Run.
///
/// PRINZIP: Die Person steht still in der Mitte der CAVE (z = 0), die Welt fährt auf sie zu (Laufband).
///          Beim Spurwechsel wird der CAVE-Rig seitlich verschoben.
///
/// TASTEN (Test): Enter/Leertaste = Start, ←/→ = Spur, Leertaste/↑ = Springen, ↓ = Ducken,
///                Enter = Wort richtig, Backspace = Wort falsch, R = Neustart, Esc = Beenden.
///
/// SCHNITTSTELLEN
///   Tracking:       runner.MoveLeft(), MoveRight(), Jump(), Duck()
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

    [Header("Wörter")]
    public string[] words = { "SMALL", "HOUSE", "GREEN", "WATER", "APPLE", "TIGER", "CLOUD", "LIGHT", "STONE", "RIVER" };
    public float wordSpawnZ = 45f;
    [Tooltip("Lesegrenze: bis hier muss das Wort gelesen sein.")]
    public float readLimitZ = 8f;
    [Tooltip("Tafeln sind langsamer als die Hindernisse.")]
    public float wordSpeed = 4.5f;
    public float wordHeight = 2.6f;
    public float[] pauseOptions = { 0.5f, 0.5f, 3f, 10f, 15f, 20f };

    [Header("CAVE / Kamera")]
    [Tooltip("Leer = Objekt mit dem Namen \"CAVE\" wird gesucht.")]
    public Transform caveRig;
    public bool caveFollowsLane = true;
    public float eyeHeight = 1.7f;        // nur PC-Kamera
    public float duckEyeHeight = 1.0f;    // nur PC-Kamera
    public float jumpHeight = 0.9f;       // nur PC-Kamera
    public bool keyboardInput = true;

    // =====================================================================
    //  Öffentlicher Zustand + Events
    // =====================================================================

    public enum State { Start, Countdown, Running, Won, Lost }
    public State GameState { get; private set; }
    public int CurrentLane { get; private set; } = 1;
    public int CorrectWords { get; private set; }
    public int WordErrors { get; private set; }
    public int Hits { get; private set; }
    public float TimeLeft => Mathf.Max(0f, roundDuration - roundTime);
    public string CurrentWord => activeBoard?.word;

    public event Action<string> WordAppeared;
    public event Action<string, bool> WordFinished; // (Wort, richtig?)
    public event Action<State> StateChanged;

    static readonly float[] Lanes = { -1.6f, 0f, 1.6f };
    const float DespawnZ = -12f, SpawnZ = 80f, JumpDuration = 0.8f, DuckDuration = 0.9f;

    enum ObstacleType { Box, Hurdle, Bar }
    class Obstacle { public Transform t; public ObstacleType type; public int lane; public bool passed; }
    class Board { public Transform t; public string word; public Renderer frame; public bool done; }

    // =====================================================================
    //  Interne Variablen
    // =====================================================================

    Transform world, rig;
    Camera pcCam;
    Vector3 caveStartPos;
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
    float laneX, jumpTime = -1f, jumpOffset, duckTimer, duckAmount, actionTimer, shake;

    bool ControlsEnabled => GameState == State.Countdown || GameState == State.Running;
    bool IsDucking => duckTimer > 0f;
    bool IsAirborne => jumpTime >= 0f && jumpOffset > 0.15f;

    // =====================================================================
    //  Steuerung (für Tracking)
    // =====================================================================

    public void MoveLeft() { if (ControlsEnabled && CurrentLane > 0) { CurrentLane--; ShowAction("Links"); } }
    public void MoveRight() { if (ControlsEnabled && CurrentLane < 2) { CurrentLane++; ShowAction("Rechts"); } }

    public void Jump()
    {
        if (!ControlsEnabled || jumpTime >= 0f) return;
        duckTimer = 0f; jumpTime = 0f; ShowAction("Sprung");
    }

    public void Duck()
    {
        if (!ControlsEnabled) return;
        jumpTime = -1f; jumpOffset = 0f; duckTimer = DuckDuration; ShowAction("Ducken");
    }

    // =====================================================================
    //  Worterkennung
    // =====================================================================

    /// <summary>Erkanntes Wort übergeben. Gibt true zurück, wenn es zur aktuellen Tafel passt.</summary>
    public bool SubmitRecognizedWord(string recognized)
    {
        if (GameState != State.Running || activeBoard == null || string.IsNullOrEmpty(recognized)) return false;
        bool match = string.Equals(recognized.Trim(), activeBoard.word, StringComparison.OrdinalIgnoreCase);
        if (match) FinishWord(true);
        return match;
    }

    public void StartGame()
    {
        if (ControlsEnabled) return;
        foreach (var o in obstacles) Destroy(o.t.gameObject);
        foreach (var b in boards) Destroy(b.t.gameObject);
        obstacles.Clear(); boards.Clear(); activeBoard = null;
        CorrectWords = WordErrors = Hits = wordsShown = 0;
        roundTime = 0f; nextWordTime = 2f; nextObstacleIn = 30f;
        CurrentLane = 1; laneX = 0f; jumpTime = -1f; jumpOffset = 0f; duckTimer = 0f;

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
        world = new GameObject("World (bewegt sich)").transform;
        world.SetParent(transform, false);
        rig = new GameObject("Rig (folgt Spur)").transform;
        rig.SetParent(transform, false);

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

        // Text in der 3D-Welt (sichtbar auf den CAVE-Wänden), hängt am Rig
        hudText = MakeText(rig, new Vector3(0, 2.9f, 6f), 0.03f, Color.white);
        bigText = MakeText(rig, new Vector3(0, 1.8f, 6f), 0.05f, new Color(1f, 0.85f, 0.3f));
        actionText = MakeText(rig, new Vector3(0, 1.0f, 4f), 0.025f, new Color(0.6f, 0.9f, 1f));

        SetupCamera();
        SetState(State.Start);
    }

    void Start()
    {
        foreach (var cam in FindObjectsOfType<Camera>())
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = RenderSettings.fogColor;
        }
    }

    void SetupCamera()
    {
        if (caveRig == null) { var go = GameObject.Find("CAVE"); if (go != null) caveRig = go.transform; }
        if (caveRig != null) { caveStartPos = caveRig.position; return; }

        pcCam = Camera.main;
        if (pcCam == null)
        {
            pcCam = new GameObject("PC Camera (First Person)").AddComponent<Camera>();
            pcCam.tag = "MainCamera";
            pcCam.gameObject.AddComponent<AudioListener>();
        }
        pcCam.fieldOfView = 75f;
        pcCam.nearClipPlane = 0.05f;
        pcCam.transform.rotation = Quaternion.Euler(4f, 0f, 0f);
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
        t.SetParent(transform, false);
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
        t.SetParent(transform, false);
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
        switch (s)
        {
            case State.Start: bigText.text = "CAVE-RUNNER\n\nEnter / Leertaste = Start"; break;
            case State.Running: bigText.text = ""; break;
            case State.Won: bigText.text = $"GEWONNEN!\n{CorrectWords} von {words.Length} richtig\n\nR = Neustart"; break;
            case State.Lost:
                bigText.text = (WordErrors >= maxWordErrors ? "VERLOREN!\n3 Wortfehler" : $"VERLOREN!\nnur {CorrectWords} von {words.Length} richtig") + "\n\nR = Neustart";
                break;
        }
        StateChanged?.Invoke(s);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        if (keyboardInput) ReadKeyboard();
        float dt = Time.deltaTime;

        if (GameState == State.Countdown)
        {
            countdown -= dt;
            bigText.text = countdown > 0f ? Mathf.CeilToInt(countdown).ToString() : "LOS!";
            if (countdown <= 0f) SetState(State.Running);
        }
        else if (GameState == State.Running)
        {
            roundTime += dt;
            MoveWorld(speed * dt);
            UpdateWords(dt);
            if (GameState == State.Running && TimeLeft <= 0f)
                SetState(CorrectWords >= wordsToWin ? State.Won : State.Lost);
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

        hudText.text = GameState == State.Start ? "" :
            $"Zeit: {Mathf.CeilToInt(TimeLeft)} s     Richtig: {CorrectWords}/{words.Length}     Fehler: {WordErrors}/{maxWordErrors}     Treffer: {Hits}";
    }

    void LateUpdate()
    {
        // Rig (HUD), CAVE und PC-Kamera folgen der Spur
        rig.localPosition = new Vector3(laneX, 0, 0);
        if (caveRig != null && caveFollowsLane)
            caveRig.position = caveStartPos + transform.TransformVector(new Vector3(laneX, 0, 0));
        if (pcCam != null)
        {
            float y = Mathf.Lerp(eyeHeight, duckEyeHeight, duckAmount) + jumpOffset;
            Vector3 wobble = Vector3.zero;
            if (shake > 0f) { shake -= Time.deltaTime; wobble = UnityEngine.Random.insideUnitSphere * 0.06f; }
            pcCam.transform.position = transform.TransformPoint(new Vector3(laneX, y, 0)) + wobble;
        }
    }

    void ReadKeyboard()
    {
        if (!ControlsEnabled)
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
            if (Input.GetKeyDown(KeyCode.Return)) FinishWord(true);
            else if (Input.GetKeyDown(KeyCode.Backspace)) FinishWord(false);
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

        // Hindernisse bewegen + Kollision bei z = 0 (da steht die Person)
        for (int i = obstacles.Count - 1; i >= 0; i--)
        {
            var o = obstacles[i];
            var p = o.t.localPosition;
            p.z -= move;
            o.t.localPosition = p;

            if (!o.passed && p.z <= 0f)
            {
                o.passed = true;
                bool avoided = o.type == ObstacleType.Hurdle ? IsAirborne
                             : o.type == ObstacleType.Bar ? IsDucking
                             : false;
                if (o.lane == CurrentLane && !avoided)
                {
                    Hits++;
                    shake = 0.3f;
                    ShowAction("TREFFER!", new Color(1f, 0.25f, 0.2f));
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

        if (WordErrors >= maxWordErrors) SetState(State.Lost);
    }

    // =====================================================================
    //  Hilfsfunktionen
    // =====================================================================

    void ShowAction(string text) => ShowAction(text, new Color(0.6f, 0.9f, 1f));

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
        if (baseMat == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            baseMat = tmp.GetComponent<Renderer>().sharedMaterial;
            tmp.SetActive(false);
            Destroy(tmp);
        }
        m = new Material(baseMat) { color = c };
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
