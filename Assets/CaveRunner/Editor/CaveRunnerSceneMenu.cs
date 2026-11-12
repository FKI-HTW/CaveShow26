using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CaveRunner.EditorTools
{
    /// <summary>
    /// Menü "CaveRunner > Demo-Szene erstellen": legt Assets/CaveRunner/Scenes/CaveRunnerDemo.unity an.
    /// </summary>
    public static class CaveRunnerSceneMenu
    {
        const string ScenePath = "Assets/CaveRunner/Scenes/CaveRunnerDemo.unity";
        const string CavePrefabPath = "Assets/Samples/CAVE/1.1.1/Example CAVE Setup/Prefabs/CAVE.prefab";

        [MenuItem("CaveRunner/Demo-Szene erstellen (PC-Test)")]
        static void CreateDesktopScene() => CreateScene(false);

        [MenuItem("CaveRunner/Demo-Szene erstellen (mit CAVE-Prefab)")]
        static void CreateCaveScene() => CreateScene(true);

        static void CreateScene(bool withCave)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            new GameObject("CaveRunnerGame").AddComponent<CaveRunnerGame>();

            if (withCave)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CavePrefabPath);
                if (prefab != null)
                {
                    var cave = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    cave.name = "CAVE"; // CaveRunnerGame erkennt die CAVE am Namen
                }
                else Debug.LogWarning("CAVE-Prefab nicht gefunden: " + CavePrefabPath);
            }

            // Nebel in der Szene speichern, damit er auch im Build funktioniert
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;

            System.IO.Directory.CreateDirectory("Assets/CaveRunner/Scenes");
            string path = withCave ? ScenePath.Replace(".unity", "_CAVE.unity") : ScenePath;
            EditorSceneManager.SaveScene(scene, path);
            AssetDatabase.Refresh();
            Debug.Log("CaveRunner-Demo-Szene erstellt: " + path + "  -> Play drücken!");
        }
    }
}
