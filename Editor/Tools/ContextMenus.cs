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

        [MenuItem("GameObject/Addressable Manager/Add Session Scope", false, 1)]
        private static void AddSessionScope(MenuCommand command)
        {
            var go = GetOrCreateGameObject(command, "[SessionAssetScope]");
            if (go.GetComponent<SessionAssetScope>() == null)
            {
                Undo.AddComponent<SessionAssetScope>(go);
                Debug.Log($"Added SessionAssetScope to {go.name}");
            }
        }

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
            CreateScriptableObject<Configs.DebugSettings>("DebugSettings");
        }

        private static void CreateScriptableObject<T>(string defaultName) where T : ScriptableObject
        {
            var asset = ScriptableObject.CreateInstance<T>();

            string path = "Assets/" + defaultName + ".asset";
            path = AssetDatabase.GenerateUniqueAssetPath(path);

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;

            Debug.Log($"Created {typeof(T).Name} at {path}");
        }

        #endregion

        #region Top Menu Items

        [MenuItem("Window/Addressable Manager/Dashboard %&a", false, 0)]
        private static void OpenDashboard()
        {
            Windows.AddressableManagerWindow.ShowWindow();
        }

        [MenuItem("Window/Addressable Manager/Documentation", false, 20)]
        private static void OpenDocumentation()
        {
            var readmePath = "Assets/com.game.addressables/README.md";
            var readme = AssetDatabase.LoadAssetAtPath<TextAsset>(readmePath);

            if (readme != null)
            {
                EditorUtility.OpenWithDefaultApp(readmePath);
            }
            else
            {
                EditorUtility.DisplayDialog("Documentation",
                    "README.md not found in package.\n\n" +
                    "Please check Assets/com.game.addressables/README.md",
                    "OK");
            }
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
                "This will create GameObjects for all scope types in the current scene:\n" +
                "• Global Scope (DontDestroyOnLoad)\n" +
                "• Session Scope\n" +
                "• Scene Scope\n" +
                "• Hierarchy Scope\n\n" +
                "Continue?",
                "Yes", "Cancel"))
            {
                CreateScopeObject<GlobalAssetScope>("[GlobalAssetScope]");
                CreateScopeObject<SessionAssetScope>("[SessionAssetScope]");
                CreateScopeObject<SceneAssetScope>("[SceneAssetScope]");
                CreateScopeObject<HierarchyAssetScope>("[HierarchyAssetScope]");

                Debug.Log("[AddressableManager] Created all scope objects.");
            }
        }

        private static void CreateScopeObject<T>(string name) where T : Component
        {
            var existing = Object.FindObjectOfType<T>();
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
