using System;
using UnityEngine;

namespace CaveRunner
{
    /// <summary>
    /// Die Spielfigur (läuft vor der Person in der CAVE).
    ///
    /// SCHNITTSTELLE FÜR TRACKING (Person 1):
    ///   player.MoveLeft()   – Körper nach links neigen
    ///   player.MoveRight()  – Körper nach rechts neigen
    ///   player.Jump()       – Kniebeuge + schnelles Aufrichten
    ///   player.Duck()       – Ducken (Rutschen)
    /// Zum Testen am PC: A/D bzw. Pfeil links/rechts, Leertaste/Pfeil hoch, Pfeil runter/S.
    /// </summary>
    public class RunnerPlayer : MonoBehaviour
    {
        [Header("Bewegung")]
        public float laneChangeSpeed = 12f;
        public float jumpHeight = 1.3f;
        public float jumpDuration = 0.8f;
        public float duckDuration = 0.9f;

        [Header("Eingabe")]
        [Tooltip("Tastatur zum Testen am PC. In der CAVE kann das aus bleiben, wenn das Tracking die Methoden aufruft.")]
        public bool keyboardInput = true;

        /// <summary>Wird vom Spiel gesetzt: nur während Countdown/Lauf darf man sich bewegen.</summary>
        public bool ControlsEnabled { get; set; } = true;

        public int CurrentLane { get; private set; } = 1; // 0 = links, 1 = Mitte, 2 = rechts
        public bool IsDucking => duckTimer > 0f;
        public float JumpOffset { get; private set; }
        public bool IsAirborne => JumpOffset > 0.35f;

        /// <summary>z.B. "Links", "Sprung" – praktisch zum Debuggen des Trackings.</summary>
        public event Action<string> ActionPerformed;

        Transform visual;
        Renderer bodyRenderer;
        Material normalMat, hitMat;
        float jumpTime = -1f;
        float duckTimer;
        float hitFlash;
        float runPhase;

        void Awake()
        {
            normalMat = RunnerUtil.Mat(new Color(0.2f, 0.45f, 0.95f));
            hitMat = RunnerUtil.Mat(new Color(1f, 0.15f, 0.1f), 1.5f);

            visual = new GameObject("Visual").transform;
            visual.SetParent(transform, false);
            bodyRenderer = RunnerUtil.Primitive(PrimitiveType.Capsule, "Body", visual,
                new Vector3(0, 0.75f, 0), new Vector3(0.45f, 0.55f, 0.45f), normalMat).GetComponent<Renderer>();
            RunnerUtil.Primitive(PrimitiveType.Sphere, "Head", visual,
                new Vector3(0, 1.5f, 0), Vector3.one * 0.38f, RunnerUtil.Mat(new Color(1f, 0.85f, 0.7f)));
        }

        // ---------- Öffentliche Steuerung (Tracking ruft diese auf) ----------

        public void MoveLeft()
        {
            if (!ControlsEnabled || CurrentLane == 0) return;
            CurrentLane--;
            ActionPerformed?.Invoke("Links");
        }

        public void MoveRight()
        {
            if (!ControlsEnabled || CurrentLane == CaveRunnerWorld.Lanes.Length - 1) return;
            CurrentLane++;
            ActionPerformed?.Invoke("Rechts");
        }

        public void Jump()
        {
            if (!ControlsEnabled || jumpTime >= 0f) return;
            duckTimer = 0f;
            jumpTime = 0f;
            ActionPerformed?.Invoke("Sprung");
        }

        public void Duck()
        {
            if (!ControlsEnabled) return;
            jumpTime = -1f; // in der Luft ducken = schnell runter
            JumpOffset = 0f;
            duckTimer = duckDuration;
            ActionPerformed?.Invoke("Ducken");
        }

        public void OnHit()
        {
            hitFlash = 0.4f;
        }

        public void ResetPlayer()
        {
            CurrentLane = 1;
            jumpTime = -1f;
            duckTimer = 0f;
            JumpOffset = 0f;
            transform.localPosition = new Vector3(CaveRunnerWorld.Lanes[1], 0f, transform.localPosition.z);
        }

        // ---------- Update ----------

        void Update()
        {
            if (keyboardInput) ReadKeyboard();

            float dt = Time.deltaTime;

            // Seitlich zur Ziel-Spur gleiten
            var p = transform.localPosition;
            p.x = Mathf.MoveTowards(p.x, CaveRunnerWorld.Lanes[CurrentLane], laneChangeSpeed * dt);

            // Sprung als Parabel
            if (jumpTime >= 0f)
            {
                jumpTime += dt;
                float t = jumpTime / jumpDuration;
                if (t >= 1f) { jumpTime = -1f; JumpOffset = 0f; }
                else JumpOffset = 4f * jumpHeight * t * (1f - t);
            }
            p.y = JumpOffset;
            transform.localPosition = p;

            // Ducken = Figur flach machen
            if (duckTimer > 0f) duckTimer -= dt;
            float targetScaleY = IsDucking ? 0.45f : 1f;
            var s = visual.localScale;
            s.y = Mathf.MoveTowards(s.y, targetScaleY, 6f * dt);
            visual.localScale = s;

            // Lauf-Wackeln, damit es nach Laufen aussieht
            runPhase += dt * 12f;
            visual.localPosition = new Vector3(0f, JumpOffset > 0 ? 0f : Mathf.Abs(Mathf.Sin(runPhase)) * 0.06f, 0f);

            // Treffer-Blinken
            if (hitFlash > 0f) hitFlash -= dt;
            bodyRenderer.sharedMaterial = (hitFlash > 0f && Mathf.Repeat(hitFlash, 0.1f) > 0.05f) ? hitMat : normalMat;
        }

        void ReadKeyboard()
        {
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) MoveLeft();
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) MoveRight();
            if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Jump();
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Duck();
        }
    }
}
