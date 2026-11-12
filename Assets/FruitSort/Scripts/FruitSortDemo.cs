using System.Collections.Generic;
using UnityEngine;
using HTW.CAVE;

/// <summary>
/// FRUIT-SORT – einfache Demo zum Testen von "Block fangen und abgeben" in der CAVE.
/// Die Körperhaltung (Arme, Drehung) liest <see cref="FruitSortTracking"/> auf demselben Objekt aus der Kinect.
///
/// SZENE: Assets/FruitSort/FruitSort.unity (Menü "CaveShow > Fruit-Sort-Szene neu erstellen").
///   Sie enthält das CAVE-Prefab und ein Objekt mit FruitSortDemo + FruitSortTracking
///   und ist unter File > Build Settings eingetragen -> File > Build And Run.
///
/// ABLAUF
///   1. Auf der Vorderwand fällt langsam ein roter oder blauer Block. Es gibt immer nur einen Block.
///   2. Beide Arme nach vorne strecken. Ist der Block im Fangbereich (Rahmen), wird er gefangen.
///   3. Der gefangene Block bekommt einen weißen Rand und folgt der Richtung der Arme.
///   4. Oberkörper drehen: Rot gehört an die linke Wand, Blau an die rechte.
///   5. An der passenden Wand verschwindet der Block, die Wand blinkt grün. An der falschen bleibt er gehalten.
///   6. Danach kommt der nächste Block. Ein nicht gefangener Block fällt aus dem Bild.
///
/// ANSICHT: Sind mehrere Bildschirme angeschlossen (CAVE), wird auf die CAVE-Wände ausgegeben.
///   An einem einzelnen Bildschirm (PC, Editor) gibt es eine Übersicht von hinten auf alle drei Wände.
///   Taste V schaltet um, im Inspector lässt sich die Ansicht bei "View" fest einstellen.
///
/// TASTEN (Test ohne Kinect / solange niemand erkannt wird):
///   Leertaste halten = beide Arme strecken, ←/→ halten = Oberkörper drehen,
///   R = neuer Block, V = Ansicht, Esc = Beenden.
/// </summary>
public class FruitSortDemo : MonoBehaviour
{
    // =====================================================================
    //  Einstellungen (Inspector)
    // =====================================================================

    [Header("Block")]
    public float blockSize = 0.45f;
    [Tooltip("Fallgeschwindigkeit in Metern pro Sekunde.")]
    public float fallSpeed = 0.3f;
    [Tooltip("Pause (s) nach einem verpassten Block, bevor der nächste erscheint.")]
    public float spawnDelay = 0.8f;

    [Header("Fangbereich auf der Vorderwand (Höhe über dem Boden in m)")]
    public float catchZoneBottom = 0.95f;
    public float catchZoneTop = 1.55f;
    [Tooltip("So weit (Grad) dürfen die Arme beim Fangen von der Vorderwand weg zeigen.")]
    public float catchAngle = 30f;

    [Header("Abgabe")]
    [Tooltip("Ab dieser Drehung (Grad) gilt der Block als an der Seitenwand. 45 = Ecke, 90 = Mitte der Seitenwand.")]
    public float deliverAngle = 55f;
    [Tooltip("So lange (s) muss die Drehung gehalten werden (gegen Zittern der Kinect).")]
    public float deliverHoldTime = 0.25f;
    [Tooltip("So viele Arme müssen beim Halten gestreckt erkannt werden. Seitlich zur Kinect ist ein Arm oft verdeckt, deshalb 1.")]
    [Range(1, 2)] public int armsToHold = 1;
    [Tooltip("Dauer (s) des Signals nach einer richtigen Abgabe.")]
    public float signalTime = 1f;

    [Header("Steuerung / Ansicht")]
    [Tooltip("Tasten zum Testen (siehe Kommentar oben).")]
    public bool keyboardInput = true;
    public enum ViewMode { Auto, Cave, Overview }
    [Tooltip("Auto = CAVE-Wände, wenn mehrere Bildschirme angeschlossen sind, sonst Übersicht (PC, Editor).")]
    public ViewMode view = ViewMode.Auto;
    [Tooltip("Erkannte Arme und Drehung einblenden (zum Einstellen der Schwellen).")]
    public bool showDebug = true;

    [Header("Material")]
    [Tooltip("Vorlage für alle Flächen. Muss als Asset in der Szene hängen, sonst fehlt der Shader im Build (alles pink).")]
    public Material colorMaterial;

    // =====================================================================
    //  Öffentlicher Zustand
    // =====================================================================

    public enum State { Falling, Held, Delivered, Pause }
    public enum BlockColor { Red, Blue }

    public State GameState { get; private set; } = State.Pause;
    public BlockColor CurrentColor { get; private set; }
    /// <summary>Richtung des gehaltenen Blocks in Grad: 0 = Vorderwand, -90 = linke Wand, +90 = rechte Wand.</summary>
    public float HeldYaw { get; private set; }
    public int Delivered { get; private set; }
    public int Missed { get; private set; }

    static readonly Color Red = new Color(0.92f, 0.16f, 0.14f), Blue = new Color(0.15f, 0.42f, 1f);
    static readonly Color Green = new Color(0.2f, 0.85f, 0.3f), Grey = new Color(0.3f, 0.32f, 0.36f);
    const float MaxYaw = 100f, TargetSize = 1.5f, FlyTime = 0.35f;

    // =====================================================================
    //  Interne Variablen
    // =====================================================================

    FruitSortTracking tracking;
    Vector3 size = VirtualEnvironment.GetDefaultDimensions(); // Breite, Höhe, Tiefe der CAVE
    Transform front, left, right;   // Wände: x = nach rechts, y = nach oben, z = in die Wand hinein
    Camera overviewCam;
    Camera[] caveCams;
    bool overview;

    Transform block, burst;
    Renderer blockBody;
    GameObject blockGlow;
    Renderer[] zoneFrame, leftFrame, rightFrame;
    TextMesh hudText, hintText, debugText, leftText, rightText;
    readonly Dictionary<Color, Material> materials = new Dictionary<Color, Material>();

    float blockY, timer, deliverTimer, keyYaw;
    Vector3 flyFrom;
    bool simulated;
    int simulatedArms;
    float simulatedYaw;

    float TargetY => (catchZoneBottom + catchZoneTop) * 0.5f;
    float TargetSide => CurrentColor == BlockColor.Red ? -1f : 1f;

    // =====================================================================
    //  Test ohne Kinect
    // =====================================================================

    /// <summary>Körperhaltung vorgeben: Anzahl gestreckter Arme (0–2) und Drehung in Grad (- = links).</summary>
    public void SimulatePose(int armsStretched, float yaw)
    {
        simulated = true;
        simulatedArms = armsStretched;
        simulatedYaw = yaw;
    }

    public void StopSimulation() => simulated = false;

    // =====================================================================
    //  Aufbau
    // =====================================================================

    void Awake()
    {
        tracking = GetComponent<FruitSortTracking>();

        // Spiel genau auf die CAVE setzen (Ursprung = Mitte des CAVE-Bodens, Blick zur Vorderwand).
        var cave = FindObjectOfType<VirtualEnvironment>();
        if (cave != null)
        {
            transform.SetPositionAndRotation(cave.transform.position, cave.transform.rotation);
            size = cave.dimensions;
            caveCams = cave.GetComponentsInChildren<Camera>(true);
        }
        SetOverview(view == ViewMode.Overview || (view == ViewMode.Auto && Display.displays.Length <= 1));

        front = BuildWall("Vorderwand", new Vector3(0, 0, size.z * 0.5f), 0f, size.x);
        left = BuildWall("Linke Wand", new Vector3(-size.x * 0.5f, 0, 0), -90f, size.z);
        right = BuildWall("Rechte Wand", new Vector3(size.x * 0.5f, 0, 0), 90f, size.z);
        Box(transform, new Vector3(0, -0.05f, 0), new Vector3(size.x + 0.8f, 0.05f, size.z + 0.8f), Mat(new Color(0.1f, 0.11f, 0.13f)));

        // Vorderwand: Fangbereich und Texte
        float zoneHeight = catchZoneTop - catchZoneBottom;
        zoneFrame = Frame(front, new Vector3(0, TargetY, 0.1f), blockSize * 2f, zoneHeight, 0.04f, Mat(Grey));
        hudText = MakeText(front, new Vector3(0, size.y - 0.2f, 0), 0.016f, Color.white);
        hintText = MakeText(front, new Vector3(0, 0.6f, 0), 0.018f, new Color(1f, 0.85f, 0.3f));
        debugText = MakeText(front, new Vector3(0, 0.2f, 0), 0.01f, new Color(0.6f, 0.9f, 1f));

        // Seitenwände: Ziel für Rot (links) und Blau (rechts)
        leftFrame = Frame(left, new Vector3(0, TargetY, 0.1f), TargetSize, TargetSize, 0.07f, Mat(Red));
        rightFrame = Frame(right, new Vector3(0, TargetY, 0.1f), TargetSize, TargetSize, 0.07f, Mat(Blue));
        MakeText(left, new Vector3(0, TargetY + TargetSize * 0.5f + 0.2f, 0), 0.022f, Red).text = "ROT";
        MakeText(right, new Vector3(0, TargetY + TargetSize * 0.5f + 0.2f, 0), 0.022f, Blue).text = "BLAU";
        leftText = MakeText(left, new Vector3(0, TargetY, 0), 0.02f, Color.white);
        rightText = MakeText(right, new Vector3(0, TargetY, 0), 0.02f, Color.white);

        // Signal bei richtiger Abgabe (grüne Fläche, wird an die passende Wand gehängt)
        burst = Box(left, Vector3.zero, Vector3.one, Mat(Green)).transform;
        burst.gameObject.SetActive(false);

        // Der Block (es gibt nur einen, er wird wiederverwendet)
        block = new GameObject("Block").transform;
        block.SetParent(transform, false);
        blockBody = Box(block, Vector3.zero, new Vector3(blockSize, blockSize, 0.08f), Mat(Red)).GetComponent<Renderer>();
        blockGlow = Box(block, new Vector3(0, 0, 0.06f), new Vector3(blockSize * 1.3f, blockSize * 1.3f, 0.02f), Mat(Color.white));

        SpawnBlock();
    }

    void Start()
    {
        foreach (var cam in FindObjectsOfType<Camera>())
        {
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
        }
    }

    /// <summary>Wand mit dunklem Hintergrund. Alles Weitere wird in Wand-Koordinaten daran gehängt.</summary>
    Transform BuildWall(string name, Vector3 position, float angle, float width)
    {
        var wall = new GameObject(name).transform;
        wall.SetParent(transform, false);
        wall.localPosition = position;
        wall.localRotation = Quaternion.Euler(0, angle, 0);
        Box(wall, new Vector3(0, size.y * 0.5f, 0.4f), new Vector3(width + 0.8f, size.y + 0.8f, 0.05f), Mat(new Color(0.06f, 0.07f, 0.09f)));
        return wall;
    }

    /// <summary>Zwischen Übersicht (ein Bildschirm) und Ausgabe auf die CAVE-Wände umschalten.</summary>
    void SetOverview(bool on)
    {
        overview = on || caveCams == null; // ohne CAVE gibt es nur die Übersicht
        if (overview && overviewCam == null)
        {
            if (caveCams == null) overviewCam = Camera.main;
            if (overviewCam == null)
            {
                overviewCam = new GameObject("Overview Camera").AddComponent<Camera>();
                if (FindObjectOfType<AudioListener>() == null) overviewCam.gameObject.AddComponent<AudioListener>();
            }
            // Von hinten in die CAVE schauen, so dass Vorderwand und beide Seitenwände zu sehen sind.
            overviewCam.transform.SetPositionAndRotation(
                transform.TransformPoint(new Vector3(0, size.y * 0.55f, -size.z * 0.5f - 2f)), transform.rotation);
            overviewCam.depth = 100f; // liegt über den CAVE-Kameras
            overviewCam.fieldOfView = 55f;
            overviewCam.clearFlags = CameraClearFlags.SolidColor;
            overviewCam.backgroundColor = Color.black;
        }
        if (overviewCam != null) overviewCam.enabled = overview;
        if (caveCams != null) foreach (var cam in caveCams) cam.enabled = !overview;
    }

    // =====================================================================
    //  Spielablauf
    // =====================================================================

    /// <summary>Neuen Block oben an der Vorderwand starten (ersetzt den aktuellen).</summary>
    public void SpawnBlock()
    {
        CurrentColor = Random.value < 0.5f ? BlockColor.Red : BlockColor.Blue;
        blockY = size.y + blockSize;
        HeldYaw = 0f;
        deliverTimer = 0f;
        burst.gameObject.SetActive(false);
        block.gameObject.SetActive(true);
        blockGlow.SetActive(false);
        blockBody.sharedMaterial = Mat(CurrentColor == BlockColor.Red ? Red : Blue);
        PlaceBlock(0f, blockY, 1f);
        GameState = State.Falling;
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape)) Application.Quit();
        if (keyboardInput)
        {
            if (Input.GetKeyDown(KeyCode.V)) SetOverview(!overview);
            if (Input.GetKeyDown(KeyCode.R)) SpawnBlock();
        }

        float dt = Time.deltaTime;
        bool tracked = ReadPose(dt, out int arms, out float yaw);
        bool ready = arms == 2 && Mathf.Abs(yaw) <= catchAngle; // beide Arme zur Vorderwand gestreckt
        float wrongSide = 0f;

        switch (GameState)
        {
            case State.Falling:
                blockY -= fallSpeed * dt;
                bool catchable = blockY >= catchZoneBottom;
                if (ready && catchable && blockY <= catchZoneTop)
                {
                    GameState = State.Held;
                    blockGlow.SetActive(true);
                }
                else if (blockY < -blockSize)
                {
                    // Nicht gefangen: aus dem Bild gefallen
                    Missed++;
                    block.gameObject.SetActive(false);
                    timer = 0f;
                    GameState = State.Pause;
                }
                else if (!catchable)
                    blockBody.sharedMaterial = Mat((CurrentColor == BlockColor.Red ? Red : Blue) * 0.35f); // verpasst
                PlaceBlock(0f, blockY, 1f);
                break;

            case State.Held:
                // Der Block folgt der Richtung der Arme. Sind sie unten, bleibt er, wo er zuletzt war.
                if (arms >= armsToHold)
                    HeldYaw = Mathf.Lerp(HeldYaw, Mathf.Clamp(yaw, -MaxYaw, MaxYaw), 1f - Mathf.Exp(-10f * dt));
                float side = HeldYaw <= -deliverAngle ? -1f : HeldYaw >= deliverAngle ? 1f : 0f;
                if (side != 0f && side != TargetSide) wrongSide = side;
                deliverTimer = side == TargetSide ? deliverTimer + dt : 0f;
                PlaceBlock(HeldYaw, blockY, 1f + 0.05f * Mathf.Sin(Time.time * 6f));

                if (deliverTimer >= deliverHoldTime)
                {
                    // Richtige Wand: Block fliegt ins Ziel und verschwindet, die Wand blinkt grün
                    Delivered++;
                    flyFrom = block.localPosition;
                    burst.SetParent(TargetSide < 0f ? left : right, false);
                    burst.localPosition = new Vector3(0, TargetY, 0.2f);
                    burst.localScale = Vector3.zero;
                    burst.gameObject.SetActive(true);
                    timer = 0f;
                    GameState = State.Delivered;
                }
                break;

            case State.Delivered:
                timer += dt;
                float fly = Mathf.Clamp01(timer / FlyTime);
                block.localPosition = Vector3.Lerp(flyFrom, WallPoint(TargetSide * 90f, TargetY), fly);
                block.localScale = Vector3.one * (1f - fly);
                float pulse = TargetSize * Mathf.Sin(Mathf.PI * Mathf.Clamp01(timer / signalTime));
                burst.localScale = new Vector3(pulse, pulse, 0.02f);
                if (timer >= signalTime) SpawnBlock();
                break;

            case State.Pause:
                timer += dt;
                if (timer >= spawnDelay) SpawnBlock();
                break;
        }

        // Anzeige
        bool delivered = GameState == State.Delivered;
        SetFrame(zoneFrame, Mat(ready && GameState == State.Falling ? Color.white : Grey));
        SetFrame(leftFrame, Mat(delivered && TargetSide < 0f ? Green : Red));
        SetFrame(rightFrame, Mat(delivered && TargetSide > 0f ? Green : Blue));
        leftText.text = delivered && TargetSide < 0f ? "RICHTIG!" : wrongSide < 0f ? "Falsche Wand\nBlau gehört nach rechts" : "";
        rightText.text = delivered && TargetSide > 0f ? "RICHTIG!" : wrongSide > 0f ? "Falsche Wand\nRot gehört nach links" : "";
        hudText.text = $"Abgegeben: {Delivered}     Verpasst: {Missed}";
        hintText.text = Hint(tracked, arms, ready);
        debugText.text = !showDebug ? "" : tracked && !simulated ? tracking.DebugText : $"Arme: {arms}   Drehung: {yaw:+0;-0;0}°";
    }

    string Hint(bool tracked, int arms, bool ready)
    {
        if (GameState == State.Held)
        {
            if (arms < armsToHold) return "Arme ausgestreckt lassen";
            return CurrentColor == BlockColor.Red ? "<<  ROT: nach links drehen" : "BLAU: nach rechts drehen  >>";
        }
        if (GameState != State.Falling) return "";
        if (ready) return "Bereit – Block fangen";
        if (tracked) return "Beide Arme nach vorne strecken";
        if (tracking != null && tracking.SensorFound) return "Bitte in die Mitte der CAVE stellen";
        return keyboardInput ? "Keine Kinect gefunden\nLeertaste = Arme strecken, Pfeiltasten = drehen" : "Keine Kinect gefunden";
    }

    /// <summary>Körperhaltung lesen: aus der Kinect oder (solange niemand erkannt wird) von der Tastatur.</summary>
    bool ReadPose(float dt, out int arms, out float yaw)
    {
        if (simulated)
        {
            arms = simulatedArms;
            yaw = simulatedYaw;
            return true;
        }
        if (tracking != null && tracking.isActiveAndEnabled && tracking.ReadPose(dt, out arms, out yaw))
            return true;

        arms = 0;
        yaw = 0f;
        if (!keyboardInput) return false;
        float turn = 0f;
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A)) turn -= 95f;
        if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D)) turn += 95f;
        keyYaw = Mathf.MoveTowards(keyYaw, turn, 150f * dt);
        arms = Input.GetKey(KeyCode.Space) ? 2 : 0;
        yaw = keyYaw;
        return false;
    }

    // =====================================================================
    //  Hilfsfunktionen
    // =====================================================================

    void PlaceBlock(float yaw, float height, float scale)
    {
        block.localPosition = WallPoint(yaw, height);
        block.localRotation = Quaternion.Euler(0, yaw, 0);
        block.localScale = Vector3.one * scale;
    }

    /// <summary>
    /// Punkt auf den CAVE-Wänden, auf den man von der Mitte aus in Richtung <paramref name="yaw"/> schaut
    /// (0 = Mitte der Vorderwand, ±90 = Mitte der Seitenwände), knapp hinter der Wand.
    /// </summary>
    Vector3 WallPoint(float yaw, float height)
    {
        float halfWidth = size.x * 0.5f, halfDepth = size.z * 0.5f;
        float rad = Mathf.Clamp(yaw, -MaxYaw, MaxYaw) * Mathf.Deg2Rad;
        float corner = Mathf.Atan2(halfWidth, halfDepth);
        Vector3 point = Mathf.Abs(rad) <= corner
            ? new Vector3(Mathf.Tan(rad) * halfDepth, height, halfDepth)
            : new Vector3(Mathf.Sign(rad) * halfWidth, height, halfWidth / Mathf.Tan(Mathf.Abs(rad)));
        return point + new Vector3(Mathf.Sin(rad), 0, Mathf.Cos(rad)) * 0.1f;
    }

    Material Mat(Color c)
    {
        if (materials.TryGetValue(c, out var m)) return m;
        if (colorMaterial == null)
        {
            // Notlösung ohne zugewiesenes Material: funktioniert im Editor, im Build fehlt dann aber der Shader.
            colorMaterial = new Material(Shader.Find("Unlit/Color"));
            Debug.LogWarning("FruitSortDemo: Material nicht zugewiesen – Szene über \"CaveShow > Fruit-Sort-Szene neu erstellen\" erzeugen.", this);
        }
        m = new Material(colorMaterial) { color = c };
        materials[c] = m;
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

    /// <summary>Rechteckiger Rahmen aus vier Leisten.</summary>
    Renderer[] Frame(Transform parent, Vector3 center, float width, float height, float thickness, Material m)
    {
        var x = new Vector3(width * 0.5f, 0, 0);
        var y = new Vector3(0, height * 0.5f, 0);
        var horizontal = new Vector3(width + thickness, thickness, 0.02f);
        var vertical = new Vector3(thickness, height + thickness, 0.02f);
        return new[]
        {
            Box(parent, center + y, horizontal, m).GetComponent<Renderer>(),
            Box(parent, center - y, horizontal, m).GetComponent<Renderer>(),
            Box(parent, center - x, vertical, m).GetComponent<Renderer>(),
            Box(parent, center + x, vertical, m).GetComponent<Renderer>(),
        };
    }

    static void SetFrame(Renderer[] frame, Material m)
    {
        foreach (var r in frame) r.sharedMaterial = m;
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
