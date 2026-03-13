#if UNITY_EDITOR
using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace Plysync.Editor
{
	public static class ItemsImporter
	{
		public static void Import(ItemsData items, Action<string> log, CancellationToken ct)
		{
			if (items == null)
			{
				log("No items block provided.");
				return;
			}

			// Placeholder: You’ll likely materialize ScriptableObjects / prefabs / configs.
			int count = items.list?.Length ?? 0;
			log($"Items import placeholder. items={count}");

			// Example: write a simple asset with raw json configs later, etc.
		}
	}
}
#endif