using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.U2D;
using YARG.Core.Logging;
using Object = UnityEngine.Object;

namespace YARG.Helpers
{
    /// <summary>
    /// Synchronous Addressables loads that also work on the web. WebGL cannot block on an Addressables operation
    /// (<c>WaitForCompletion</c> throws there), so on the web every asset of the built-in catalog is loaded once at
    /// startup by <see cref="PreloadAsync"/>, kept loaded, and <see cref="Load{T}"/> returns it from that cache.
    /// </summary>
    public static class SyncAddressables
    {
        private const string MAIN_CATALOG_ID = "AddressablesMainContentCatalog";

        // Every key (address, GUID, label) maps to the assets it locates
        private static readonly Dictionary<string, List<Object>> _assets = new();
        private static readonly UniTaskCompletionSource _ready = new();

        public static bool IsReady { get; private set; } = !PlatformInfo.IsWeb;

        /// <summary>
        /// Completes when <see cref="Load{T}"/> can be used. Immediately on desktop.
        /// </summary>
        public static UniTask WhenReady => IsReady ? UniTask.CompletedTask : _ready.Task;

        /// <summary>
        /// Loads an asset by key, including sub-asset keys like <c>InstrumentIcons[guitar]</c>.
        /// Returns null (and logs) on the web if the asset was not preloaded.
        /// </summary>
        public static T Load<T>(string key) where T : Object
        {
            if (!PlatformInfo.IsWeb)
            {
                return Addressables.LoadAssetAsync<T>(key).WaitForCompletion();
            }

            if (!IsReady)
            {
                YargLogger.LogFormatError("Addressable '{0}' requested before the web preload finished", key);
                return null;
            }

            var asset = Find<T>(key);
            if (asset == null)
            {
                YargLogger.LogFormatError<string, string>("Addressable '{0}' ({1}) is not in the web preload", key, typeof(T).Name);
            }
            return asset;
        }

        /// <summary>
        /// Loads every asset of the built-in catalog, then marks the cache as ready. Web only; a no-op on desktop.
        /// </summary>
        public static async UniTask PreloadAsync()
        {
            if (IsReady)
            {
                return;
            }

            try
            {
                await Addressables.InitializeAsync();

                // Each location is loaded once, then registered under every key that locates it
                var keysByLocation = new Dictionary<IResourceLocation, List<string>>();
                foreach (var locator in Addressables.ResourceLocators)
                {
                    if (locator.LocatorId != MAIN_CATALOG_ID)
                    {
                        continue;
                    }

                    foreach (var key in locator.Keys)
                    {
                        if (key is not string stringKey || !locator.Locate(stringKey, null, out var locations))
                        {
                            continue;
                        }

                        foreach (var location in locations)
                        {
                            if (!typeof(Object).IsAssignableFrom(location.ResourceType))
                            {
                                // Asset bundles and other non-asset locations
                                continue;
                            }

                            if (!keysByLocation.TryGetValue(location, out var keys))
                            {
                                keysByLocation[location] = keys = new List<string>();
                            }
                            keys.Add(stringKey);
                        }
                    }
                }

                foreach (var (location, keys) in keysByLocation)
                {
                    var assets = new List<Object>();
                    try
                    {
                        assets.Add(await Addressables.LoadAssetAsync<Object>(location));

                        // Sprite sheets are addressed as Sheet[SpriteName]
                        if (location.ResourceType == typeof(Texture2D) && HasSprites(location.PrimaryKey))
                        {
                            var sprites = await Addressables.LoadAssetAsync<IList<Sprite>>(location.PrimaryKey);
                            if (sprites != null)
                            {
                                assets.AddRange(sprites);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        YargLogger.LogException(e, $"Failed to preload addressable '{location.PrimaryKey}'");
                        continue;
                    }

                    foreach (var key in keys)
                    {
                        if (!_assets.TryGetValue(key, out var list))
                        {
                            _assets[key] = list = new List<Object>();
                        }
                        list.AddRange(assets);
                    }
                }

                YargLogger.LogFormatInfo("Preloaded {0} addressable assets for the web", keysByLocation.Count);
            }
            catch (Exception e)
            {
                YargLogger.LogException(e, "Failed to preload addressables for the web");
            }
            finally
            {
                IsReady = true;
                _ready.TrySetResult();
            }
        }

        private static bool HasSprites(string key)
        {
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (locator.LocatorId == MAIN_CATALOG_ID && locator.Locate(key, typeof(Sprite), out _))
                {
                    return true;
                }
            }
            return false;
        }

        private static T Find<T>(string key) where T : Object
        {
            if (_assets.TryGetValue(key, out var assets))
            {
                foreach (var asset in assets)
                {
                    if (asset is T typed)
                    {
                        return typed;
                    }
                }
            }

            // Sub-asset key: Main[Sub]
            int open = key.IndexOf('[');
            if (open > 0 && key.EndsWith("]"))
            {
                string main = key[..open];
                string sub = key[(open + 1)..^1];
                if (_assets.TryGetValue(main, out var mainAssets))
                {
                    foreach (var asset in mainAssets)
                    {
                        if (asset is T typed && asset.name == sub)
                        {
                            return typed;
                        }

                        if (asset is SpriteAtlas atlas && typeof(T) == typeof(Sprite))
                        {
                            var sprite = atlas.GetSprite(sub);
                            if (sprite != null)
                            {
                                return sprite as T;
                            }
                        }
                    }
                }
            }

            return null;
        }
    }
}
