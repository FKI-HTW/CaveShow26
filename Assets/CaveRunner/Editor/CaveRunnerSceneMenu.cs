using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CaveRunner.EditorTools
{
    /// <summary>
    /// Sorgt dafür, dass die Demo per "File > Build And Run" (Strg+B) startet:
    /// - Beim Öffnen des Projekts wird Assets/CaveRunner/Scenes/CaveRunnerDemo.unity angelegt (falls nicht vorhanden)
    ///   und als erste Szene in die Build Settings eingetragen.
    /// - Menü "CaveRunner": Szene neu erstellen / Build and Run für die aktuelle Plattform (Windows oder Mac).
    /// </summary>
    [InitializeOnLoad]
    public static class CaveRunnerSceneMenu
    {
        public const string ScenePath = "Assets/CaveRunner/Scenes/CaveRunnerDemo.unity";
        const string CaveScenePath = "Assets/CaveRunner/Scenes/CaveRunnerDemo_CAVE.unity";
        const string CavePrefabPath = "Assets/Samples/CAVE/1.1.1/Example CAVE Setup/Prefabs/CAVE.prefab";

        static CaveRunnerSceneMenu()
        {
            // Erst ausführen, wenn der Editor fertig geladen ist
            EditorApplication.delayCall += EnsureDemoSceneInBuild;
        }

        static void EnsureDemoSceneInBuild()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists(ScenePath)) CreateSceneFile(ScenePath, false);
            AddToBuildSettings(ScenePath);
        }

        // ---------- Menü ----------

        [MenuItem("CaveRunner/Demo-Szene öffnen")]
        static void OpenDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            if (!File.Exists(ScenePath)) CreateSceneFile(ScenePath, false);
            EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("CaveRunner/Demo-Szene neu erstellen (PC-Test)")]
        static void RecreateDesktopScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateSceneFile(ScenePath, false);
            AddToBuildSettings(ScenePath);
            EditorSceneManager.OpenScene(ScenePath);
        }

        [MenuItem("CaveRunner/Demo-Szene mit CAVE-Prefab erstellen")]
        static void CreateCaveScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            CreateSceneFile(CaveScenePath, true);
            EditorSceneManager.OpenScene(CaveScenePath);
        }

        [MenuItem("CaveRunner/Build and Run")]
        static void BuildAndRunMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Build(true);
        }

        /// <summary>Kommandozeile: Unity -batchmode -quit -projectPath . -executeMethod CaveRunner.EditorTools.CaveRunnerSceneMenu.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            bool ok = Build(false);
            if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
        }

        // ---------- Build ----------

        static bool Build(bool autoRun)
        {
            if (!File.Exists(ScenePath)) CreateSceneFile(ScenePath, false);
            AddToBuildSettings(ScenePath);

            var target = EditorUserBuildSettings.activeBuildTarget;
            string location;
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    location = "Builds/Windows/CaveRunnerDemo.exe"; break;
                case BuildTarget.StandaloneOSX:
                    location = "Builds/Mac/CaveRunnerDemo.app"; break;
                case BuildTarget.StandaloneLinux64:
                    location = "Builds/Linux/CaveRunnerDemo.x86_64"; break;
                default:
                    Debug.LogError("CaveRunner: Bitte unter File > Build Settings Windows/Mac/Linux als Plattform wählen (aktuell: " + target + ").");
                    return false;
            }

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = location,
                target = target,
                options = autoRun ? BuildOptions.AutoRunPlayer : BuildOptions.None
            });
            bool ok = report.summary.result == BuildResult.Succeeded;
            if (ok) Debug.Log("CaveRunner-Build fertig: " + location);
            else Debug.LogError("CaveRunner-Build fehlgeschlagen: " + report.summary.result);
            return ok;
        }

        // ---------- Szene erzeugen ----------

        static void CreateSceneFile(string path, bool withCave)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var previousActive = SceneManager.GetActiveScene();
            bool replaceUntitled = string.IsNullOrEmpty(previousActive.path) && SceneManager.sceneCount == 1;

            // Additiv anlegen, damit die gerade offene Szene nicht verloren geht
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                replaceUntitled ? NewSceneMode.Single : NewSceneMode.Additive);
            SceneManager.SetActiveScene(scene);

            new GameObject("CaveRunnerGame").AddComponent<CaveRunnerGame>();

            if (withCave)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CavePrefabPath);
                if (prefab != null)
                {
                    var cave = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    cave.name = "CAVE"; // CaveRunnerGame erkennt die CAVE am Namen
                }
                else Debug.LogWarning("CAVE-Prefab nicht gefunden: " + CavePrefabPath);
            }

            // Nebel in der Szene speichern, sonst fehlt er im Build
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;

            EditorSceneManager.SaveScene(scene, path);

            if (!replaceUntitled)
            {
                if (previousActive.IsValid()) SceneManager.SetActiveScene(previousActive);
                EditorSceneManager.CloseScene(scene, true);
            }
            AssetDatabase.Refresh();
            Debug.Log("CaveRunner-Demo-Szene erstellt: " + path);
        }

        static void AddToBuildSettings(string path)
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            if (scenes.Exists(s => s.path == path)) return;
            scenes.Insert(0, new EditorBuildSettingsScene(path, true)); // erste Szene = Startszene
            EditorBuildSettings.scenes = scenes.ToArray();
            Debug.Log("CaveRunner: Demo-Szene als Startszene in die Build Settings eingetragen.");
        }
    }
}
