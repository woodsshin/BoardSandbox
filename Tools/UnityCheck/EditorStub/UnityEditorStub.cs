// The part of the UnityEditor API that the editor scripts use, declared with the signatures of the
// Unity reference source, so that those scripts can be compiled without an editor installation.
using System;
using UnityEngine.SceneManagement;

namespace UnityEditor
{
    public enum BuildTarget
    {
        Android = 13,
        StandaloneWindows64 = 19
    }

    [Flags]
    public enum AndroidArchitecture : uint
    {
        None = 0,
        ARMv7 = 1,
        ARM64 = 2,
        X86_64 = 8,
        All = 4294967295
    }

    public static class PlayerSettings
    {
        public static class Android
        {
            public static AndroidArchitecture targetArchitectures { get; set; }
        }
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

        public static string OpenFolderPanel(string title, string folder, string defaultName)
        {
            return null;
        }
    }

    public class AssetImporter
    {
        public string assetPath
        {
            get { return null; }
        }

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
        public static PluginImporter[] GetImporters(BuildTarget platform)
        {
            return null;
        }

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

namespace UnityEditor.Build
{
    public interface IOrderedCallback
    {
        int callbackOrder { get; }
    }

    public interface IPreprocessBuildWithReport : IOrderedCallback
    {
        void OnPreprocessBuild(UnityEditor.Build.Reporting.BuildReport report);
    }

    public class BuildFailedException : Exception
    {
        public BuildFailedException(string message) : base(message)
        {
        }

        public BuildFailedException(Exception innerException) : base(innerException.Message, innerException)
        {
        }
    }
}

namespace UnityEditor.Build.Reporting
{
    public struct BuildSummary
    {
        public BuildTarget platform
        {
            get { return default; }
        }
    }

    public class BuildReport
    {
        public BuildSummary summary
        {
            get { return default; }
        }
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
