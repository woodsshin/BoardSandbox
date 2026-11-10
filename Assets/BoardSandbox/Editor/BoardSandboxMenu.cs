using System;
using System.IO;
using BoardSandbox.Core.Luau;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BoardSandbox.Client.Editor
{
    public static class BoardSandboxMenu
    {
        private const string ScenePath = "Assets/BoardSandbox/Scenes/Board.unity";

        [MenuItem("Board Sandbox/Check Native Plugin")]
        public static void CheckNativePlugin()
        {
            try
            {
                string version = LuauVm.NativeLuauVersion;
                EditorUtility.DisplayDialog("Board Sandbox", "The native Luau library is loaded (Luau " + version + ").", "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Board Sandbox", "The native Luau library could not be used:\n\n" + e.Message, "OK");
            }
        }

        [MenuItem("Board Sandbox/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects);
            new GameObject("Board Sandbox").AddComponent<BoardSandboxApp>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
                return;
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            AssetDatabase.Refresh();
        }
    }
}
