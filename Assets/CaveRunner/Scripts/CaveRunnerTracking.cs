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
/// Die Wörter bewertet weiterhin die Betreuung (Enter / Backspace) oder eine Spracherkennung
/// über CaveRunner.SubmitRecognizedWord.
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
    public float jumpSquatDepth = 0.12f;
    [Tooltip("So schnell (m/s) muss der Kopf danach nach oben, damit es als Sprung zählt.")]
    public float jumpUpSpeed = 0.8f;
    [Tooltip("So weit (m) über der Standhöhe zählt der Kopf als echter Sprung.")]
    public float jumpRise = 0.12f;

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

        // Kopfposition leicht glätten (die Kinect zittert und liefert nur 30 Werte pro Sekunde)
        if (!wasPresent) { headPos = head; headVelY = 0f; }
        float prevY = headPos.y;
        headPos = Vector3.Lerp(headPos, head, 1f - Mathf.Exp(-15f * dt));
        headVelY = Mathf.Lerp(headVelY, (headPos.y - prevY) / Mathf.Max(dt, 0.0001f), 1f - Mathf.Exp(-20f * dt));
        if (!calibrated) Calibrate();

        if (runner.keyboardInput && Input.GetKeyDown(KeyCode.C)) { Calibrate(); runner.ShowAction("Kalibriert"); }

        if (runner.IsPlaying) ReadMoves(dt);
        else ReadStartGesture(dt, handRaised);

        runner.InfoLine = showDebug
            ? $"Kopf  seitlich: {headPos.x - standPos.x:+0.00;-0.00} m   Höhe: {headPos.y - standPos.y:+0.00;-0.00} m   hoch: {headVelY:+0.0;-0.0} m/s"
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
        // Spur: links von der Standposition = links, Mitte = Mitte, rechts = rechts (mit etwas Puffer gegen Flackern).
        // Nur beim Wechsel der Zone umschalten -> die Tastatur funktioniert parallel weiter.
        float dx = headPos.x - standPos.x;
        int newZone = dx < -leanThreshold ? 0 : dx > leanThreshold ? 2
                    : Mathf.Abs(dx) < leanThreshold * 0.5f ? 1 : zone;
        if (newZone != zone) { zone = newZone; runner.SetLane(zone); }

        float dy = standPos.y - headPos.y; // > 0 = tiefer als normal

        // Springen: Kniebeuge und kurz danach schnell hoch – oder der Kopf ist deutlich über der Standhöhe (echter Sprung)
        if (dy > jumpSquatDepth) squatTimer = 0.7f;
        else if (squatTimer > 0f) squatTimer -= dt;
        if ((headVelY > jumpUpSpeed && squatTimer > 0f) || dy < -jumpRise)
        {
            squatTimer = 0f;
            runner.Jump();
        }

        // Ducken: solange der Kopf tief ist (aber nicht, während man gerade hochschnellt)
        if (dy > duckThreshold && headVelY < 0.5f) runner.Duck();
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
