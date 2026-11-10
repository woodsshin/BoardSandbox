using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BoardSandbox.Client.Rendering
{
    // The materials of the table, created on demand and cached by color. The shaders ship in a Resources folder
    // so that they are part of every build; when they cannot be found a plain unlit shader stands in.
    public sealed class MaterialSet : IDisposable
    {
        public const string OpaqueShaderName = "BoardSandbox/Basic";
        public const string OverlayShaderName = "BoardSandbox/Overlay";

        private static readonly string[] OpaqueFallbacks = { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default" };
        private static readonly string[] OverlayFallbacks = { "Sprites/Default", "Unlit/Transparent Cutout", "Universal Render Pipeline/Unlit" };

        private readonly Shader _opaque;
        private readonly Shader _overlay;
        private readonly Dictionary<uint, Material> _pieces = new Dictionary<uint, Material>();
        private readonly Dictionary<uint, Material> _overlays = new Dictionary<uint, Material>();
        private readonly List<Material> _owned = new List<Material>();

        public MaterialSet()
        {
            _opaque = Find(OpaqueShaderName, OpaqueFallbacks);
            _overlay = Find(OverlayShaderName, OverlayFallbacks);
            if (_opaque == null || _overlay == null)
                throw new InvalidOperationException("No usable shader was found; '" + OpaqueShaderName + "' should be in a Resources folder.");

            Vertex = Own(new Material(_opaque) { name = "Board Sandbox Vertex Colors" });
            Vertex.color = Color.white;
            SetFloat(Vertex, "_Glossiness", 0.12f);
        }

        // Shows the colors stored in the vertices of the mesh.
        public Material Vertex { get; }

        public Material Piece(Color32 color)
        {
            uint key = Pack(color);
            if (_pieces.TryGetValue(key, out Material material))
                return material;
            material = Own(new Material(_opaque) { name = "Piece " + key.ToString("x8") });
            material.color = color;
            SetFloat(material, "_Glossiness", 0.55f);
            _pieces[key] = material;
            return material;
        }

        // A see-through tint; the alpha of the color decides how much shows.
        public Material Overlay(Color32 color)
        {
            uint key = Pack(color);
            if (_overlays.TryGetValue(key, out Material material))
                return material;
            material = Own(new Material(_overlay) { name = "Overlay " + key.ToString("x8") });
            material.color = color;
            _overlays[key] = material;
            return material;
        }

        public void Dispose()
        {
            foreach (Material material in _owned)
                ObjectUtil.Destroy(material);
            _owned.Clear();
            _pieces.Clear();
            _overlays.Clear();
        }

        private Material Own(Material material)
        {
            _owned.Add(material);
            return material;
        }

        private static void SetFloat(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static uint Pack(Color32 color)
        {
            return ((uint)color.r << 24) | ((uint)color.g << 16) | ((uint)color.b << 8) | color.a;
        }

        private static Shader Find(string preferred, string[] fallbacks)
        {
            Shader shader = Shader.Find(preferred);
            if (shader != null && shader.isSupported)
                return shader;
            foreach (string name in fallbacks)
            {
                shader = Shader.Find(name);
                if (shader != null && shader.isSupported)
                    return shader;
            }
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            return pipeline != null ? pipeline.defaultShader : null;
        }
    }
}
