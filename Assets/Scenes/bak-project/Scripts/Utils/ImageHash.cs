using UnityEngine;

public static class ImageHash
{
    public static string AverageHash(Texture2D tex, int size = 8)
    {
        var scaled = Scale(tex, size, size);
        float avg = 0f;
        var pixels = scaled.GetPixels();
        float[] lum = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            float l = pixels[i].r * 0.299f + pixels[i].g * 0.587f + pixels[i].b * 0.114f;
            lum[i] = l;
            avg += l;
        }
        avg /= pixels.Length;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < lum.Length; i++)
            sb.Append(lum[i] > avg ? '1' : '0');
        return sb.ToString();
    }

    private static Texture2D Scale(Texture2D src, int w, int h)
    {
        RenderTexture rt = RenderTexture.GetTemporary(w, h);
        Graphics.Blit(src, rt);
        RenderTexture.active = rt;
        Texture2D scaled = new Texture2D(w, h, TextureFormat.RGB24, false);
        scaled.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        scaled.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return scaled;
    }
}