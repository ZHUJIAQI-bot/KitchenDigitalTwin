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
        // GitHub Pages 不返回 Content-Encoding 头，必须禁用压缩，否则加载进度条会一直卡住
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.decompressionFallback = false;

        // 运行时用 Shader.Find("Standard") 建材质，必须保证 Standard 打进包，否则黑屏
        EnsureStandardShaderIncluded();

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

    private static void EnsureStandardShaderIncluded()
    {
        Shader standard = Shader.Find("Standard");
        if (standard == null)
        {
            Debug.LogError("Standard shader not found in editor");
            return;
        }

        UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
        if (assets == null || assets.Length == 0)
        {
            return;
        }

        SerializedObject serializedObject = new SerializedObject(assets[0]);
        SerializedProperty alwaysIncluded = serializedObject.FindProperty("m_AlwaysIncludedShaders");
        if (alwaysIncluded == null)
        {
            return;
        }

        for (int i = 0; i < alwaysIncluded.arraySize; i++)
        {
            if (alwaysIncluded.GetArrayElementAtIndex(i).objectReferenceValue == standard)
            {
                return; // 已包含
            }
        }

        int index = alwaysIncluded.arraySize;
        alwaysIncluded.InsertArrayElementAtIndex(index);
        alwaysIncluded.GetArrayElementAtIndex(index).objectReferenceValue = standard;
        serializedObject.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("Standard shader added to Always Included Shaders");
    }
}
