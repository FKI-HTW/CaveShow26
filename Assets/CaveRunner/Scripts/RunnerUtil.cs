using System.Collections.Generic;
using UnityEngine;

namespace CaveRunner
{
    /// <summary>
    /// Kleine Hilfsfunktionen, um die Demo-Szene komplett aus Code zu bauen
    /// (Würfel, Materialien, 3D-Text). Später durch echte Modelle/Prefabs ersetzen.
    /// </summary>
    public static class RunnerUtil
    {
        static Material baseMaterial;
        static Font font;
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();

        public static Font Font
        {
            get
            {
                if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return font;
            }
        }

        /// <summary>Material mit Farbe (und optionalem Leuchten). Gleiche Farben teilen sich ein Material.</summary>
        public static Material Mat(Color color, float emission = 0f)
        {
            string key = ColorUtility.ToHtmlStringRGBA(color) + "_" + emission;
            if (cache.TryGetValue(key, out var cached)) return cached;

            if (baseMaterial == null)
            {
                // Standard-Material eines Primitives klonen -> funktioniert auch im Build.
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                baseMaterial = tmp.GetComponent<Renderer>().sharedMaterial;
                tmp.SetActive(false);
                Object.Destroy(tmp);
            }

            var m = new Material(baseMaterial) { color = color };
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", color * emission);
            }
            cache[key] = m;
            return m;
        }

        public static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.Destroy(go.GetComponent<Collider>()); // Kollision rechnen wir selbst (Spur + Höhe)
            return go;
        }

        public static GameObject Primitive(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.Destroy(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>3D-Text in der Welt (funktioniert auch auf allen CAVE-Wänden, anders als OnGUI).</summary>
        public static TextMesh Text(string name, Transform parent, Vector3 localPos, float characterSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            var tm = go.AddComponent<TextMesh>();
            tm.font = Font;
            tm.fontSize = 64;
            tm.characterSize = characterSize;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            go.GetComponent<MeshRenderer>().sharedMaterial = Font.material;
            return tm;
        }
    }
}
