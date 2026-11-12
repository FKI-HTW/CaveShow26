using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;
using JointType = Windows.Kinect.JointType;
using TrackingState = Windows.Kinect.TrackingState;

/// <summary>
/// FRUIT-SORT – liest die Körperhaltung aus dem Kinect-Tracking der CAVE. Gehört auf dasselbe Objekt wie <see cref="FruitSortDemo"/>.
///
/// Gemessen wird pro Arm die Strecke Schulter -> Hand, von oben gesehen (CAVE-Koordinaten: x = rechts, z = zur Vorderwand):
///   Arm gestreckt = die Hand ist waagerecht mindestens <see cref="armReach"/> von der Schulter entfernt.
///   Drehung       = Richtung, in die die gestreckten Arme zeigen (0° = Vorderwand, -90° = linke Wand, +90° = rechte Wand).
///
/// Die Drehung kommt bewusst aus den Armen und nicht aus der Schulterlinie: Steht die Person seitlich zur Kinect,
/// ist die hintere Schulter verdeckt, der vordere gestreckte Arm aber gut zu sehen.
///
/// Gelesen wird die Person, deren Kopf auch die Perspektive der CAVE steuert.
/// </summary>
[RequireComponent(typeof(FruitSortDemo))]
public class FruitSortTracking : MonoBehaviour
{
    [Header("CAVE (leer = automatisch suchen)")]
    public VirtualEnvironment environment;
    public KinectTracker tracker;

    [Header("Arme")]
    [Tooltip("So weit (m) muss die Hand waagerecht von der Schulter weg sein, damit der Arm als gestreckt zählt.")]
    public float armReach = 0.35f;
    [Tooltip("Glättung der Drehung (höher = direkter, niedriger = ruhiger).")]
    public float yawSmoothing = 12f;

    /// <summary>Werte der letzten Messung als Text (zum Einstellen der Schwellen).</summary>
    public string DebugText { get; private set; } = "";

    /// <summary>Ist eine Kinect angeschlossen?</summary>
    public bool SensorFound => manager != null && manager.sensor != null;

    VirtualEyeTracking eyeTracking;
    KinectManager manager;
    float smoothedYaw;
    bool hadArms;

    void Awake()
    {
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
        else Debug.LogWarning("FruitSortTracking: Kein Kinect Tracker im CAVE-Prefab gefunden – nur Tastatur.", this);
    }

    /// <summary>
    /// Haltung der Person in der CAVE: Anzahl der gestreckten Arme (0–2) und ihre Richtung in Grad.
    /// Gibt false zurück, wenn niemand erkannt wird. Einmal pro Frame aufrufen.
    /// </summary>
    public bool ReadPose(float dt, out int armsStretched, out float yaw)
    {
        armsStretched = 0;
        yaw = smoothedYaw;
        var actor = FindPlayer();
        if (actor == null)
        {
            hadArms = false;
            DebugText = "";
            return false;
        }

        Vector2 sum = Vector2.zero;
        bool leftArm = ReadArm(actor, JointType.ShoulderLeft, JointType.HandLeft, JointType.WristLeft, out Vector2 leftDir, out float leftReach);
        bool rightArm = ReadArm(actor, JointType.ShoulderRight, JointType.HandRight, JointType.WristRight, out Vector2 rightDir, out float rightReach);
        if (leftArm) { sum += leftDir; armsStretched++; }
        if (rightArm) { sum += rightDir; armsStretched++; }

        // Zeigen die Arme in verschiedene Richtungen (z. B. beide zur Seite gestreckt), zählt das nicht.
        if (armsStretched == 2 && sum.magnitude < 1.6f) armsStretched = 0;

        if (armsStretched > 0)
        {
            float measured = Mathf.Atan2(sum.x, sum.y) * Mathf.Rad2Deg;
            // Kinect zittert und liefert nur 30 Werte pro Sekunde -> leicht glätten
            smoothedYaw = hadArms ? Mathf.LerpAngle(smoothedYaw, measured, 1f - Mathf.Exp(-yawSmoothing * dt)) : measured;
        }
        hadArms = armsStretched > 0;
        yaw = smoothedYaw;

        DebugText = $"Arme: {armsStretched}   Drehung: {yaw:+0;-0;0}°   Reichweite  links: {leftReach:0.00} m   rechts: {rightReach:0.00} m";
        return true;
    }

    /// <summary>Richtung (von oben gesehen: x, z) und waagerechte Reichweite eines Arms. true = gestreckt.</summary>
    bool ReadArm(KinectActor actor, JointType shoulderType, JointType handType, JointType wristType, out Vector2 direction, out float reach)
    {
        direction = Vector2.zero;
        reach = 0f;
        var shoulder = actor.GetJoint(shoulderType);
        var hand = actor.GetJoint(handType);
        if (hand.trackingState == TrackingState.NotTracked) hand = actor.GetJoint(wristType);
        if (shoulder.trackingState == TrackingState.NotTracked || hand.trackingState == TrackingState.NotTracked) return false;

        // Die Gelenke des Actors sind Weltkoordinaten -> in die CAVE umrechnen
        Vector3 arm = environment.transform.InverseTransformPoint(hand.position)
                    - environment.transform.InverseTransformPoint(shoulder.position);
        var flat = new Vector2(arm.x, arm.z);
        reach = flat.magnitude;
        if (reach < armReach) return false;
        direction = flat / reach;
        return true;
    }

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
}
