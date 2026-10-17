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
    public static void Dump()
    {
        var log = new MelonLogger.Instance("RouteMaker.Diag");
        try
        {
            log.Msg($"Render pipeline: {(GraphicsSettings.currentRenderPipeline == null ? "built-in" : GraphicsSettings.currentRenderPipeline.name)}");
            var camera = Camera.main;
            if (camera != null)
            {
                log.Msg($"Main camera: {camera.name} path={camera.actualRenderingPath} depthMode={camera.depthTextureMode} hdr={camera.allowHDR}");
            }

            DumpDecals(log);
            DumpShaders(log);
            DumpSurfaceUnderCamera(log, camera);
        }
        catch (Exception e)
        {
            log.Error($"Diagnostics failed: {e}");
        }
    }

    private static void DumpDecals(MelonLogger.Instance log)
    {
        var decals = UnityEngine.Object.FindObjectsOfType<TGBDecal>(true);
        log.Msg($"TGBDecal instances: {decals.Length}");
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
