using UnityEngine;

namespace Plyground.Sync.Runtime
{
    public sealed class SceneMarker : MonoBehaviour
    {
        public string gameId;
        public string revision;
        public string localServerBaseUrl; // optional (for debugging)
        public string importedAtUtc;

        // ---- Sync build info (from GET /sync/list) ----
        public string syncRootPath;       // maps to "path" in /sync/list
        public string environmentPath;    // environmentPath
        public string gameItemPath;       // gameItemPath
        public string buildFilePath;      // buildFilePath (optional)
        public string modulePath;         // modulePath (optional)
        public string assetPath;          // assetPath (optional)
    }
}