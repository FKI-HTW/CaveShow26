using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;

/// <summary>
/// Erzeugt die Szene für die Fruit-Sort-Demo: CAVE-Prefab + ein Objekt mit FruitSortDemo und FruitSortTracking.
/// Alles andere (Wände, Block, Anzeige) baut FruitSortDemo beim Start selbst auf.
/// </summary>
public static class FruitSortSceneBuilder
{
    public const string ScenePath = "Assets/FruitSort/FruitSort.unity";

    const string MaterialFolder = "Assets/FruitSort/Materials";

    const string PreferredPrefab = "Assets/Samples/CAVE/1.1.1/Example CAVE Setup/Prefabs/CAVE.prefab";

    [MenuItem("CaveShow/Fruit-Sort-Szene neu erstellen")]
    public static void CreateScene()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        var prefab = FindCavePrefab();
        if (prefab == null)
        {
            Debug.LogError("Fruit-Sort: Kein CAVE-Prefab gefunden. Bitte das Sample \"Example CAVE Setup\" des CAVE-Pakets importieren.");
            return;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var cave = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
        var demoObject = new GameObject("Fruit Sort");
        var demo = demoObject.AddComponent<FruitSortDemo>();
        demo.colorMaterial = LoadOrCreateMaterial();
        var tracking = demoObject.AddComponent<FruitSortTracking>();
        tracking.environment = cave.GetComponent<VirtualEnvironment>();
        tracking.tracker = cave.GetComponentInChildren<KinectTracker>(true);

        RenderSettings.skybox = null;

        EditorSceneManager.SaveScene(scene, ScenePath);
        AddToBuildSettings(ScenePath);
        AssetDatabase.SaveAssets();

        Debug.Log("Fruit-Sort: Szene gespeichert unter " + ScenePath);
    }

    /// <summary>
    /// Material-Vorlage als Asset. Weil die Szene sie referenziert, kommt der Shader in den Build –
    /// sonst wäre dort alles pink.
    /// </summary>
    static Material LoadOrCreateMaterial()
    {
        string path = MaterialFolder + "/FruitSortColor.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null)
            return material;

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder("Assets/FruitSort", "Materials");

        material = new Material(Shader.Find("Unlit/Color"));
        AssetDatabase.CreateAsset(material, path);
        return material;
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
