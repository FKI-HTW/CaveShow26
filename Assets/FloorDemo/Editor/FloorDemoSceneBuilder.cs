using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using HTW.CAVE;
using HTW.CAVE.Kinect;

namespace CaveShow.EditorTools
{
	/// <summary>
	/// Erzeugt die Szene für die Boden-Tracking-Demo: CAVE-Prefab + FloorFieldDemo.
	/// </summary>
	public static class FloorDemoSceneBuilder
	{
		public const string scenePath = "Assets/FloorDemo/FloorFieldDemo.unity";

		private const string k_PreferredPrefab = "Assets/Samples/CAVE/1.1.1/Example CAVE Setup/Prefabs/CAVE.prefab";

		[MenuItem("CaveShow/Boden-Demo-Szene neu erstellen")]
		public static void CreateScene()
		{
			if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
				return;

			var prefab = FindCavePrefab();

			if (prefab == null)
			{
				Debug.LogError("Boden-Demo: Kein CAVE-Prefab gefunden. Bitte das Sample \"Example CAVE Setup\" des CAVE-Pakets importieren.");
				return;
			}

			var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			var cave = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
			var demo = new GameObject("Floor Field Demo").AddComponent<FloorFieldDemo>();
			demo.environment = cave.GetComponent<VirtualEnvironment>();
			demo.tracker = cave.GetComponentInChildren<KinectTracker>(true);

			RenderSettings.skybox = null;
			RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
			RenderSettings.ambientLight = Color.black;

			EditorSceneManager.SaveScene(scene, scenePath);
			AddToBuildSettings(scenePath);
			AssetDatabase.SaveAssets();

			Debug.Log("Boden-Demo: Szene gespeichert unter " + scenePath);
		}

		private static GameObject FindCavePrefab()
		{
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(k_PreferredPrefab);

			if (prefab != null)
				return prefab;

			return AssetDatabase.FindAssets("CAVE t:Prefab")
				.Select(AssetDatabase.GUIDToAssetPath)
				.OrderByDescending(path => path.Contains("Example CAVE Setup"))
				.Select(AssetDatabase.LoadAssetAtPath<GameObject>)
				.FirstOrDefault(candidate => candidate != null && candidate.GetComponent<VirtualEnvironment>() != null);
		}

		private static void AddToBuildSettings(string path)
		{
			var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);

			if (scenes.Any(scene => scene.path == path))
				return;

			scenes.Insert(0, new EditorBuildSettingsScene(path, true));
			EditorBuildSettings.scenes = scenes.ToArray();
		}
	}
}
