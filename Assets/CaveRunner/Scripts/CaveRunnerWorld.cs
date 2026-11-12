using System;
using System.Collections.Generic;
using UnityEngine;

namespace CaveRunner
{
    public enum ObstacleType
    {
        Box,        // Kiste: Spur wechseln
        LowHurdle,  // niedriges Hindernis: springen
        HighBar     // Stange oben: ducken
    }

    public class Obstacle : MonoBehaviour
    {
        public ObstacleType type;
        public int lane;
        public bool resolved;
    }

    /// <summary>
    /// SPIELWELT (Umgebung): Tunnel mit 3 Spuren + Hindernisse.
    ///
    /// Idee für die CAVE: Die Person (und die CAVE) bleibt bei z = 0 stehen,
    /// die WELT bewegt sich auf sie zu (Laufband-Prinzip). Darum liegt alles,
    /// was sich bewegt, unter "World" und wandert Richtung -z.
    /// </summary>
    public class CaveRunnerWorld : MonoBehaviour
    {
        /// <summary>x-Position der drei Spuren (links, Mitte, rechts).</summary>
        public static readonly float[] Lanes = { -1.6f, 0f, 1.6f };

        [Header("Tunnel")]
        public float segmentLength = 10f;
        public int segmentCount = 10;
        public float tunnelWidth = 6.5f;
        public float tunnelHeight = 4.5f;

        [Header("Tempo")]
        [Tooltip("Meter pro Sekunde, mit der die Welt auf die Person zukommt.")]
        public float speed = 10f;

        [Header("Hindernisse")]
        public bool spawnObstacles = true;
        public float obstacleMinGap = 14f;
        public float obstacleMaxGap = 24f;
        [Range(0f, 1f)] public float doubleBoxChance = 0.25f;
        public float spawnDistance = 80f;
        public float despawnZ = -12f;

        [Header("Atmosphäre")]
        public bool useFog = true;

        /// <summary>Wird ausgelöst, wenn die Figur ein Hindernis trifft.</summary>
        public event Action<ObstacleType> ObstacleHit;

        public bool Running { get; set; }
        public Transform WorldRoot { get; private set; }

        RunnerPlayer player;
        readonly List<Transform> segments = new List<Transform>();
        readonly List<Obstacle> obstacles = new List<Obstacle>();
        float distanceUntilNextObstacle;

        // Farben der Höhle
        Material floorMat, laneMat, wallMatA, wallMatB, ceilingMat, ribMat, lampMat;
        Material boxMat, hurdleMat, barMat, postMat;

        public void Init(RunnerPlayer runner)
        {
            player = runner;
            WorldRoot = new GameObject("World (bewegt sich)").transform;
            WorldRoot.SetParent(transform, false);

            CreateMaterials();
            SetupAtmosphere();
            for (int i = 0; i < segmentCount; i++)
                segments.Add(BuildSegment(i, despawnZ + i * segmentLength));

            distanceUntilNextObstacle = 30f;
        }

        void CreateMaterials()
        {
            floorMat = RunnerUtil.Mat(new Color(0.22f, 0.2f, 0.19f));
            laneMat = RunnerUtil.Mat(new Color(0.95f, 0.9f, 0.6f), 0.6f);
            wallMatA = RunnerUtil.Mat(new Color(0.36f, 0.3f, 0.26f));
            wallMatB = RunnerUtil.Mat(new Color(0.3f, 0.25f, 0.22f));
            ceilingMat = RunnerUtil.Mat(new Color(0.18f, 0.16f, 0.15f));
            ribMat = RunnerUtil.Mat(new Color(0.45f, 0.33f, 0.2f));
            lampMat = RunnerUtil.Mat(new Color(1f, 0.8f, 0.45f), 2f);

            boxMat = RunnerUtil.Mat(new Color(0.85f, 0.55f, 0.25f));
            hurdleMat = RunnerUtil.Mat(new Color(1f, 0.5f, 0.1f), 0.4f);
            barMat = RunnerUtil.Mat(new Color(0.9f, 0.2f, 0.25f), 0.4f);
            postMat = RunnerUtil.Mat(new Color(0.5f, 0.35f, 0.2f));
        }

        void SetupAtmosphere()
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.35f, 0.32f, 0.3f);
            if (useFog)
            {
                // Nebel versteckt, dass Segmente/Hindernisse hinten "aufploppen".
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = new Color(0.08f, 0.07f, 0.07f);
                RenderSettings.fogStartDistance = 35f;
                RenderSettings.fogEndDistance = spawnDistance + 5f;
            }

            if (FindObjectOfType<Light>() == null)
            {
                var sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
                sun.intensity = 0.5f;
                sun.color = new Color(1f, 0.92f, 0.85f);
                sun.transform.rotation = Quaternion.Euler(50f, -20f, 0f);
            }
        }

        Transform BuildSegment(int index, float z)
        {
            var seg = new GameObject("Segment " + index).transform;
            seg.SetParent(WorldRoot, false);
            seg.localPosition = new Vector3(0, 0, z);

            float L = segmentLength, W = tunnelWidth, H = tunnelHeight;
            var wallMat = index % 2 == 0 ? wallMatA : wallMatB; // abwechselnd -> man spürt die Geschwindigkeit

            // Boden + Spurlinien (gestrichelt)
            RunnerUtil.Box("Floor", seg, new Vector3(0, -0.05f, 0), new Vector3(W, 0.1f, L), floorMat);
            float lineX = (Lanes[1] - Lanes[0]) * 0.5f;
            RunnerUtil.Box("Line L", seg, new Vector3(-lineX, 0.01f, 0), new Vector3(0.08f, 0.02f, L * 0.45f), laneMat);
            RunnerUtil.Box("Line R", seg, new Vector3(lineX, 0.01f, 0), new Vector3(0.08f, 0.02f, L * 0.45f), laneMat);

            // Wände + Decke
            RunnerUtil.Box("Wall L", seg, new Vector3(-W * 0.5f - 0.1f, H * 0.5f, 0), new Vector3(0.2f, H, L), wallMat);
            RunnerUtil.Box("Wall R", seg, new Vector3(W * 0.5f + 0.1f, H * 0.5f, 0), new Vector3(0.2f, H, L), wallMat);
            RunnerUtil.Box("Ceiling", seg, new Vector3(0, H + 0.1f, 0), new Vector3(W + 0.4f, 0.2f, L), ceilingMat);

            // Holzbogen (wie im Bergwerk) am Segmentanfang
            float zRib = -L * 0.5f + 0.2f;
            RunnerUtil.Box("Rib L", seg, new Vector3(-W * 0.5f + 0.15f, H * 0.5f, zRib), new Vector3(0.3f, H, 0.3f), ribMat);
            RunnerUtil.Box("Rib R", seg, new Vector3(W * 0.5f - 0.15f, H * 0.5f, zRib), new Vector3(0.3f, H, 0.3f), ribMat);
            RunnerUtil.Box("Rib Top", seg, new Vector3(0, H - 0.15f, zRib), new Vector3(W, 0.3f, 0.3f), ribMat);

            // Lampe in jedem 2. Segment
            if (index % 2 == 0)
            {
                RunnerUtil.Box("Lamp", seg, new Vector3(0, H - 0.05f, 0), new Vector3(0.6f, 0.1f, 1.2f), lampMat);
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

        void Update()
        {
            if (!Running) return;
            float move = speed * Time.deltaTime;

            // Tunnel-Segmente nach hinten schieben und vorne wiederverwenden
            float totalLength = segmentCount * segmentLength;
            foreach (var seg in segments)
            {
                var p = seg.localPosition;
                p.z -= move;
                if (p.z < despawnZ - segmentLength) p.z += totalLength;
                seg.localPosition = p;
            }

            // Hindernisse bewegen + Kollision prüfen
            float playerZ = player.transform.localPosition.z;
            for (int i = obstacles.Count - 1; i >= 0; i--)
            {
                var o = obstacles[i];
                var p = o.transform.localPosition;
                float before = p.z;
                p.z -= move;
                o.transform.localPosition = p;

                if (!o.resolved && before > playerZ && p.z <= playerZ)
                {
                    o.resolved = true;
                    if (o.lane == player.CurrentLane && !IsAvoided(o.type))
                    {
                        player.OnHit();
                        ObstacleHit?.Invoke(o.type);
                    }
                }

                if (p.z < despawnZ)
                {
                    Destroy(o.gameObject);
                    obstacles.RemoveAt(i);
                }
            }

            // Neue Hindernisse
            if (spawnObstacles)
            {
                distanceUntilNextObstacle -= move;
                if (distanceUntilNextObstacle <= 0f)
                {
                    SpawnRandomRow(spawnDistance);
                    distanceUntilNextObstacle = UnityEngine.Random.Range(obstacleMinGap, obstacleMaxGap);
                }
            }
        }

        bool IsAvoided(ObstacleType type)
        {
            switch (type)
            {
                case ObstacleType.LowHurdle: return player.IsAirborne;
                case ObstacleType.HighBar: return player.IsDucking;
                default: return false; // Kiste: nur durch Spurwechsel
            }
        }

        void SpawnRandomRow(float z)
        {
            int lane = UnityEngine.Random.Range(0, Lanes.Length);
            var type = (ObstacleType)UnityEngine.Random.Range(0, 3);
            SpawnObstacle(type, lane, z);

            // Manchmal eine zweite Kiste auf einer anderen Spur (eine Spur bleibt immer frei)
            if (type == ObstacleType.Box && UnityEngine.Random.value < doubleBoxChance)
            {
                int other = (lane + UnityEngine.Random.Range(1, Lanes.Length)) % Lanes.Length;
                SpawnObstacle(ObstacleType.Box, other, z);
            }
        }

        public Obstacle SpawnObstacle(ObstacleType type, int lane, float z)
        {
            var root = new GameObject(type + " (Spur " + lane + ")").transform;
            root.SetParent(WorldRoot, false);
            root.localPosition = new Vector3(Lanes[lane], 0f, z);

            switch (type)
            {
                case ObstacleType.Box:
                    RunnerUtil.Box("Kiste", root, new Vector3(0, 0.6f, 0), new Vector3(1.2f, 1.2f, 1.2f), boxMat);
                    break;
                case ObstacleType.LowHurdle:
                    RunnerUtil.Box("Balken", root, new Vector3(0, 0.45f, 0), new Vector3(1.4f, 0.18f, 0.2f), hurdleMat);
                    RunnerUtil.Box("Bein L", root, new Vector3(-0.6f, 0.2f, 0), new Vector3(0.1f, 0.4f, 0.1f), postMat);
                    RunnerUtil.Box("Bein R", root, new Vector3(0.6f, 0.2f, 0), new Vector3(0.1f, 0.4f, 0.1f), postMat);
                    break;
                case ObstacleType.HighBar:
                    RunnerUtil.Box("Stange", root, new Vector3(0, 1.3f, 0), new Vector3(1.5f, 0.25f, 0.25f), barMat);
                    RunnerUtil.Box("Pfosten L", root, new Vector3(-0.72f, 0.95f, 0), new Vector3(0.12f, 1.9f, 0.12f), postMat);
                    RunnerUtil.Box("Pfosten R", root, new Vector3(0.72f, 0.95f, 0), new Vector3(0.12f, 1.9f, 0.12f), postMat);
                    break;
            }

            var o = root.gameObject.AddComponent<Obstacle>();
            o.type = type;
            o.lane = lane;
            obstacles.Add(o);
            return o;
        }

        public void ClearObstacles()
        {
            foreach (var o in obstacles) if (o != null) Destroy(o.gameObject);
            obstacles.Clear();
            distanceUntilNextObstacle = 30f;
        }
    }
}
