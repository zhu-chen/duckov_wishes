using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Duckov.Utilities;
using ItemStatsSystem;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace duckov_wishes
{
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        // Configurable wishes (TypeIDs) and weight multiplier
        [SerializeField] private List<int> wishedItemTypeIds = new List<int>();
        [SerializeField] private int _weightMultiplier = 2; //1-50

        // Track changes to revert later
        private readonly Dictionary<LootSpawner, List<int>> _spawnerAddedFixed = new Dictionary<LootSpawner, List<int>>();
        private readonly Dictionary<LootBoxLoader, List<int>> _lootBoxAddedFixed = new Dictionary<LootBoxLoader, List<int>>();
        private readonly Dictionary<UnityEngine.Object, bool> _originalRandomFromPool = new Dictionary<UnityEngine.Object, bool>();
        private readonly Dictionary<LootBoxLoader, float> _originalFixedItemSpawnChance = new Dictionary<LootBoxLoader, float>();

        // Simple runtime UI
        private GameObject _uiRoot;
        private InputField _idsInput;
        private InputField _multiplierInput;
        private Button _applyButton;
        private Button _resetButton;
        private Text _statusText;
        private bool _uiVisible;
        private static Font _runtimeFont;

        // Public API
        public void SetWishes(IEnumerable<int> typeIds)
        {
            wishedItemTypeIds = typeIds?.Distinct().ToList() ?? new List<int>();
            RevertAll();
            ApplyToAllActiveScenes();
        }

        public void SetWishesWithMultiplier(IEnumerable<int> typeIds, int weightMultiplier)
        {
            _weightMultiplier = Mathf.Clamp(weightMultiplier, 1, 50);
            SetWishes(typeIds);
        }

        protected override void OnAfterSetup()
        {
            ApplyToAllActiveScenes();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        protected override void OnBeforeDeactivate()
        {
            try
            {
                RevertAll();
            }
            catch (Exception e)
            {
                Debug.LogError($"[duckov_wishes] Revert failed: {e}");
            }
            finally
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                DestroyUI();
            }
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                ToggleUI();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                ApplyInScene(scene);
            }
            catch (Exception e)
            {
                Debug.LogError($"[duckov_wishes] ApplyInScene error: {e}");
            }
        }

        private void ApplyToAllActiveScenes()
        {
            if (wishedItemTypeIds == null || wishedItemTypeIds.Count == 0)
                return;

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded)
                    ApplyInScene(scene);
            }
        }

        private void ApplyInScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;
            if (wishedItemTypeIds == null || wishedItemTypeIds.Count == 0)
                return;

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var spawner in root.GetComponentsInChildren<LootSpawner>(true))
                {
                    try { BoostSpawner(spawner); }
                    catch (Exception e) { Debug.LogWarning($"[duckov_wishes] BoostSpawner failed on {spawner?.name}: {e}"); }
                }

                foreach (var box in root.GetComponentsInChildren<LootBoxLoader>(true))
                {
                    try { BoostLootBox(box); }
                    catch (Exception e) { Debug.LogWarning($"[duckov_wishes] BoostLootBox failed on {box?.name}: {e}"); }
                }
            }
        }

        private void BoostSpawner(LootSpawner spawner)
        {
            if (spawner == null) return;

            if (!_spawnerAddedFixed.ContainsKey(spawner))
                _spawnerAddedFixed[spawner] = new List<int>();

            bool poolBoosted = TryBoostRandomPool(spawner, "randomPool", typeof(LootSpawner));

            var fixedItemsField = typeof(LootSpawner).GetField("fixedItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fixedItemsField != null)
            {
                var fixedItems = fixedItemsField.GetValue(spawner) as List<int>;
                if (fixedItems != null)
                {
                    foreach (var id in wishedItemTypeIds)
                    {
                        if (!fixedItems.Contains(id))
                        {
                            fixedItems.Add(id);
                            _spawnerAddedFixed[spawner].Add(id);
                        }
                    }
                }
            }

            var randomFromPoolField = typeof(LootSpawner).GetField("randomFromPool", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (randomFromPoolField != null && poolBoosted)
            {
                if (!_originalRandomFromPool.ContainsKey(spawner))
                    _originalRandomFromPool[spawner] = (bool)randomFromPoolField.GetValue(spawner);
                randomFromPoolField.SetValue(spawner, true);
            }

            try { spawner.CalculateChances(); } catch { }
        }

        private void BoostLootBox(LootBoxLoader box)
        {
            if (box == null) return;

            if (!_lootBoxAddedFixed.ContainsKey(box))
                _lootBoxAddedFixed[box] = new List<int>();

            bool poolBoosted = TryBoostRandomPool(box, "randomPool", typeof(LootBoxLoader));

            var fixedItemsField = typeof(LootBoxLoader).GetField("fixedItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fixedItemsField != null)
            {
                var fixedItems = fixedItemsField.GetValue(box) as List<int>;
                if (fixedItems != null)
                {
                    foreach (var id in wishedItemTypeIds)
                    {
                        if (!fixedItems.Contains(id))
                        {
                            fixedItems.Add(id);
                            _lootBoxAddedFixed[box].Add(id);
                        }
                    }
                }
            }

            var fixedChanceField = typeof(LootBoxLoader).GetField("fixedItemSpawnChance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (fixedChanceField != null)
            {
                float original = (float)fixedChanceField.GetValue(box);
                if (!_originalFixedItemSpawnChance.ContainsKey(box))
                    _originalFixedItemSpawnChance[box] = original;
                float boosted = Mathf.Clamp01(Mathf.Max(original, 0.5f + 0.05f * Mathf.Clamp(_weightMultiplier, 1, 50)));
                fixedChanceField.SetValue(box, boosted);
            }

            var randomFromPoolField = typeof(LootBoxLoader).GetField("randomFromPool", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (randomFromPoolField != null && poolBoosted)
            {
                if (!_originalRandomFromPool.ContainsKey(box))
                    _originalRandomFromPool[box] = (bool)randomFromPoolField.GetValue(box);
                randomFromPoolField.SetValue(box, true);
            }

            try { box.CalculateChances(); } catch { }
        }

        private bool TryBoostRandomPool(object host, string fieldName, Type hostType)
        {
            try
            {
                var poolField = hostType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (poolField == null) return false;
                var pool = poolField.GetValue(host);
                if (pool == null) return false;

                var poolType = pool.GetType();
                Type entryType = null;
                if (poolType.IsGenericType)
                {
                    var args = poolType.GetGenericArguments();
                    if (args != null && args.Length == 1)
                        entryType = args[0];
                }
                if (entryType == null) return false;

                var listField = poolType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(f => IsListOf(f.FieldType, entryType) || IsArrayOf(f.FieldType, entryType));

                object listObj = null;
                if (listField == null)
                {
                    var listProp = poolType.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .FirstOrDefault(p => p.CanRead && (IsListOf(p.PropertyType, entryType) || IsArrayOf(p.PropertyType, entryType)));
                    if (listProp == null) return false;
                    listObj = listProp.GetValue(pool);
                }
                else
                {
                    listObj = listField.GetValue(pool);
                }

                return TryEnsureEntries(listObj, entryType, Mathf.Clamp(_weightMultiplier, 1, 50));
            }
            catch
            {
                return false;
            }
        }

        private bool TryEnsureEntries(object listObj, Type entryType, int desiredCountPerId)
        {
            if (listObj == null) return false;

            if (listObj is System.Collections.IList list && listObj.GetType().IsGenericType)
            {
                var idField = entryType.GetField("itemTypeID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var id in wishedItemTypeIds)
                {
                    int existingCount = 0;
                    foreach (var existing in list)
                    {
                        if (existing != null && idField != null)
                        {
                            try
                            {
                                var val = (int)idField.GetValue(existing);
                                if (val == id) existingCount++;
                            }
                            catch { }
                        }
                    }

                    int toAdd = Mathf.Max(0, desiredCountPerId - existingCount);
                    for (int i = 0; i < toAdd; i++)
                    {
                        var entry = Activator.CreateInstance(entryType);
                        if (idField != null)
                            idField.SetValue(entry, id);
                        list.Add(entry);
                    }
                }
                return true;
            }

            return false;
        }

        private static bool IsListOf(Type t, Type elem)
        {
            return t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && t.GetGenericArguments()[0] == elem;
        }

        private static bool IsArrayOf(Type t, Type elem)
        {
            return t.IsArray && t.GetElementType() == elem;
        }

        private void RevertAll()
        {
            foreach (var kv in _spawnerAddedFixed.ToList())
            {
                var spawner = kv.Key;
                var added = kv.Value;
                if (spawner == null) continue;
                var fixedItemsField = typeof(LootSpawner).GetField("fixedItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var fixedItems = fixedItemsField?.GetValue(spawner) as List<int>;
                if (fixedItems != null)
                {
                    foreach (var id in added)
                        fixedItems.Remove(id);
                }
            }
            _spawnerAddedFixed.Clear();

            foreach (var kv in _lootBoxAddedFixed.ToList())
            {
                var box = kv.Key;
                var added = kv.Value;
                if (box == null) continue;
                var fixedItemsField = typeof(LootBoxLoader).GetField("fixedItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var fixedItems = fixedItemsField?.GetValue(box) as List<int>;
                if (fixedItems != null)
                {
                    foreach (var id in added)
                        fixedItems.Remove(id);
                }
            }
            _lootBoxAddedFixed.Clear();

            foreach (var kv in _originalFixedItemSpawnChance.ToList())
            {
                var box = kv.Key;
                if (box == null) continue;
                var fixedChanceField = typeof(LootBoxLoader).GetField("fixedItemSpawnChance", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                fixedChanceField?.SetValue(box, kv.Value);
            }
            _originalFixedItemSpawnChance.Clear();

            foreach (var kv in _originalRandomFromPool.ToList())
            {
                var host = kv.Key;
                if (host == null) continue;
                var t = host.GetType();
                var field = t.GetField("randomFromPool", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                field?.SetValue(host, kv.Value);
            }
            _originalRandomFromPool.Clear();
        }

        // UI
        private void ToggleUI()
        {
            _uiVisible = !_uiVisible;
            if (_uiVisible)
            {
                CreateUIIfNeeded();
                _uiRoot.SetActive(true);
            }
            else
            {
                if (_uiRoot != null) _uiRoot.SetActive(false);
            }
        }

        private void CreateUIIfNeeded()
        {
            if (_uiRoot != null) return;

            EnsureEventSystemExists();

            _uiRoot = new GameObject("WishesUI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            UnityEngine.Object.DontDestroyOnLoad(_uiRoot);
            var canvas = _uiRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 9999;
            var scaler = _uiRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
   panel.transform.SetParent(_uiRoot.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
       panelRect.anchorMin = new Vector2(1, 1);
            panelRect.anchorMax = new Vector2(1, 1);
            panelRect.pivot = new Vector2(1, 1);
            panelRect.anchoredPosition = new Vector2(-20, -20);
 panelRect.sizeDelta = new Vector2(460, 230);
    var panelImg = panel.GetComponent<Image>();
         panelImg.color = new Color(0f, 0f, 0f, 0.8f);

            Text CreateLabel(string name, Transform parent, string text, Vector2 anchoredPos)
            {
                var go = new GameObject(name, typeof(RectTransform), typeof(Text));
                go.transform.SetParent(parent, false);
                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
                rect.pivot = new Vector2(0, 1);
                rect.anchoredPosition = anchoredPos;
                rect.sizeDelta = new Vector2(420, 24);
                var t = go.GetComponent<Text>();
                t.text = text;
                t.font = GetRuntimeFont();
                t.color = Color.white;
                t.fontSize = 16;
                t.alignment = TextAnchor.MiddleLeft;
                return t;
            }

          InputField CreateInput(string name, Transform parent, string placeholder, Vector2 anchoredPos)
     {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
           go.transform.SetParent(parent, false);
    var rect = go.GetComponent<RectTransform>();
    rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
          rect.pivot = new Vector2(0, 1);
     rect.anchoredPosition = anchoredPos;
       rect.sizeDelta = new Vector2(420, 30);
 var bg = go.GetComponent<Image>();
    bg.color = new Color(1, 1, 1, 0.1f);

       var textGO = new GameObject("Text", typeof(RectTransform), typeof(Text));
  textGO.transform.SetParent(go.transform, false);
     var textRect = textGO.GetComponent<RectTransform>();
           textRect.anchorMin = new Vector2(0, 0);
       textRect.anchorMax = new Vector2(1, 1);
      textRect.offsetMin = new Vector2(10, 6);
  textRect.offsetMax = new Vector2(-10, -6);
            var text = textGO.GetComponent<Text>();
     text.font = GetRuntimeFont();
        text.color = Color.white;
  text.fontSize = 16;
     text.alignment = TextAnchor.MiddleLeft;

    var phGO = new GameObject("Placeholder", typeof(RectTransform), typeof(Text));
          phGO.transform.SetParent(go.transform, false);
                var phRect = phGO.GetComponent<RectTransform>();
                phRect.anchorMin = new Vector2(0, 0);
   phRect.anchorMax = new Vector2(1, 1);
       phRect.offsetMin = new Vector2(10, 6);
         phRect.offsetMax = new Vector2(-10, -6);
       var ph = phGO.GetComponent<Text>();
     ph.font = GetRuntimeFont();
    ph.color = new Color(1, 1, 1, 0.5f);
     ph.fontSize = 16;
    ph.alignment = TextAnchor.MiddleLeft;
    ph.text = placeholder;

    var input = go.GetComponent<InputField>();
     input.textComponent = text;
     input.placeholder = ph;
       input.targetGraphic = bg;
 input.lineType = InputField.LineType.SingleLine;

       return input;
  }

          CreateLabel("LblIDs", panel.transform, "祈愿物品ID(用逗号分隔)", new Vector2(20, -20));
            _idsInput = CreateInput("IDsInput", panel.transform, "例如:101,202,303", new Vector2(20, -46));

   CreateLabel("LblMul", panel.transform, "倍率(整数,1-50)", new Vector2(20, -84));
            _multiplierInput = CreateInput("MulInput", panel.transform, _weightMultiplier.ToString(), new Vector2(20, -110));

          var btnGO = new GameObject("ApplyBtn", typeof(RectTransform), typeof(Image), typeof(Button));
    btnGO.transform.SetParent(panel.transform, false);
         var btnRect = btnGO.GetComponent<RectTransform>();
 btnRect.anchorMin = btnRect.anchorMax = new Vector2(0, 1);
     btnRect.pivot = new Vector2(0, 1);
    btnRect.anchoredPosition = new Vector2(20, -160);
            btnRect.sizeDelta = new Vector2(120, 36);
      var btnImg = btnGO.GetComponent<Image>();
        btnImg.color = new Color(0.2f, 0.6f, 0.9f, 0.9f);
     _applyButton = btnGO.GetComponent<Button>();
    _applyButton.targetGraphic = btnImg;

            var btnTextGO = new GameObject("Text", typeof(RectTransform), typeof(Text));
            btnTextGO.transform.SetParent(btnGO.transform, false);
     var btnTextRect = btnTextGO.GetComponent<RectTransform>();
            btnTextRect.anchorMin = new Vector2(0, 0);
 btnTextRect.anchorMax = new Vector2(1, 1);
    btnTextRect.offsetMin = Vector2.zero;
         btnTextRect.offsetMax = Vector2.zero;
     var btnText = btnTextGO.GetComponent<Text>();
            btnText.text = "应用";
        btnText.font = GetRuntimeFont();
            btnText.color = Color.white;
      btnText.fontSize = 16;
            btnText.alignment = TextAnchor.MiddleCenter;

            // Reset Button
            var resetBtnGO = new GameObject("ResetBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            resetBtnGO.transform.SetParent(panel.transform, false);
            var resetBtnRect = resetBtnGO.GetComponent<RectTransform>();
            resetBtnRect.anchorMin = resetBtnRect.anchorMax = new Vector2(0, 1);
            resetBtnRect.pivot = new Vector2(0, 1);
            resetBtnRect.anchoredPosition = new Vector2(150, -160);
            resetBtnRect.sizeDelta = new Vector2(120, 36);
            var resetBtnImg = resetBtnGO.GetComponent<Image>();
            resetBtnImg.color = new Color(0.9f, 0.2f, 0.2f, 0.9f);
            _resetButton = resetBtnGO.GetComponent<Button>();
            _resetButton.targetGraphic = resetBtnImg;

            var resetBtnTextGO = new GameObject("Text", typeof(RectTransform), typeof(Text));
            resetBtnTextGO.transform.SetParent(resetBtnGO.transform, false);
            var resetBtnTextRect = resetBtnTextGO.GetComponent<RectTransform>();
            resetBtnTextRect.anchorMin = Vector2.zero;
            resetBtnTextRect.anchorMax = Vector2.one;
            resetBtnTextRect.offsetMin = Vector2.zero;
            resetBtnTextRect.offsetMax = Vector2.zero;
            var resetBtnText = resetBtnTextGO.GetComponent<Text>();
            resetBtnText.text = "重置";
            resetBtnText.font = GetRuntimeFont();
            resetBtnText.color = Color.white;
            resetBtnText.fontSize = 16;
            resetBtnText.alignment = TextAnchor.MiddleCenter;

            var st = new GameObject("Status", typeof(RectTransform), typeof(Text));
            st.transform.SetParent(panel.transform, false);
            var stRect = st.GetComponent<RectTransform>();
            stRect.anchorMin = stRect.anchorMax = new Vector2(0, 1);
            stRect.pivot = new Vector2(0, 1);
            stRect.anchoredPosition = new Vector2(290, -160);
            stRect.sizeDelta = new Vector2(150, 36);
            _statusText = st.GetComponent<Text>();
            _statusText.font = GetRuntimeFont();
            _statusText.text = "按 F8 打开/关闭本面板";
        _statusText.color = Color.white;
    _statusText.fontSize = 14;
         _statusText.alignment = TextAnchor.MiddleLeft;

         _applyButton.onClick.AddListener(ApplyFromUI);
            _resetButton.onClick.AddListener(ApplyResetFromUI);

            _uiRoot.SetActive(false);
        }

        private void DestroyUI()
        {
            if (_uiRoot != null)
            {
                UnityEngine.Object.Destroy(_uiRoot);
                _uiRoot = null;
                _idsInput = null;
                _multiplierInput = null;
                _applyButton = null;
                _resetButton = null;
                _statusText = null;
                _uiVisible = false;
            }
        }

        private void ApplyFromUI()
        {
            try
            {
                var ids = ParseIDs(_idsInput != null ? _idsInput.text : string.Empty);
                int mul = _weightMultiplier;
                if (_multiplierInput != null && !string.IsNullOrWhiteSpace(_multiplierInput.text))
                {
                    if (int.TryParse(_multiplierInput.text.Trim(), out var parsed))
                        mul = Mathf.Clamp(parsed, 1, 50);
                }

                SetWishesWithMultiplier(ids, mul);

                if (_statusText != null)
                    _statusText.text = $"已应用: {ids.Count} 项, 倍率 x{mul}";
            }
            catch (Exception e)
            {
                if (_statusText != null)
                    _statusText.text = $"应用失败: {e.Message}";
            }
        }

        private void ApplyResetFromUI()
        {
            try
            {
                SetWishes(new List<int>());

                if (_idsInput != null)
                    _idsInput.text = string.Empty;

                if (_statusText != null)
                    _statusText.text = "已重置所有祈愿。";
            }
            catch (Exception e)
            {
                if (_statusText != null)
                    _statusText.text = $"重置失败: {e.Message}";
            }
        }

        private List<int> ParseIDs(string text)
        {
            var result = new List<int>();
            if (string.IsNullOrWhiteSpace(text)) return result;
            var parts = text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (int.TryParse(p.Trim(), out var id))
                    result.Add(id);
            }
            return result.Distinct().ToList();
        }

        private static Font GetRuntimeFont()
        {
            if (_runtimeFont != null) return _runtimeFont;
            string[] candidates = { "Microsoft YaHei", "微软雅黑", "Arial", "SimHei", "宋体" };
            foreach (var name in candidates)
            {
                try
                {
                    var f = Font.CreateDynamicFontFromOSFont(name, 16);
                    if (f != null)
                    {
                        _runtimeFont = f;
                        return _runtimeFont;
                    }
                }
                catch { }
            }
            try { _runtimeFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { _runtimeFont = null; }
            return _runtimeFont;
        }

        private static void EnsureEventSystemExists()
        {
            if (UnityEngine.Object.FindObjectOfType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                UnityEngine.Object.DontDestroyOnLoad(es);
            }
        }
    }
}
