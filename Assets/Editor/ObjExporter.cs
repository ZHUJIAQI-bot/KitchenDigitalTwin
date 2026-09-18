using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 把运行时生成的场景导出为 OBJ + MTL，供 Blender 渲染出图。
/// 因为场景几何全部在 Start() 中动态生成，这里先进入 Play 模式、等场景建完再导出。
/// </summary>
[InitializeOnLoad]
public static class ObjExporter
{
    private const string ScenePath = "Assets/Scenes/Main.unity";
    private const string OutputDir = "Export";
    private const string RunningKey = "ObjExporter.Running";

    private static int framesWaited;

    // 进入 Play 模式会触发域重载，静态回调会被清空；
    // 用 SessionState（跨域重载保留）标记任务，并在重载后自动重新注册
    static ObjExporter()
    {
        if (SessionState.GetBool(RunningKey, false))
        {
            framesWaited = 0;
            EditorApplication.update += Tick;
        }
    }

    public static void ExportFromPlaymode()
    {
        framesWaited = 0;
        SessionState.SetBool(RunningKey, true);
        EditorSceneManager.OpenScene(ScenePath);
        EditorApplication.update += Tick;
        EditorApplication.isPlaying = true;
    }

    private static void Tick()
    {
        if (!SessionState.GetBool(RunningKey, false) || !EditorApplication.isPlaying)
        {
            return;
        }

        framesWaited++;
        if (framesWaited < 90)   // 等约 1.5 秒，确保场景构建与静态合批完成
        {
            return;
        }

        SessionState.SetBool(RunningKey, false);
        EditorApplication.update -= Tick;

        int written = DoExport();
        Debug.Log("OBJ export finished: " + written + " objects");

        EditorApplication.isPlaying = false;
        EditorApplication.Exit(written > 0 ? 0 : 1);
    }

    private static int DoExport()
    {
        string root = Path.GetFullPath(OutputDir);
        Directory.CreateDirectory(root);

        Dictionary<Color, int> materialIndex = new Dictionary<Color, int>();
        List<Color> materialColors = new List<Color>();

        StringBuilder obj = new StringBuilder();
        obj.AppendLine("# 数字孪生驱动的老旧住宅厨房改造平台 - 场景导出");

        int vertexOffset = 1;
        int objectCount = 0;

        MeshFilter[] filters = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
        StringBuilder groups = new StringBuilder();

        for (int f = 0; f < filters.Length; f++)
        {
            MeshFilter filter = filters[f];
            if (filter == null || filter.sharedMesh == null)
            {
                continue;
            }
            Renderer renderer = filter.GetComponent<Renderer>();
            if (renderer == null || renderer.sharedMaterial == null)
            {
                continue;
            }

            // 跳过粒子等非网格渲染物
            string name = filter.gameObject.name;
            if (name.Contains("Particle"))
            {
                continue;
            }

            Color color = renderer.sharedMaterial.color;
            int matIndex;
            if (!materialIndex.TryGetValue(color, out matIndex))
            {
                matIndex = materialColors.Count;
                materialIndex[color] = matIndex;
                materialColors.Add(color);
            }

            Mesh mesh = filter.sharedMesh;
            Vector3[] verts = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int[] tris = mesh.triangles;
            if (verts.Length == 0 || tris.Length == 0)
            {
                continue;
            }

            Matrix4x4 matrix = filter.transform.localToWorldMatrix;
            Matrix4x4 normalMatrix = matrix.inverse.transpose;

            groups.AppendLine("g " + Sanitize(name) + "_" + objectCount);
            groups.AppendLine("usemtl mat_" + matIndex);

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 v = matrix.MultiplyPoint3x4(verts[i]);
                groups.AppendLine("v " + F(v.x) + " " + F(v.y) + " " + F(v.z));
            }
            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 n = normals.Length > i ? normalMatrix.MultiplyVector(normals[i]).normalized : Vector3.up;
                groups.AppendLine("vn " + F(n.x) + " " + F(n.y) + " " + F(n.z));
            }
            for (int i = 0; i < tris.Length; i += 3)
            {
                int a = vertexOffset + tris[i];
                int b = vertexOffset + tris[i + 1];
                int c = vertexOffset + tris[i + 2];
                groups.AppendLine("f " + a + "//" + a + " " + b + "//" + b + " " + c + "//" + c);
            }

            vertexOffset += verts.Length;
            objectCount++;
        }

        obj.Append(groups);

        // MTL
        StringBuilder mtl = new StringBuilder();
        mtl.AppendLine("# 场景材质（按颜色归并）");
        for (int i = 0; i < materialColors.Count; i++)
        {
            Color c = materialColors[i];
            mtl.AppendLine("newmtl mat_" + i);
            mtl.AppendLine("Kd " + F(c.r) + " " + F(c.g) + " " + F(c.b));
            mtl.AppendLine("Ka 0.1 0.1 0.1");
            mtl.AppendLine("Ks 0.05 0.05 0.05");
            mtl.AppendLine("Ns 20");
            mtl.AppendLine("d 1.0");
            mtl.AppendLine("illum 2");
            mtl.AppendLine();
        }

        string objPath = Path.Combine(root, "scene.obj");
        string mtlPath = Path.Combine(root, "scene.mtl");

        StringBuilder header = new StringBuilder();
        header.AppendLine("mtllib scene.mtl");
        File.WriteAllText(objPath, header + obj.ToString(), new UTF8Encoding(false));
        File.WriteAllText(mtlPath, mtl.ToString(), new UTF8Encoding(false));

        Debug.Log("材质数：" + materialColors.Count + "，导出：" + objPath);
        return objectCount;
    }

    private static string F(float value)
    {
        return value.ToString("F4", CultureInfo.InvariantCulture);
    }

    private static string Sanitize(string name)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        return sb.Length == 0 ? "obj" : sb.ToString();
    }
}
