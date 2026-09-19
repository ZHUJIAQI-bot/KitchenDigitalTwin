using UnityEngine;

/// <summary>
/// 全屏色调效果：挂在相机上，用 Custom/ColorGrade 着色器做饱和度/曝光/对比/色阶调整。
/// 不依赖 post-processing 包，WebGL 下也可用。
/// </summary>
[RequireComponent(typeof(Camera))]
public class ColorGradeEffect : MonoBehaviour
{
    public Material gradeMaterial;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (gradeMaterial != null)
        {
            Graphics.Blit(source, destination, gradeMaterial);
        }
        else
        {
            Graphics.Blit(source, destination);
        }
    }
}
