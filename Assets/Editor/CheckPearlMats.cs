using UnityEditor;
using UnityEngine;

public static class CheckPearlMats
{
    public static void Run()
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Models/Lujiazui/OrientalPearl.fbx");
        if (go == null) { Debug.Log("FBX NOT FOUND"); return; }
        var renderers = go.GetComponentsInChildren<Renderer>(true);
        var seen = new System.Collections.Generic.HashSet<string>();
        int glow = 0;
        foreach (var r in renderers)
        {
            foreach (var m in r.sharedMaterials)
            {
                if (m == null) { Debug.Log(r.name + " -> NULL material"); continue; }
                if (seen.Add(m.name))
                {
                    bool isGlow = m.name.Contains("LJ_Glow");
                    if (isGlow) glow++;
                    Debug.Log(string.Format("材质 {0}  shader={1}  glow={2}", m.name, m.shader.name, isGlow));
                }
            }
        }
        Debug.Log("渲染器数=" + renderers.Length + " 发光材质数=" + glow);
    }
}
