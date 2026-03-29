#if UNITY_EDITOR
using UnityEngine;

namespace Plysync.Editor
{
	public sealed class SceneMarker : MonoBehaviour
	{
		public string gameId;
		public string variationId;
		public string revision;
		public string localServerBaseUrl;
		public string importedAtUtc;

		public string syncRootPath;
		public string environmentPath;
		public string gameItemPath;
		public string buildFilePath;
		public string modulePath;
		public string assetPath;
	}
}
#endif
