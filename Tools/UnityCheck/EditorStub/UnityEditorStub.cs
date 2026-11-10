// The part of the UnityEditor API that the editor scripts use, declared with the signatures of the
// Unity reference source, so that those scripts can be compiled without an editor installation.
using System;
using UnityEngine.SceneManagement;

namespace UnityEditor
{
    public enum BuildTarget
    {
        StandaloneWindows64 = 19
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class MenuItem : Attribute
    {
        public MenuItem(string itemName)
        {
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class InitializeOnLoadAttribute : Attribute
    {
    }

    public static class EditorApplication
    {
        public delegate void CallbackFunction();

        public static CallbackFunction delayCall;
    }

    public static class EditorUtility
    {
        public static bool DisplayDialog(string title, string message, string ok)
        {
            return true;
        }
    }

    public class AssetImporter
    {
        public static AssetImporter GetAtPath(string path)
        {
            return null;
        }

        public void SaveAndReimport()
        {
        }
    }

    public sealed class PluginImporter : AssetImporter
    {
        public void SetCompatibleWithAnyPlatform(bool enable)
        {
        }

        public bool GetCompatibleWithAnyPlatform()
        {
            return false;
        }

        public void SetCompatibleWithEditor(bool enable)
        {
        }

        public bool GetCompatibleWithEditor()
        {
            return false;
        }

        public void SetCompatibleWithPlatform(BuildTarget platform, bool enable)
        {
        }

        public bool GetCompatibleWithPlatform(BuildTarget platform)
        {
            return false;
        }

        public void SetPlatformData(BuildTarget platform, string key, string value)
        {
        }

        public string GetPlatformData(BuildTarget platform, string key)
        {
            return null;
        }

        public void SetEditorData(string key, string value)
        {
        }

        public string GetEditorData(string key)
        {
            return null;
        }
    }

    public static class AssetDatabase
    {
        public static string[] GetAllAssetPaths()
        {
            return null;
        }

        public static void Refresh()
        {
        }
    }

    public class EditorBuildSettingsScene
    {
        public EditorBuildSettingsScene(string path, bool enabled)
        {
        }
    }

    public static class EditorBuildSettings
    {
        public static EditorBuildSettingsScene[] scenes { get; set; }
    }
}

namespace UnityEditor.SceneManagement
{
    public enum NewSceneSetup
    {
        EmptyScene,
        DefaultGameObjects
    }

    public enum NewSceneMode
    {
        Single,
        Additive
    }

    public static class EditorSceneManager
    {
        public static Scene NewScene(NewSceneSetup setup)
        {
            return default;
        }

        public static bool SaveScene(Scene scene, string dstScenePath)
        {
            return true;
        }

        public static bool SaveCurrentModifiedScenesIfUserWantsTo()
        {
            return true;
        }
    }
}
