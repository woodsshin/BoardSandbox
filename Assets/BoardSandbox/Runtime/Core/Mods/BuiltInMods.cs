using System.IO;

namespace BoardSandbox.Core.Mods
{
    // Where the games that come with the app are, as each platform keeps them. They are the Mods folder of Unity's
    // StreamingAssets folder.
    public static class BuiltInMods
    {
        public const string FolderName = "Mods";

        // The folder inside an Android APK that StreamingAssets is packed into.
        public const string AndroidAssetsFolder = "assets";

        // Where StreamingAssets is a folder on disk, which is everywhere except Android.
        public static IModSource FromFolder(string streamingAssetsPath)
        {
            return new FolderModSource(Path.Combine(streamingAssetsPath ?? string.Empty, FolderName), ModOrigin.BuiltIn);
        }

        // Android packs StreamingAssets into the APK, which Application.dataPath names. Its streamingAssetsPath
        // ("jar:file://...!/assets") is a path inside that archive and not a folder that System.IO can open.
        public static IModSource FromAndroidPackage(string apkPath)
        {
            return new ZipModSource(apkPath, AndroidAssetsFolder + "/" + FolderName, ModOrigin.BuiltIn);
        }
    }
}
