using System;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace CairnRouteMaker.Game;

/// <summary>
/// One-off dump of rendering details we can only learn at runtime (F10). Used to pick how the
/// route overlay should be drawn; not part of normal play.
/// </summary>
internal static class Diagnostics
{
    /// <summary>The frame rate in the dump is averaged over about this many seconds.</summary>
    private const float FrameWindow = 5f;

    /// <summary>Recent frame lengths in seconds, a ring with the newest just before <see cref="_frameIndex"/>.</summary>
    private static readonly float[] FrameTimes = new float[2048];
    private static int _frameIndex;
    private static int _frameCount;

    /// <summary>Note this frame's length for the dump's frame rate; call every frame.</summary>
    public static void SampleFrame()
    {
        FrameTimes[_frameIndex] = Time.unscaledDeltaTime;
        _frameIndex = (_frameIndex + 1) % FrameTimes.Length;
        _frameCount = Math.Min(_frameCount + 1, FrameTimes.Length);
    }

    public static void Dump()
    {
        var log = new MelonLogger.Instance("RouteMaker.Diag");
        try
        {
            // First, before the dump's own slow work.
            DumpFrameRate(log);
            log.Msg($"Render pipeline: {(GraphicsSettings.currentRenderPipeline == null ? "built-in" : GraphicsSettings.currentRenderPipeline.name)}");
            var camera = Camera.main;
            if (camera != null)
            {
                log.Msg($"Main camera: {camera.name} path={camera.actualRenderingPath} depthMode={camera.depthTextureMode} hdr={camera.allowHDR}");
            }

            log.Msg($"World state:\n{GameApi.DescribeWorldState()}");
            DumpDecals(log);
            DumpShaders(log);
            DumpSurfaceUnderCamera(log, camera);
        }
        catch (Exception e)
        {
            log.Error($"Diagnostics failed: {e}");
        }
    }

    private static void DumpFrameRate(MelonLogger.Instance log)
    {
        var total = 0f;
        var slowest = 0f;
        var frames = 0;
        while (frames < _frameCount && total < FrameWindow)
        {
            var seconds = FrameTimes[(_frameIndex - 1 - frames + FrameTimes.Length) % FrameTimes.Length];
            total += seconds;
            slowest = MathF.Max(slowest, seconds);
            frames++;
        }

        if (frames > 0 && total > 0f)
        {
            log.Msg($"Frame rate over the last {total:0.0} s: {frames / total:0.0} fps ({total / frames * 1000f:0.0} ms a frame), slowest frame {slowest * 1000f:0.0} ms. "
                + $"vSyncCount={QualitySettings.vSyncCount} targetFrameRate={Application.targetFrameRate}");
        }
    }

    private static void DumpDecals(MelonLogger.Instance log)
    {
        var decals = UnityEngine.Object.FindObjectsOfType<TGBDecal>(true);
        log.Msg($"TGBDecal instances: {decals.Length}");
        log.Msg($"Decal system: {TGBDecalSystem.allDecalsCount} registered, {TGBDecalSystem.visibleDecalsCount} visible. Stats: {TGBDecalSystem.GetStatsMessage()}");
        var seen = new HashSet<string>();
        foreach (var decal in decals)
        {
            var material = decal.m_Material;
            var key = material == null ? "(null)" : material.name;
            if (!seen.Add(key) || seen.Count > 12)
            {
                continue;
            }

            log.Msg($"  decal '{decal.name}' layer={decal.layer} tint={decal.tint} scale={decal.transform.lossyScale} material='{key}'");
            if (material != null)
            {
                DumpMaterial(log, material);
            }
        }
    }

    private static void DumpMaterial(MelonLogger.Instance log, Material material)
    {
        var shader = material.shader;
        log.Msg($"    shader='{shader.name}' queue={material.renderQueue} keywords=[{string.Join(",", material.shaderKeywords)}]");
        for (var i = 0; i < shader.GetPropertyCount(); i++)
        {
            var name = shader.GetPropertyName(i);
            var type = shader.GetPropertyType(i);
            var value = type switch
            {
                ShaderPropertyType.Color => material.GetColor(name).ToString(),
                ShaderPropertyType.Vector => material.GetVector(name).ToString(),
                ShaderPropertyType.Float or ShaderPropertyType.Range => material.GetFloat(name).ToString("0.###"),
                ShaderPropertyType.Texture => material.GetTexture(name) is { } t ? $"{t.name} {t.width}x{t.height}" : "none",
                _ => "",
            };
            log.Msg($"      {name} ({type}) = {value}");
        }
    }

    private static void DumpShaders(MelonLogger.Instance log)
    {
        foreach (var shader in Resources.FindObjectsOfTypeAll<Shader>())
        {
            var name = shader.name;
            if (name.Contains("Decal", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Unlit", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Internal-Colored", StringComparison.OrdinalIgnoreCase))
            {
                log.Msg($"Shader available: {name}");
            }
        }
    }

    private static void DumpSurfaceUnderCamera(MelonLogger.Instance log, Camera camera)
    {
        if (camera == null)
        {
            return;
        }

        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (!Physics.Raycast(ray, out var hit, 300f, GameApi.SurfaceMask, QueryTriggerInteraction.Ignore))
        {
            log.Msg("No surface straight ahead of the camera.");
            return;
        }

        var collider = hit.collider;
        log.Msg($"Surface ahead: '{collider.name}' layer={LayerMask.LayerToName(collider.gameObject.layer)} type={collider.GetIl2CppType().Name} distance={hit.distance:0.0}");
        var meshCollider = collider.TryCast<MeshCollider>();
        if (meshCollider != null && meshCollider.sharedMesh != null)
        {
            var mesh = meshCollider.sharedMesh;
            log.Msg($"  collider mesh '{mesh.name}' readable={mesh.isReadable} vertices={mesh.vertexCount}");
        }

        var renderer = collider.GetComponent<MeshRenderer>();
        if (renderer != null && renderer.sharedMaterial != null)
        {
            log.Msg($"  renderer material '{renderer.sharedMaterial.name}' shader='{renderer.sharedMaterial.shader.name}'");
        }
    }
}
