using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;
using JointType = Windows.Kinect.JointType;
using TrackingState = Windows.Kinect.TrackingState;

/// <summary>
/// CAVE-RUNNER – Steuerung über das Kinect-Tracking der CAVE. Gehört auf dasselbe Objekt wie <see cref="CaveRunner"/>.
///
/// GESTEN (Person steht in der CAVE und schaut zur Frontwand)
///   Arm über den Kopf heben und kurz halten  = Runde starten / neu starten
///   Schritt oder Neigen nach links / rechts  = linke / rechte Spur, zurück zur Mitte = mittlere Spur
///   Kniebeuge + schnell aufrichten, oder hüpfen = Springen
///   tief in die Hocke                         = Ducken (solange man unten bleibt)
///
/// Beim Start der Runde wird die Standposition gemerkt (Mitte + Körpergröße), alle Gesten zählen relativ dazu.
/// Also: gerade hinstellen, Arm heben, stehen bleiben bis der Countdown läuft. Taste C merkt die Position neu.
///
/// Gelesen werden die Skelettdaten (Kopf, Hände) der Person, deren Kopf auch die Perspektive der CAVE steuert.
/// Die Wörter bewertet die Spracherkennung in CaveRunner (Windows) oder die Betreuung mit den Tasten R / F.
/// </summary>
[RequireComponent(typeof(CaveRunner))]
public class CaveRunnerTracking : MonoBehaviour
{
    [Header("CAVE (leer = automatisch suchen)")]
    public VirtualEnvironment environment;
    public KinectTracker tracker;

    [Header("Start-Geste")]
    [Tooltip("So weit (m) muss eine Hand über dem Kopf sein.")]
    public float handAboveHead = 0.1f;
    [Tooltip("So lange (s) muss der Arm oben bleiben.")]
    public float startHoldTime = 1f;
    [Tooltip("Pause (s) nach dem Ende einer Runde, bevor die Start-Geste wieder zählt.")]
    public float restartDelay = 2f;

    [Header("Spur, Springen, Ducken")]
    [Tooltip("So weit (m) muss der Kopf zur Seite, damit die Spur wechselt.")]
    public float leanThreshold = 0.15f;
    [Tooltip("So weit (m) muss der Kopf unter die Standhöhe, um zu ducken.")]
    public float duckThreshold = 0.35f;
    [Tooltip("Mindest-Tiefe (m) der Kniebeuge vor dem Sprung.")]
    public float jumpSquatDepth = 0.08f;
    [Tooltip("So schnell (m/s) muss der Kopf danach nach oben, damit es als Sprung zählt. Der Wert \"max\" in der Anzeige zeigt, was beim letzten Versuch erreicht wurde.")]
    public float jumpUpSpeed = 0.7f;
    [Tooltip("So weit (m) über der Standhöhe zählt der Kopf als echter Sprung (Hüpfer ohne Kniebeuge).")]
    public float jumpRise = 0.07f;

    [Tooltip("Kopf-Werte in der Anzeige einblenden (zum Einstellen der Schwellen).")]
    public bool showDebug = true;

    /// <summary>Wird gerade eine Person in der CAVE erkannt?</summary>
    public bool HasPlayer { get; private set; }

    CaveRunner runner;
    VirtualEyeTracking eyeTracking;
    KinectManager manager;

    bool simulated, simulatedHandRaised;
    Vector3 simulatedHead;

    Vector3 headPos, standPos;   // Kopf geglättet / beim Start gemerkt, in CAVE-Koordinaten (x = rechts, y = hoch)
    float headVelY, squatTimer, holdTimer, gestureLockedUntil;
    float peakVelY, peakTimer;   // höchste Geschwindigkeit nach oben der letzten Sekunden (nur Anzeige)

    // Die letzten ungeglätteten Kopfhöhen – daraus wird die Geschwindigkeit nach oben berechnet.
    const int HistorySize = 24;
    const float VelocityWindow = 0.12f; // Sekunden
    readonly float[] historyTime = new float[HistorySize], historyY = new float[HistorySize];
    int historyNext, historyCount;
    bool calibrated, gestureArmed;
    int zone = 1;

    void Awake()
    {
        runner = GetComponent<CaveRunner>();
        if (environment == null) environment = FindObjectOfType<VirtualEnvironment>();
        if (environment == null)
        {
            // Test am PC ohne CAVE: nur Tastatur.
            enabled = false;
            return;
        }
        eyeTracking = environment.GetComponent<VirtualEyeTracking>();
        if (tracker == null && eyeTracking != null) tracker = eyeTracking.tracker;
        if (tracker == null) tracker = environment.GetComponentInChildren<KinectTracker>(true);
        if (tracker != null) manager = tracker.GetComponent<KinectManager>();
        else Debug.LogWarning("CaveRunnerTracking: Kein Kinect Tracker im CAVE-Prefab gefunden – nur Tastatur.", this);

        runner.StateChanged += OnStateChanged;
    }

    void OnDestroy()
    {
        if (runner != null) runner.StateChanged -= OnStateChanged;
    }

    void OnStateChanged(CaveRunner.State state)
    {
        if (state == CaveRunner.State.Countdown) Calibrate();
        if (state == CaveRunner.State.Won || state == CaveRunner.State.Lost)
        {
            // Wer am Ende jubelt, soll nicht sofort die nächste Runde starten.
            gestureArmed = false;
            gestureLockedUntil = Time.time + restartDelay;
        }
    }

    /// <summary>Aktuelle Kopfposition als "gerade stehen, mittlere Spur" merken.</summary>
    public void Calibrate()
    {
        calibrated = HasPlayer;
        if (!HasPlayer) return; // wird nachgeholt, sobald jemand erkannt wird
        standPos = headPos;
        zone = 1;
        squatTimer = 0f;
        peakVelY = 0f;
    }

    /// <summary>Test ohne Kinect: eine Person vorgeben (Kopfposition in CAVE-Koordinaten, Meter).</summary>
    public void Simulate(Vector3 head, bool handRaised = false)
    {
        simulated = true;
        simulatedHead = head;
        simulatedHandRaised = handRaised;
    }

    public void StopSimulation() => simulated = false;

    void Update()
    {
        if (runner.Paused) return; // Pause (Taste P): keine Gesten auswerten
        float dt = Time.deltaTime;
        bool wasPresent = HasPlayer;
        HasPlayer = ReadBody(out Vector3 head, out bool handRaised);

        if (!HasPlayer)
        {
            holdTimer = 0f;
            headVelY = 0f;
            runner.InfoLine = "";
            runner.StartHint = SensorFound() ? "Bitte in die Mitte der CAVE stellen" : "Keine Kinect gefunden\nEnter = Start";
            return;
        }

        // Kopfposition leicht glätten (die Kinect zittert und liefert nur 30 Werte pro Sekunde).
        // Seitlich etwas stärker (ruhige Spur), in der Höhe nur wenig – sonst geht ein kurzer Hüpfer verloren.
        if (!wasPresent) { headPos = head; headVelY = 0f; historyCount = 0; }
        float side = 1f - Mathf.Exp(-15f * dt), up = 1f - Mathf.Exp(-30f * dt);
        headPos = new Vector3(Mathf.Lerp(headPos.x, head.x, side), Mathf.Lerp(headPos.y, head.y, up), Mathf.Lerp(headPos.z, head.z, side));
        // Geschwindigkeit direkt aus den ungeglätteten Werten der letzten Zehntelsekunde (nicht aus der geglätteten Kurve,
        // die bei einem Sprung viel zu langsam aussieht).
        headVelY = VelocityUp(Time.unscaledTime, head.y);
        if (headVelY > peakVelY) { peakVelY = headVelY; peakTimer = 2f; }
        else if ((peakTimer -= dt) <= 0f) peakVelY = Mathf.Max(headVelY, 0f);
        if (!calibrated) Calibrate();

        if (runner.keyboardInput && Input.GetKeyDown(KeyCode.C)) { Calibrate(); runner.ShowAction("Kalibriert"); }

        if (runner.IsPlaying) ReadMoves(dt);
        else ReadStartGesture(dt, handRaised);

        runner.InfoLine = showDebug
            ? $"Kopf  seitlich: {headPos.x - standPos.x:+0.00;-0.00} m   Höhe: {headPos.y - standPos.y:+0.00;-0.00} m   hoch: {headVelY:+0.0;-0.0} m/s (max {peakVelY:0.0})"
            : "";
    }

    void ReadStartGesture(float dt, bool handRaised)
    {
        // Der Arm muss nach dem Ende einer Runde erst einmal unten gewesen sein.
        if (!handRaised) gestureArmed = true;
        bool holding = handRaised && gestureArmed && Time.time >= gestureLockedUntil;
        holdTimer = holding ? holdTimer + dt : 0f;

        string hint = "Arm über den Kopf heben = Start";
        if (holdTimer > 0f)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(holdTimer / Mathf.Max(startHoldTime, 0.01f) * 5f), 0, 5);
            hint += "\n[" + new string('#', steps) + new string('-', 5 - steps) + "]";
        }
        runner.StartHint = hint;

        if (holdTimer >= startHoldTime)
        {
            holdTimer = 0f;
            gestureArmed = false;
            runner.StartGame();
        }
    }

    void ReadMoves(float dt)
    {
        // Spur: links von der Standposition = links, Mitte = Mitte, rechts = rechts.
        // Zur Seite ab leanThreshold, zurück in die Mitte schon ab 70 % davon (Puffer gegen Flackern) –
        // man muss also nicht wieder exakt auf dem Startpunkt stehen.
        // Nur beim Wechsel der Zone umschalten -> die Tastatur funktioniert parallel weiter.
        float dx = headPos.x - standPos.x;
        int newZone = dx < -leanThreshold ? 0 : dx > leanThreshold ? 2
                    : Mathf.Abs(dx) < leanThreshold * 0.7f ? 1 : zone;
        if (newZone != zone) { zone = newZone; runner.SetLane(zone); }

        float dy = standPos.y - headPos.y; // > 0 = tiefer als normal

        // Standhöhe langsam nachführen, solange die Person ruhig und ungefähr aufrecht steht.
        // (Beim Start mit erhobenem Arm steht man oft etwas gestreckter als danach.)
        if (Mathf.Abs(dy) < 0.06f && Mathf.Abs(headVelY) < 0.15f)
        {
            standPos.y = Mathf.Lerp(standPos.y, headPos.y, 1f - Mathf.Exp(-dt / 0.7f));
            dy = standPos.y - headPos.y;
        }

        // Springen, zwei Wege:
        //  1) Kniebeuge und kurz danach schnell hoch
        //  2) Hüpfer: der Kopf ist über der Standhöhe und bewegt sich noch nach oben
        if (dy > jumpSquatDepth) squatTimer = 0.8f;
        else if (squatTimer > 0f) squatTimer -= dt;
        bool squatJump = squatTimer > 0f && headVelY > jumpUpSpeed;
        bool hop = dy < -jumpRise && headVelY > 0.2f;
        if (squatJump || hop)
        {
            squatTimer = 0f;
            runner.Jump();
        }

        // Ducken: solange der Kopf tief ist (aber nicht, während man gerade hochschnellt)
        if (dy > duckThreshold && headVelY < 0.5f) runner.Duck();
    }

    /// <summary>Neue Kopfhöhe merken und die Geschwindigkeit nach oben (m/s) über die letzten ~0,12 s liefern.</summary>
    float VelocityUp(float now, float y)
    {
        historyTime[historyNext] = now;
        historyY[historyNext] = y;
        historyNext = (historyNext + 1) % HistorySize;
        historyCount = Mathf.Min(historyCount + 1, HistorySize);

        // Ältesten Wert suchen, der noch im Zeitfenster liegt
        float oldestTime = now, oldestY = y;
        for (int i = 0; i < historyCount; i++)
        {
            if (now - historyTime[i] <= VelocityWindow && historyTime[i] < oldestTime)
            {
                oldestTime = historyTime[i];
                oldestY = historyY[i];
            }
        }
        float span = now - oldestTime;
        return span >= 0.04f ? (y - oldestY) / span : headVelY; // zu wenig Daten -> alten Wert behalten
    }

    // =====================================================================
    //  Skelettdaten
    // =====================================================================

    /// <summary>Kopfposition (CAVE-Koordinaten) der Person in der CAVE und ob sie eine Hand über den Kopf hebt.</summary>
    bool ReadBody(out Vector3 head, out bool handRaised)
    {
        head = simulatedHead;
        handRaised = simulatedHandRaised;
        if (simulated) return true;

        var actor = FindPlayer();
        if (actor == null) return false;

        // Die Gelenke direkt aus dem Kinect-Bild nehmen (Koordinaten relativ zum Sensor) und in die CAVE umrechnen.
        // Die "Virtual Eyes" wären stark geglättet und damit zu träge für Sprünge.
        var joints = actor.bodyFrame.joints;
        var headJoint = joints[(int)JointType.Head];
        head = headJoint.trackingState != TrackingState.NotTracked
            ? ToCave(headJoint.position)
            : ToCave(joints[(int)JointType.SpineShoulder].position) + Vector3.up * 0.3f;

        float limit = head.y + handAboveHead;
        handRaised = IsAbove(joints[(int)JointType.HandLeft], limit) || IsAbove(joints[(int)JointType.HandRight], limit);
        return true;
    }

    bool IsAbove(KinectJoint joint, float height) =>
        joint.trackingState != TrackingState.NotTracked && ToCave(joint.position).y > height;

    Vector3 ToCave(Vector3 sensorPosition) =>
        environment.transform.InverseTransformPoint(tracker.transform.TransformPoint(sensorPosition));

    KinectActor FindPlayer()
    {
        if (tracker == null) return null;

        // Dieselbe Person, deren Kopf die Perspektive der CAVE steuert.
        if (eyeTracking != null && eyeTracking.isActiveAndEnabled) return eyeTracking.actor;

        // Sonst: wer am längsten in der CAVE steht.
        KinectActor best = null;
        foreach (var actor in tracker.actors)
            if (actor != null && environment.Contains(actor.bounds.center) && (best == null || actor.createdAt < best.createdAt))
                best = actor;
        return best;
    }

    bool SensorFound() => manager != null && manager.sensor != null;
}
