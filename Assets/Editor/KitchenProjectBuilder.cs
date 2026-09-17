using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class KitchenProjectBuilder
{
    private const string ScenePath = "Assets/Scenes/Main.unity";

    [MenuItem("Kitchen/Build Demo Scene")]
    public static void Build()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Scripts");

        SceneAsset existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
        if (existing != null)
        {
            AssetDatabase.DeleteAsset(ScenePath);
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        GameObject root = new GameObject("Kitchen Digital Twin");
        root.AddComponent<KitchenSimulator>();
        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true)
        };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Kitchen demo scene built: " + ScenePath);
    }

    public static void BuildPlayer()
    {
        Build();
        string outputPath = Path.GetFullPath("Builds/KitchenDigitalTwin.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
        BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outputPath,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None
        });
        Debug.Log("Kitchen player built: " + outputPath);
    }

    [MenuItem("Kitchen/Build WebGL")]
    public static void BuildWebGL()
    {
        string outputPath = Path.GetFullPath("docs");
        Directory.CreateDirectory(outputPath);
        BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outputPath,
            target = BuildTarget.WebGL,
            options = BuildOptions.None
        });
        Debug.Log("Kitchen WebGL built to: " + outputPath);
    }
}
