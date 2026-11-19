using System;
using UnityEditor;
using UnityEngine;

namespace BoardSandbox.Client.Editor
{
    // Repairs an Android project that was exported before EosAndroidLibraryBuildStep existed, or by an editor that did not run it,
    // without exporting again. It does to the folder what the build step does to a project that Unity has just generated.
    public static class EosAndroidProjectMenu
    {
        [MenuItem("Board Sandbox/Fix Exported Android Project (EOS)...")]
        public static void FixExportedProject()
        {
            string folder = EditorUtility.OpenFolderPanel("The folder that Unity exported (it holds unityLibrary)", string.Empty, string.Empty);
            if (string.IsNullOrEmpty(folder))
                return;

            try
            {
                EosAndroidProjectFix.Result result = EosAndroidProjectFix.Adapt(folder);
                Debug.Log("[EOS Android] " + result.Describe());
                EditorUtility.DisplayDialog("Board Sandbox", result.Describe(), "OK");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorUtility.DisplayDialog("Board Sandbox", "The exported project could not be fixed:\n\n" + e.Message, "OK");
            }
        }
    }
}
