using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;

/// <summary>
/// Erzeugt die Szene für den CAVE-Runner: CAVE-Prefab + ein Objekt mit CaveRunner und CaveRunnerTracking.
/// Alles andere (Tunnel, Hindernisse, Anzeige) baut CaveRunner beim Start selbst auf.
/// </summary>
public static class CaveRunnerSceneBuilder
{
    public const string ScenePath = "Assets/CaveRunner/CaveRunner.unity";

    const string PreferredPrefab = "Assets/Samples/CAVE/1.1.1/Example CAVE Setup/Prefabs/CAVE.prefab";

    [MenuItem("CaveShow/Runner-Szene neu erstellen")]
    public static void CreateScene()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var prefab = FindCavePrefab();
        if (prefab == null)
        {
            Debug.LogError("CAVE-Runner: Kein CAVE-Prefab gefunden. Bitte das Sample \"Example CAVE Setup\" des CAVE-Pakets importieren.");
            return;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cave = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var runner = new GameObject("Cave Runner");
        runner.AddComponent<CaveRunner>();
        var tracking = runner.AddComponent<CaveRunnerTracking>();
        tracking.environment = cave.GetComponent<VirtualEnvironment>();
        tracking.tracker = cave.GetComponentInChildren<KinectTracker>(true);

        RenderSettings.skybox = null;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();

        Debug.Log("CAVE-Runner: Szene gespeichert unter " + ScenePath);
    }

    static GameObject FindCavePrefab()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PreferredPrefab);
        if (prefab != null)
            return prefab;

        return AssetDatabase.FindAssets("CAVE t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .OrderByDescending(path => path.Contains("Example CAVE Setup"))
            .Select(AssetDatabase.LoadAssetAtPath<GameObject>)
            .FirstOrDefault(candidate => candidate != null && candidate.GetComponent<VirtualEnvironment>() != null);
    }

    static void AddToBuildSettings(string path)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        if (scenes.Any(scene => scene.path == path))
            return;

        scenes.Insert(0, new EditorBuildSettingsScene(path, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
