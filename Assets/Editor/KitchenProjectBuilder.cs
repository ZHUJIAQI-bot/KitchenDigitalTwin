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
        // 文件名带内容哈希：避免浏览器缓存旧的 .wasm/.data 导致一直玩到旧版本
        PlayerSettings.WebGL.nameFilesAsHashes = true;

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
        PruneOldBuilds(outputPath, 3);
        Debug.Log("Kitchen WebGL built to: " + outputPath);
    }

    // 只保留最近 keepGenerations 代构建产物
    // 原因：GitHub Pages 给 index.html 加了 max-age=600 缓存，浏览器可能仍在用旧的 index.html
    // 若旧文件已被删除就会 404（Unable to load file Build/xxx.framework.js）
    private static void PruneOldBuilds(string outputPath, int keepGenerations)
    {
        string buildDir = Path.Combine(outputPath, "Build");
        if (!Directory.Exists(buildDir))
        {
            return;
        }

        string[] patterns = { "*.wasm", "*.data", "*.loader.js", "*.framework.js" };
        foreach (string pattern in patterns)
        {
            string[] files = Directory.GetFiles(buildDir, pattern);
            System.Array.Sort(files, (a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
            for (int i = keepGenerations; i < files.Length; i++)
            {
                File.Delete(files[i]);
            }
            if (files.Length > keepGenerations)
            {
                Debug.Log("Pruned " + (files.Length - keepGenerations) + " old " + pattern + " file(s)");
            }
        }
    }

    // 运行时用 Shader.Find 取用的着色器必须打进包，否则 WebGL 下返回 null
    private static readonly string[] RequiredShaders =
    {
        "Standard",
        "Custom/Glass",
        "Custom/Water",
        "Unlit/Transparent Cutout",
        "Particles/Standard Unlit",
        "Transparent/Cutout/Diffuse",
        "Legacy Shaders/Transparent/Diffuse",
    };

    private static void EnsureStandardShaderIncluded()
    {
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

        bool changed = false;
        foreach (string shaderName in RequiredShaders)
        {
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning("Shader not found in editor: " + shaderName);
                continue;
            }

            bool present = false;
            for (int i = 0; i < alwaysIncluded.arraySize; i++)
            {
                if (alwaysIncluded.GetArrayElementAtIndex(i).objectReferenceValue == shader)
                {
                    present = true;
                    break;
                }
            }
            if (present)
            {
                continue;
            }

            int index = alwaysIncluded.arraySize;
            alwaysIncluded.InsertArrayElementAtIndex(index);
            alwaysIncluded.GetArrayElementAtIndex(index).objectReferenceValue = shader;
            changed = true;
            Debug.Log("Added to Always Included Shaders: " + shaderName);
        }

        if (changed)
        {
            serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
