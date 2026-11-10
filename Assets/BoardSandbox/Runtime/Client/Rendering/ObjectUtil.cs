using UnityEngine;

namespace BoardSandbox.Client.Rendering
{
    public static class ObjectUtil
    {
        // Destroys an object the way the current mode requires.
        public static void Destroy(Object value)
        {
            if (value == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(value);
            else
                Object.DestroyImmediate(value);
        }
    }
}
