using System;
using System.Collections.Generic;
using System.IO;
using Core;
using UnityEngine;

namespace Save
{
    /// <summary>
    /// PURPOSE: Generic persistence layer used by every other manager instead of each
    ///          one talking to PlayerPrefs/disk directly.
    ///   - Settings: small values (volumes, rebinds, toggles) - PlayerPrefs-backed.
    ///     These are static (PlayerPrefs is global) and are written to disk on Flush(),
    ///     on application pause/quit, and when this manager is destroyed - not on every
    ///     call, so dragging a slider doesn't hammer the disk/registry.
    ///   - Game saves: slot-based, file-backed JSON for larger structured data
    ///     (player progress, inventory, world state) you'd want to browse, back up,
    ///     or ship multiple slots of. These need the SaveManager instance.
    /// DEPENDENCIES: Core (Singleton) only.
    /// PUBLIC API (static):   SaveFloat/LoadFloat, SaveInt/LoadInt, SaveBool/LoadBool,
    ///                        SaveString/LoadString, SaveObject/TryLoadObject,
    ///                        HasSetting, DeleteSetting, Flush
    /// PUBLIC API (instance): SaveGame/TryLoadGame/HasSave/DeleteSave/GetSaveSlots
    /// </summary>
    public class SaveManager : Singleton<SaveManager>
    {
        private const string SaveFileExtension = ".json";
        private static string SaveDirectory => Path.Combine(Application.persistentDataPath, "Saves");

        private static bool _dirty;

        protected override void Awake()
        {
            base.Awake();
            if (IsDuplicate) return;
            EnsureSaveDirectoryExists();
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            Flush();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused) Flush();
        }

        private void OnApplicationQuit() => Flush();

        // ---------------- Settings (PlayerPrefs) ----------------

        public static void SaveFloat(string key, float value) { PlayerPrefs.SetFloat(key, value); _dirty = true; }
        public static float LoadFloat(string key, float defaultValue = 0f) => PlayerPrefs.GetFloat(key, defaultValue);

        public static void SaveInt(string key, int value) { PlayerPrefs.SetInt(key, value); _dirty = true; }
        public static int LoadInt(string key, int defaultValue = 0) => PlayerPrefs.GetInt(key, defaultValue);

        public static void SaveBool(string key, bool value) { PlayerPrefs.SetInt(key, value ? 1 : 0); _dirty = true; }
        public static bool LoadBool(string key, bool defaultValue = false) => PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;

        public static void SaveString(string key, string value) { PlayerPrefs.SetString(key, value); _dirty = true; }
        public static string LoadString(string key, string defaultValue = "") => PlayerPrefs.GetString(key, defaultValue);

        public static bool HasSetting(string key) => PlayerPrefs.HasKey(key);
        public static void DeleteSetting(string key) { PlayerPrefs.DeleteKey(key); _dirty = true; }

        /// <summary>Writes pending settings changes to disk. Call after a batch of changes
        /// (e.g. when a settings screen closes); also runs automatically on pause/quit.</summary>
        public static void Flush()
        {
            if (!_dirty) return;
            PlayerPrefs.Save();
            _dirty = false;
        }

        /// <summary>
        /// For plain [Serializable] classes (not primitives, not generics - JsonUtility
        /// can't serialize either at the root).
        /// </summary>
        public static void SaveObject<T>(string key, T value) where T : class
        {
            try
            {
                PlayerPrefs.SetString(key, JsonUtility.ToJson(value));
                _dirty = true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Failed to save object '{key}': {e}");
            }
        }

        public static bool TryLoadObject<T>(string key, out T value) where T : class
        {
            value = null;
            if (!PlayerPrefs.HasKey(key)) return false;

            try
            {
                value = JsonUtility.FromJson<T>(PlayerPrefs.GetString(key));
                return value != null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Failed to load object '{key}': {e}");
                return false;
            }
        }

        // ---------------- Slot-based game saves (file-backed JSON) ----------------

        public bool SaveGame<T>(string slot, T data) where T : class
        {
            if (!IsValidSlotName(slot)) return false;

            try
            {
                EnsureSaveDirectoryExists();
                string json = JsonUtility.ToJson(data, prettyPrint: true);
                string finalPath = GetSavePath(slot);
                string tempPath = finalPath + ".tmp";

                // Write to a temp file first, then swap it in, so a crash mid-write can't
                // corrupt an existing save. File.Replace swaps atomically on the same volume.
                File.WriteAllText(tempPath, json);
                if (File.Exists(finalPath))
                    File.Replace(tempPath, finalPath, null);
                else
                    File.Move(tempPath, finalPath);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Failed to save slot '{slot}': {e}");
                return false;
            }
        }

        public bool TryLoadGame<T>(string slot, out T data) where T : class
        {
            data = null;
            if (!IsValidSlotName(slot)) return false;
            string path = GetSavePath(slot);
            if (!File.Exists(path)) return false;

            try
            {
                data = JsonUtility.FromJson<T>(File.ReadAllText(path));
                return data != null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Failed to load slot '{slot}': {e}");
                return false;
            }
        }

        public bool HasSave(string slot) => IsValidSlotName(slot) && File.Exists(GetSavePath(slot));

        public void DeleteSave(string slot)
        {
            if (!IsValidSlotName(slot)) return;
            string path = GetSavePath(slot);
            if (File.Exists(path)) File.Delete(path);
        }

        public IEnumerable<string> GetSaveSlots()
        {
            EnsureSaveDirectoryExists();
            foreach (string file in Directory.GetFiles(SaveDirectory, "*" + SaveFileExtension))
                yield return Path.GetFileNameWithoutExtension(file);
        }

        private static string GetSavePath(string slot) => Path.Combine(SaveDirectory, slot + SaveFileExtension);

        /// <summary>Slot names become file names, so reject empty names, path separators and other invalid characters.</summary>
        private static bool IsValidSlotName(string slot)
        {
            if (string.IsNullOrWhiteSpace(slot))
            {
                Debug.LogError("[SaveManager] Save slot name cannot be null or empty.");
                return false;
            }

            if (slot.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || slot.Contains(".."))
            {
                Debug.LogError($"[SaveManager] Save slot name '{slot}' contains invalid characters.");
                return false;
            }

            return true;
        }

        private static void EnsureSaveDirectoryExists()
        {
            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);
        }
    }
}
