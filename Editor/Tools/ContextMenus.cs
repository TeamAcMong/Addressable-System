using UnityEditor;
using UnityEngine;
using AddressableManager.Scopes;

namespace AddressableManager.Editor.Tools
{
    /// <summary>
    /// Context menu items for easy access to Addressable Manager features
    /// </summary>
    public static class ContextMenus
    {
        #region GameObject Context Menus

        [MenuItem("GameObject/Addressable Manager/Add Global Scope", false, 0)]
        private static void AddGlobalScope(MenuCommand command)
        {
            var go = GetOrCreateGameObject(command, "[GlobalAssetScope]");
            if (go.GetComponent<GlobalAssetScope>() == null)
            {
                Undo.AddComponent<GlobalAssetScope>(go);
                Debug.Log($"Added GlobalAssetScope to {go.name}");
            }
        }

        // SessionAssetScope was removed in 4.0.0 — sessions now route through
        // ScopeManager.GetOrCreateScope("Session"). No GameObject menu needed.

        [MenuItem("GameObject/Addressable Manager/Add Scene Scope", false, 2)]
        private static void AddSceneScope(MenuCommand command)
        {
            var go = GetOrCreateGameObject(command, "[SceneAssetScope]");
            if (go.GetComponent<SceneAssetScope>() == null)
            {
                Undo.AddComponent<SceneAssetScope>(go);
                Debug.Log($"Added SceneAssetScope to {go.name}");
            }
        }

        [MenuItem("GameObject/Addressable Manager/Add Hierarchy Scope", false, 3)]
        private static void AddHierarchyScope(MenuCommand command)
        {
            var go = GetOrCreateGameObject(command, "[HierarchyAssetScope]");
            if (go.GetComponent<HierarchyAssetScope>() == null)
            {
                Undo.AddComponent<HierarchyAssetScope>(go);
                Debug.Log($"Added HierarchyAssetScope to {go.name}");
            }
        }

        [MenuItem("GameObject/Addressable Manager/View in Dashboard", false, 20)]
        private static void ViewInDashboard(MenuCommand command)
        {
            Windows.AddressableManagerWindow.ShowWindow();
        }

        private static GameObject GetOrCreateGameObject(MenuCommand command, string defaultName)
        {
            GameObject go = command.context as GameObject;

            if (go == null)
            {
                go = new GameObject(defaultName);
                Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
                Selection.activeGameObject = go;
            }

            return go;
        }

        #endregion

        #region Asset Context Menus

        [MenuItem("Assets/Addressable Manager/Create Preload Config", false, 1000)]
        private static void CreatePreloadConfig()
        {
            CreateScriptableObject<Configs.AddressablePreloadConfig>("PreloadConfig");
        }

        [MenuItem("Assets/Addressable Manager/Create Pool Config", false, 1001)]
        private static void CreatePoolConfig()
        {
            CreateScriptableObject<Configs.PoolConfiguration>("PoolConfig");
        }

        [MenuItem("Assets/Addressable Manager/Create Debug Settings", false, 1002)]
        private static void CreateDebugSettings()
        {
            // DebugSettings.Instance loads from Resources/AddressableManager/, so that is where the
            // asset has to be created. It used to be written to Assets/DebugSettings.asset - a path
            // the loader never looks at - so the settings had no effect, the "no DebugSettings found"
            // prompt kept reappearing, and because GenerateUniqueAssetPath never overwrites, saying
            // yes to it produced DebugSettings 1, DebugSettings 2, and so on, forever.
            CreateScriptableObject<Configs.DebugSettings>("DebugSettings", DebugSettingsFolder);
        }

        /// <summary>Where <c>DebugSettings.Instance</c> looks. Must stay in step with it.</summary>
        private const string DebugSettingsFolder = "Assets/Resources/AddressableManager";

        private static void CreateScriptableObject<T>(string defaultName, string folder = "Assets")
            where T : ScriptableObject
        {
            if (!EnsureFolder(folder))
            {
                Debug.LogError($"[AddressableManager] Could not create the folder '{folder}'. " +
                               $"No {typeof(T).Name} was created.");
                return;
            }

            // An existing asset is selected rather than duplicated. Creating a second one here is
            // never what the user meant: the loaders read exactly one, so the extra copies are
            // invisible files that look like configuration and change nothing.
            string existing = $"{folder}/{defaultName}.asset";
            var alreadyThere = AssetDatabase.LoadAssetAtPath<T>(existing);
            if (alreadyThere != null)
            {
                EditorUtility.FocusProjectWindow();
                Selection.activeObject = alreadyThere;
                EditorGUIUtility.PingObject(alreadyThere);
                Debug.Log($"[AddressableManager] {typeof(T).Name} already exists at {existing}; selected it.");
                return;
            }

            var asset = ScriptableObject.CreateInstance<T>();
            AssetDatabase.CreateAsset(asset, existing);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;

            Debug.Log($"[AddressableManager] Created {typeof(T).Name} at {existing}");
        }

        /// <summary>Create a folder chain under Assets/ if it is not there. True when it exists after.</summary>
        private static bool EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return true;

            var parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") return false;

            string path = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = path + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(path, parts[i]);
                path = next;
            }

            return AssetDatabase.IsValidFolder(folder);
        }

        #endregion

        #region Top Menu Items

        // "Window/Addressable Manager/Dashboard" is registered by AddressableManagerWindow itself.
        // A second [MenuItem] for the same path used to live here, delegating straight back to it:
        // Unity accepts both registrations and does not define which handler runs, so the two were a
        // coin toss that happened to land the same way. Register a path once, in the class that owns
        // the thing it opens.

        [MenuItem("Window/Addressable Manager/Documentation", false, 20)]
        private static void OpenDocumentation()
        {
            // Packages/, not Assets/. A UPM package is never under Assets/, so this looked in a
            // folder that cannot exist, and then told the user to go and check that same wrong path -
            // an error message sending the reader somewhere the file could not be.
            const string readmePath = "Packages/com.game.addressables/README.md";

            if (AssetDatabase.LoadAssetAtPath<TextAsset>(readmePath) != null)
            {
                EditorUtility.OpenWithDefaultApp(readmePath);
                return;
            }

            EditorUtility.DisplayDialog("Documentation",
                $"README.md was not found at {readmePath}.\n\n" +
                "That usually means the package was vendored into Assets/ rather than installed " +
                "through the Package Manager. The same document is on the repository.",
                "OK");
        }

        [MenuItem("Window/Addressable Manager/Settings", false, 21)]
        private static void OpenSettings()
        {
            var settings = Resources.Load<Configs.DebugSettings>("AddressableManager/DebugSettings");

            if (settings != null)
            {
                Selection.activeObject = settings;
                EditorGUIUtility.PingObject(settings);
            }
            else
            {
                if (EditorUtility.DisplayDialog("Settings Not Found",
                    "No DebugSettings found in Resources/AddressableManager/.\n\n" +
                    "Would you like to create one?",
                    "Yes", "Cancel"))
                {
                    CreateDebugSettings();
                }
            }
        }

        [MenuItem("Window/Addressable Manager/Clear All Caches", false, 40)]
        private static void ClearAllCaches()
        {
            if (EditorUtility.DisplayDialog("Clear All Caches",
                "This will clear all asset tracker data and performance metrics.\n\n" +
                "This operation only affects Editor tracking data, not runtime assets.",
                "Clear", "Cancel"))
            {
                Data.AssetTrackerService.Instance.Clear();
                Data.PerformanceMetrics.Instance.Clear();

                Debug.Log("[AddressableManager] All caches cleared.");
            }
        }

        #endregion

        #region Quick Actions

        [MenuItem("Tools/Addressable Manager/Quick Setup/Create All Scope Objects", false, 100)]
        private static void CreateAllScopeObjects()
        {
            if (EditorUtility.DisplayDialog("Create All Scopes",
                "This will create GameObjects for the persistent scope types in the current scene:\n" +
                "• Global Scope (DontDestroyOnLoad)\n" +
                "• Scene Scope\n" +
                "• Hierarchy Scope\n\n" +
                "Session is now a ScopeManager entry — use\n" +
                "ScopeManager.Instance.GetOrCreateScope(\"Session\")\n" +
                "or Assets.StartSession() in code.\n\n" +
                "Continue?",
                "Yes", "Cancel"))
            {
                CreateScopeObject<GlobalAssetScope>("[GlobalAssetScope]");
                CreateScopeObject<SceneAssetScope>("[SceneAssetScope]");
                CreateScopeObject<HierarchyAssetScope>("[HierarchyAssetScope]");

                Debug.Log("[AddressableManager] Created scope GameObjects (Global / Scene / Hierarchy). For sessions use ScopeManager.GetOrCreateScope(\"Session\").");
            }
        }

        private static void CreateScopeObject<T>(string name) where T : Component
        {
            var existing = Object.FindAnyObjectByType<T>();
            if (existing != null)
            {
                Debug.LogWarning($"{typeof(T).Name} already exists on {existing.gameObject.name}");
                return;
            }

            var go = new GameObject(name);
            go.AddComponent<T>();
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        }

        [MenuItem("Tools/Addressable Manager/Quick Setup/Create Sample Configs", false, 101)]
        private static void CreateSampleConfigs()
        {
            if (EditorUtility.DisplayDialog("Create Sample Configs",
                "This will create sample configuration files:\n" +
                "• PreloadConfig.asset\n" +
                "• PoolConfig.asset\n" +
                "• DebugSettings.asset\n\n" +
                "These will be created in Assets/ folder.\n\n" +
                "Continue?",
                "Yes", "Cancel"))
            {
                CreateScriptableObject<Configs.AddressablePreloadConfig>("PreloadConfig");
                CreateScriptableObject<Configs.PoolConfiguration>("PoolConfig");
                CreateScriptableObject<Configs.DebugSettings>("DebugSettings");

                Debug.Log("[AddressableManager] Created sample configuration files.");
            }
        }

        #endregion
    }
}
