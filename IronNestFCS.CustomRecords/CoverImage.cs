using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.CustomRecords;

/// <summary>
/// 把封面图片字节转换成中心封面贴图：任意分辨率的图片（含非 2 次幂）直接双线性拉伸
/// 填充到整张 1024x1024 贴图，替换盘面 Record Disk Blend 材质的 CoverArt 槽位
/// （游戏结构：mat[0]=VinylRecord 整体黑胶、mat[1]=CoverArt 中心封面——只替换封面槽位，
/// 黑胶槽位保持原样，即"整体纯黑胶 + 中心封面图"的原版样式）。
///
/// 全程只用了 IL2CPP 裁剪后仍保留的 Texture2D API：LoadImage / GetPixels32 /
/// SetPixels32 / Apply（已验证这些在 UnityEngine.CoreModule interop stub 里存在）。
/// 缩放用自写双线性，避免依赖被裁的 Graphics/Blit-to-Texture2D 读回路径。
/// </summary>
internal static class CoverImage
{
    private const int CanvasSize = 1024;

    /// <summary>
    /// 从封面原始字节(PNG/JPEG)构造整幅拉伸的 1024x1024 封面贴图。
    /// 解码失败返回 null（调用方应跳过该文件）。
    /// </summary>
    internal static Texture2D? Build(byte[] coverBytes)
    {
        // 先把封面解码进一张临时可读纹理。LoadImage 会按图实际尺寸 Reinitialize。
        var srcTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(srcTex, new Il2CppStructArray<byte>(coverBytes), false))
        {
            Object.Destroy(srcTex);
            return null;
        }

        int srcW = srcTex.width, srcH = srcTex.height;
        // GetPixels32 返回交错的 Color32[]，行序自底向上（Unity 约定）。
        // 整幅拉伸不涉及方向敏感的操作，行序差异无影响。
        Color32[] src = ToManaged(srcTex.GetPixels32());
        Object.Destroy(srcTex); // 源纹理用完即弃。

        // 任意分辨率 → 双线性拉伸填充整张 1024x1024。
        Color32[] full = ScaleBilinear(src, srcW, srcH, CanvasSize, CanvasSize);

        var outTex = new Texture2D(CanvasSize, CanvasSize, TextureFormat.RGBA32, false);
        outTex.SetPixels32(new Il2CppStructArray<Color32>(full));
        outTex.Apply();
        return outTex;
    }

    /// <summary>Il2Cpp Color32 数组拷成托管数组，便于在托管侧高速随机访问。</summary>
    internal static Color32[] ToManaged(Il2CppStructArray<Color32> arr)
    {
        var managed = new Color32[arr.Length];
        for (int i = 0; i < arr.Length; i++)
            managed[i] = arr[i];
        return managed;
    }

    /// <summary>
    /// 双线性缩放 Color32 位图。源/目标都按行优先、自底向上布局（与 GetPixels32 一致）。
    /// </summary>
    private static Color32[] ScaleBilinear(Color32[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new Color32[dw * dh];
        // 用 dw-1 / dh-1 做比例，保证目标四角精确映射到源四角。
        float sx = sw > 1 ? (float)(sw - 1) / (dw - 1) : 0f;
        float sy = sh > 1 ? (float)(sh - 1) / (dh - 1) : 0f;

        for (int y = 0; y < dh; y++)
        {
            float fy = y * sy;
            int y0 = (int)fy;
            int y1 = Mathf.Min(y0 + 1, sh - 1);
            float wy = fy - y0;

            for (int x = 0; x < dw; x++)
            {
                float fx = x * sx;
                int x0 = (int)fx;
                int x1 = Mathf.Min(x0 + 1, sw - 1);
                float wx = fx - x0;

                Color32 c00 = src[y0 * sw + x0];
                Color32 c10 = src[y0 * sw + x1];
                Color32 c01 = src[y1 * sw + x0];
                Color32 c11 = src[y1 * sw + x1];

                dst[y * dw + x] = BilerpColor(c00, c10, c01, c11, wx, wy);
            }
        }
        return dst;
    }

    private static Color32 BilerpColor(Color32 c00, Color32 c10, Color32 c01, Color32 c11, float wx, float wy)
    {
        float top_r = c00.r + (c10.r - c00.r) * wx;
        float top_g = c00.g + (c10.g - c00.g) * wx;
        float top_b = c00.b + (c10.b - c00.b) * wx;
        float top_a = c00.a + (c10.a - c00.a) * wx;

        float bot_r = c01.r + (c11.r - c01.r) * wx;
        float bot_g = c01.g + (c11.g - c01.g) * wx;
        float bot_b = c01.b + (c11.b - c01.b) * wx;
        float bot_a = c01.a + (c11.a - c01.a) * wx;

        return new Color32(
            (byte)(top_r + (bot_r - top_r) * wy),
            (byte)(top_g + (bot_g - top_g) * wy),
            (byte)(top_b + (bot_b - top_b) * wy),
            (byte)(top_a + (bot_a - top_a) * wy));
    }
}
