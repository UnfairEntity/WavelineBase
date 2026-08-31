using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Core
{
    /// <summary>
    /// PURPOSE: Generic persistence layer used by every other manager instead of each
    ///          one talking to PlayerPrefs/disk directly.
    ///   - Settings: small values (volumes, rebinds, toggles) - PlayerPrefs-backed.
    ///   - Game saves: slot-based, file-backed JSON for larger structured data
    ///     (player progress, inventory, world state) you'd want to browse, back up,
    ///     or ship multiple slots of.
    /// DEPENDENCIES: None beyond UnityEngine + Input System (for the rebind helpers).
    /// PUBLIC API: SaveFloat/LoadFloat, SaveInt/LoadInt, SaveBool/LoadBool,
    ///             SaveString/LoadString, SaveObject/TryLoadObject,
    ///             SaveGame/TryLoadGame/HasSave/DeleteSave/GetSaveSlots,
    ///             SavePlayerRebinds/LoadPlayerRebinds/ClearPlayerRebinds
    /// </summary>
    public class SaveManager : Singleton<SaveManager>
    {
        private const string SaveFileExtension = ".json";
        private string SaveDirectory => Application.persistentDataPath + "/Saves/";

        protected override void Awake()
        {
            base.Awake();
            if (IsDuplicate) return;
            EnsureSaveDirectoryExists();
        }

        // ---------------- Settings (PlayerPrefs) ----------------

        public void SaveFloat(string key, float value) { PlayerPrefs.SetFloat(key, value); PlayerPrefs.Save(); }
        public float LoadFloat(string key, float defaultValue = 0f) => PlayerPrefs.GetFloat(key, defaultValue);

        public void SaveInt(string key, int value) { PlayerPrefs.SetInt(key, value); PlayerPrefs.Save(); }
        public int LoadInt(string key, int defaultValue = 0) => PlayerPrefs.GetInt(key, defaultValue);

        public void SaveBool(string key, bool value) { PlayerPrefs.SetInt(key, value ? 1 : 0); PlayerPrefs.Save(); }
        public bool LoadBool(string key, bool defaultValue = false) => PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) == 1;

        public void SaveString(string key, string value) { PlayerPrefs.SetString(key, value); PlayerPrefs.Save(); }
        public string LoadString(string key, string defaultValue = "") => PlayerPrefs.GetString(key, defaultValue);

        public bool HasSetting(string key) => PlayerPrefs.HasKey(key);
        public void DeleteSetting(string key) => PlayerPrefs.DeleteKey(key);

        /// <summary>
        /// For plain [Serializable] classes (not primitives, not generics - JsonUtility
        /// can't serialize either at the root).
        /// </summary>
        public void SaveObject<T>(string key, T value) where T : class
        {
            try
            {
                PlayerPrefs.SetString(key, JsonUtility.ToJson(value));
                PlayerPrefs.Save();
            }
            catch (Exception e)
            {
                Debug.LogError($"[SaveManager] Failed to save object '{key}': {e}");
            }
        }

        public bool TryLoadObject<T>(string key, out T value) where T : class
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
            if (string.IsNullOrEmpty(slot))
            {
                Debug.LogError("[SaveManager] Save slot name cannot be null or empty.");
                return false;
            }

            try
            {
                EnsureSaveDirectoryExists();
                string json = JsonUtility.ToJson(data, prettyPrint: true);
                string finalPath = GetSavePath(slot);
                string tempPath = finalPath + ".tmp";

                // Write-then-replace so a crash mid-write can't corrupt an existing save.
                File.WriteAllText(tempPath, json);
                if (File.Exists(finalPath)) File.Delete(finalPath);
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

        public bool HasSave(string slot) => File.Exists(GetSavePath(slot));

        public void DeleteSave(string slot)
        {
            string path = GetSavePath(slot);
            if (File.Exists(path)) File.Delete(path);
        }

        public IEnumerable<string> GetSaveSlots()
        {
            EnsureSaveDirectoryExists();
            foreach (string file in Directory.GetFiles(SaveDirectory, "*" + SaveFileExtension))
                yield return Path.GetFileNameWithoutExtension(file);
        }

        private string GetSavePath(string slot) => SaveDirectory + slot + SaveFileExtension;

        private void EnsureSaveDirectoryExists()
        {
            if (!Directory.Exists(SaveDirectory))
                Directory.CreateDirectory(SaveDirectory);
        }
    }
}
