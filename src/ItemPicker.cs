using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ItemPicker
{
    [BepInPlugin("qol.itempicker", "Item Picker", "1.0.0")]
    public class ItemPickerPlugin : BaseUnityPlugin
    {
        internal static ItemPickerPlugin Instance;
        internal static ManualLogSource Log;
        internal static KeyCode CurrentToggleKey = KeyCode.F8;
        internal static ConfigEntry<float> AutoOpenSeconds;

        private ConfigEntry<string> toggleKeyName;
        private static GameObject tickerObject;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            toggleKeyName = Config.Bind<string>("General", "ToggleKey", "F8",
                "开关物品菜单的按键（Unity KeyCode 名称，例如 F8、F9、Insert）");
            AutoOpenSeconds = Config.Bind<float>("General", "AutoOpenSeconds", 0f,
                "进入游戏后自动打开物品菜单的延迟秒数（0 = 不自动打开）");

            KeyCode parsed;
            if (Enum.TryParse<KeyCode>(toggleKeyName.Value, true, out parsed))
            {
                CurrentToggleKey = parsed;
            }
            else
            {
                Logger.LogWarning("无法解析按键名 '" + toggleKeyName.Value + "'，仍使用 F8。");
            }

            new Harmony("qol.itempicker").PatchAll(typeof(ItemPickerPlugin).Assembly);
            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo("物品生成器已加载：按 " + CurrentToggleKey + " 打开菜单。");
        }

        private static void EnsureTicker()
        {
            if (tickerObject != null)
            {
                return;
            }
            tickerObject = new GameObject("ItemPickerTicker");
            UnityEngine.Object.DontDestroyOnLoad(tickerObject);
            tickerObject.AddComponent<PickerTicker>();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureTicker();
            if (PickerUI.IsOpen)
            {
                PickerUI.Close();
            }
            PickerUI.ScheduleAutoOpen(AutoOpenSeconds.Value);
        }
    }

    // 菜单打开时屏蔽玩家操作（移动/攻击/快捷键），避免点图标时角色乱动
    [HarmonyPatch(typeof(PlayerCamera), "HandleInput")]
    internal static class PlayerInputBlockPatch
    {
        private static bool Prefix()
        {
            return !PickerUI.IsOpen;
        }
    }

    internal sealed class PickerTicker : MonoBehaviour
    {
        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(ItemPickerPlugin.CurrentToggleKey))
                {
                    PickerUI.Toggle();
                }
                else if (PickerUI.IsOpen && Input.GetKeyDown(KeyCode.Escape))
                {
                    PickerUI.HandleEscape();
                }
                PickerUI.Tick();
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogError("物品生成器 tick 出错：" + ex);
            }
        }
    }

    internal sealed class ItemEntry
    {
        public string Id;
        public string Name;
        public string Desc;
        public string Category;
        public Sprite Icon;
    }

    internal sealed class CellClick : MonoBehaviour, IPointerClickHandler
    {
        public ItemEntry Entry;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }
            int amount = 1;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                amount = 10;
            }
            else if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
            {
                amount = 100;
            }
            PickerUI.Spawn(Entry, amount);
        }
    }

    internal sealed class CellHover : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        public ItemEntry Entry;

        public void OnPointerEnter(PointerEventData eventData)
        {
            PickerUI.ShowTooltip(Entry);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            PickerUI.HideTooltip();
        }
    }

    internal static class PickerUI
    {
        public static bool IsOpen;

        private static GameObject root;
        private static RectTransform panel;
        private static RectTransform content;
        private static TextMeshProUGUI filterText;
        private static TextMeshProUGUI statusText;
        private static RectTransform categoryBar;

        private static readonly List<ItemEntry> allEntries = new List<ItemEntry>();
        private static readonly List<string> categories = new List<string>();
        private static bool itemsLoaded;
        private static string currentCategory = "";
        private static string filter = "";
        private static string pendingFilter = "";
        private static float rebuildAt = -1f;
        private static float autoOpenAt = -1f;
        private static TMP_FontAsset font;
        private static RectTransform tooltipRect;
        private static TextMeshProUGUI tooltipName;
        private static TextMeshProUGUI tooltipDesc;
        private static Image tooltipIcon;

        public static void ShowTooltip(ItemEntry e)
        {
            if (tooltipRect == null || e == null)
            {
                return;
            }
            tooltipName.text = e.Name;
            tooltipDesc.text = string.IsNullOrEmpty(e.Desc) ? "" : e.Desc;
            if (tooltipIcon != null)
            {
                tooltipIcon.sprite = e.Icon;
                tooltipIcon.enabled = e.Icon != null;
                tooltipIcon.color = e.Icon == null ? new Color(0.4f, 0.4f, 0.45f, 0.6f) : Color.white;
            }
            tooltipRect.gameObject.SetActive(true);
            tooltipRect.SetAsLastSibling();
            PositionTooltip();
        }

        public static void HideTooltip()
        {
            if (tooltipRect != null)
            {
                tooltipRect.gameObject.SetActive(false);
            }
        }

        private static void PositionTooltip()
        {
            if (tooltipRect == null || !tooltipRect.gameObject.activeSelf)
            {
                return;
            }
            Vector2 mouse = Input.mousePosition;
            float w = tooltipRect.sizeDelta.x;
            float h = tooltipRect.sizeDelta.y;
            float x = mouse.x + 28f;
            float y = mouse.y - h * 0.5f;
            if (x + w > Screen.width - 8f)
            {
                x = mouse.x - w - 28f;
            }
            if (y + h > Screen.height - 8f)
            {
                y = Screen.height - h - 8f;
            }
            if (y < 8f)
            {
                y = 8f;
            }
            tooltipRect.position = new Vector3(x, y, 0f);
        }

        public static void ScheduleAutoOpen(float seconds)
        {
            if (seconds > 0f)
            {
                autoOpenAt = Time.unscaledTime + seconds;
            }
        }

        public static void Toggle()
        {
            if (IsOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        public static void Open()
        {
            if (PlayerCamera.main == null || PlayerCamera.main.body == null)
            {
                return;
            }
            if (!itemsLoaded)
            {
                LoadItems();
            }
            if (root == null)
            {
                Build();
            }
            if (root == null)
            {
                return;
            }
            root.SetActive(true);
            IsOpen = true;
            RebuildGrid();
        }

        public static void Close()
        {
            HideTooltip();
            IsOpen = false;
            if (root != null)
            {
                root.SetActive(false);
            }
        }

        public static void HandleEscape()
        {
            if (!string.IsNullOrEmpty(filter))
            {
                filter = "";
                pendingFilter = "";
                RebuildGrid();
                return;
            }
            Close();
        }

        internal static void Tick()
        {
            if (!IsOpen && autoOpenAt > 0f && Time.unscaledTime >= autoOpenAt && PlayerCamera.main != null)
            {
                autoOpenAt = -1f;
                Open();
            }
            if (!IsOpen)
            {
                return;
            }
            if (tooltipRect != null && tooltipRect.gameObject.activeSelf)
            {
                PositionTooltip();
            }
            HandleTyping();
            if (rebuildAt > 0f && Time.unscaledTime >= rebuildAt)
            {
                rebuildAt = -1f;
                filter = pendingFilter;
                RebuildGrid();
            }
        }

        private static void HandleTyping()
        {
            string typed = Input.inputString;
            if (string.IsNullOrEmpty(typed))
            {
                return;
            }
            bool changed = false;
            for (int i = 0; i < typed.Length; i++)
            {
                char c = typed[i];
                if (c == '\b')
                {
                    if (filter.Length > 0)
                    {
                        filter = filter.Substring(0, filter.Length - 1);
                        changed = true;
                    }
                }
                else if (c == '\n' || c == '\r' || c == '\t')
                {
                    continue;
                }
                else if (!char.IsControl(c))
                {
                    filter += c;
                    changed = true;
                }
            }
            if (changed)
            {
                pendingFilter = filter;
                rebuildAt = Time.unscaledTime + 0.12f;
                RefreshFilterLabel();
            }
        }

        private static void LoadItems()
        {
            itemsLoaded = true;
            try
            {
                font = TMP_Settings.defaultFontAsset;
                GameObject[] prefabs = Resources.LoadAll<GameObject>("");
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < prefabs.Length; i++)
                {
                    GameObject prefab = prefabs[i];
                    if (prefab == null)
                    {
                        continue;
                    }
                    Item item = prefab.GetComponent<Item>();
                    if (item == null)
                    {
                        continue;
                    }
                    string id = prefab.name;
                    if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    {
                        continue;
                    }
                    ItemEntry entry = new ItemEntry();
                    entry.Id = id;
                    entry.Icon = FindIcon(prefab);
                    string name = Locale.GetItem(id);
                    string desc = Locale.GetItem(id + "dsc");
                    string category = "";
                    try
                    {
                        ItemInfo info = Item.GetItem(id);
                        if (info != null)
                        {
                            if (!string.IsNullOrEmpty(info.fullName))
                            {
                                name = info.fullName;
                            }
                            if (!string.IsNullOrEmpty(info.description))
                            {
                                desc = info.description;
                            }
                            if (!string.IsNullOrEmpty(info.category))
                            {
                                category = info.category;
                            }
                        }
                    }
                    catch
                    {
                    }
                    if (string.IsNullOrEmpty(name))
                    {
                        name = id;
                    }
                    entry.Name = name;
                    entry.Desc = desc;
                    entry.Category = category;
                    allEntries.Add(entry);
                }

                allEntries.Sort(delegate (ItemEntry a, ItemEntry b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                HashSet<string> cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < allEntries.Count; i++)
                {
                    if (!string.IsNullOrEmpty(allEntries[i].Category))
                    {
                        cats.Add(allEntries[i].Category);
                    }
                }
                categories.AddRange(cats);
                categories.Sort(StringComparer.OrdinalIgnoreCase);

                ItemPickerPlugin.Log.LogInfo("物品列表加载完成：" + allEntries.Count + " 个可生成物品。");
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogError("加载物品列表失败：" + ex);
            }
        }

        private static Sprite FindIcon(GameObject prefab)
        {
            SpriteRenderer[] all = prefab.GetComponentsInChildren<SpriteRenderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].sprite != null)
                {
                    return all[i].sprite;
                }
            }
            return null;
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static void Stretch(RectTransform rt, float left, float right, float top, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }

        private static TextMeshProUGUI NewText(string name, Transform parent, string text, float size, TextAlignmentOptions align)
        {
            RectTransform rt = NewRect(name, parent);
            TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                t.font = font;
            }
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.enableWordWrapping = false;
            t.raycastTarget = false;
            t.color = Color.white;
            return t;
        }

        private static void Build()
        {
            try
            {
                if (font == null)
                {
                    font = TMP_Settings.defaultFontAsset;
                }
                if (EventSystem.current == null)
                {
                    GameObject es = new GameObject("ItemPickerEventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
                    UnityEngine.Object.DontDestroyOnLoad(es);
                }

                root = new GameObject("ItemPickerCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                UnityEngine.Object.DontDestroyOnLoad(root);
                Canvas canvas = root.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 12000;
                CanvasScaler scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;

                RectTransform backdrop = NewRect("Backdrop", root.transform);
                Stretch(backdrop, 0f, 0f, 0f, 0f);
                Image backdropImage = backdrop.gameObject.AddComponent<Image>();
                backdropImage.color = new Color(0f, 0f, 0f, 0.55f);
                Button backdropButton = backdrop.gameObject.AddComponent<Button>();
                backdropButton.transition = Selectable.Transition.None;
                backdropButton.onClick.AddListener(new UnityEngine.Events.UnityAction(Close));

                panel = NewRect("Panel", root.transform);
                panel.anchorMin = new Vector2(0.5f, 0.5f);
                panel.anchorMax = new Vector2(0.5f, 0.5f);
                panel.pivot = new Vector2(0.5f, 0.5f);
                panel.sizeDelta = new Vector2(1280f, 800f);
                panel.anchoredPosition = Vector2.zero;
                Image panelImage = panel.gameObject.AddComponent<Image>();
                panelImage.color = new Color(0.07f, 0.08f, 0.11f, 0.98f);

                TextMeshProUGUI title = NewText("Title", panel, "物品生成器", 34f, TextAlignmentOptions.Left);
                RectTransform titleRect = (RectTransform)title.transform;
                titleRect.anchorMin = new Vector2(0f, 1f);
                titleRect.anchorMax = new Vector2(0f, 1f);
                titleRect.pivot = new Vector2(0f, 1f);
                titleRect.anchoredPosition = new Vector2(24f, -18f);
                titleRect.sizeDelta = new Vector2(420f, 46f);
                title.color = new Color(1f, 0.95f, 0.8f);

                TextMeshProUGUI hint = NewText("Hint", panel,
                    "左键 1 个 · Shift+左键 10 个 · Ctrl+左键 100 个 · 打字筛选 · Esc 关闭", 18f, TextAlignmentOptions.Right);
                RectTransform hintRect = (RectTransform)hint.transform;
                hintRect.anchorMin = new Vector2(1f, 1f);
                hintRect.anchorMax = new Vector2(1f, 1f);
                hintRect.pivot = new Vector2(1f, 1f);
                hintRect.anchoredPosition = new Vector2(-24f, -26f);
                hintRect.sizeDelta = new Vector2(760f, 32f);
                hint.color = new Color(0.75f, 0.78f, 0.85f);

                filterText = NewText("Filter", panel, "", 20f, TextAlignmentOptions.Left);
                RectTransform filterRect = (RectTransform)filterText.transform;
                filterRect.anchorMin = new Vector2(0f, 1f);
                filterRect.anchorMax = new Vector2(0f, 1f);
                filterRect.pivot = new Vector2(0f, 1f);
                filterRect.anchoredPosition = new Vector2(24f, -66f);
                filterRect.sizeDelta = new Vector2(700f, 28f);
                filterText.color = new Color(0.7f, 0.85f, 1f);

                statusText = NewText("Status", panel, "", 18f, TextAlignmentOptions.Right);
                RectTransform statusRect = (RectTransform)statusText.transform;
                statusRect.anchorMin = new Vector2(1f, 1f);
                statusRect.anchorMax = new Vector2(1f, 1f);
                statusRect.pivot = new Vector2(1f, 1f);
                statusRect.anchoredPosition = new Vector2(-24f, -66f);
                statusRect.sizeDelta = new Vector2(700f, 28f);
                statusText.color = new Color(0.6f, 0.85f, 0.6f);

                categoryBar = NewRect("Categories", panel);
                categoryBar.anchorMin = new Vector2(0f, 1f);
                categoryBar.anchorMax = new Vector2(1f, 1f);
                categoryBar.pivot = new Vector2(0.5f, 1f);
                categoryBar.anchoredPosition = new Vector2(0f, -100f);
                categoryBar.sizeDelta = new Vector2(-48f, 44f);
                HorizontalLayoutGroup catLayout = categoryBar.gameObject.AddComponent<HorizontalLayoutGroup>();
                catLayout.spacing = 8f;
                catLayout.childAlignment = TextAnchor.MiddleLeft;
                catLayout.childControlWidth = true;
                catLayout.childControlHeight = true;
                catLayout.childForceExpandWidth = false;
                catLayout.childForceExpandHeight = false;

                RectTransform scrollRectT = NewRect("Scroll", panel);
                scrollRectT.anchorMin = new Vector2(0f, 0f);
                scrollRectT.anchorMax = new Vector2(1f, 1f);
                scrollRectT.pivot = new Vector2(0.5f, 0.5f);
                scrollRectT.offsetMin = new Vector2(24f, 24f);
                scrollRectT.offsetMax = new Vector2(-24f, -156f);
                Image scrollBg = scrollRectT.gameObject.AddComponent<Image>();
                scrollBg.color = new Color(0f, 0f, 0f, 0.35f);
                scrollRectT.gameObject.AddComponent<RectMask2D>();
                ScrollRect scroll = scrollRectT.gameObject.AddComponent<ScrollRect>();
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.scrollSensitivity = 40f;
                scroll.viewport = scrollRectT;

                RectTransform contentRect = NewRect("Content", scrollRectT);
                contentRect.anchorMin = new Vector2(0f, 1f);
                contentRect.anchorMax = new Vector2(1f, 1f);
                contentRect.pivot = new Vector2(0.5f, 1f);
                contentRect.anchoredPosition = Vector2.zero;
                contentRect.sizeDelta = new Vector2(0f, 0f);
                GridLayoutGroup grid = contentRect.gameObject.AddComponent<GridLayoutGroup>();
                grid.cellSize = new Vector2(124f, 128f);
                grid.spacing = new Vector2(8f, 8f);
                grid.padding = new RectOffset(10, 10, 10, 10);
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = 9;
                ContentSizeFitter fitter = contentRect.gameObject.AddComponent<ContentSizeFitter>();
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                content = contentRect;
                scroll.content = contentRect;

                // 自绘悬浮提示（游戏自带 tooltip 的 canvas 层级比本菜单低，会被挡住）
                tooltipRect = NewRect("Tooltip", root.transform);
                tooltipRect.anchorMin = Vector2.zero;
                tooltipRect.anchorMax = Vector2.zero;
                tooltipRect.pivot = Vector2.zero;
                tooltipRect.sizeDelta = new Vector2(440f, 300f);
                Image tipBg = tooltipRect.gameObject.AddComponent<Image>();
                tipBg.color = new Color(0.10f, 0.11f, 0.16f, 1f);
                Outline tipOutline = tooltipRect.gameObject.AddComponent<Outline>();
                tipOutline.effectColor = new Color(0.45f, 0.48f, 0.58f, 1f);
                tipOutline.effectDistance = new Vector2(2f, -2f);
                tipBg.raycastTarget = false;

                tooltipName = NewText("TipName", tooltipRect, "", 20f, TextAlignmentOptions.TopLeft);
                RectTransform tnRect = (RectTransform)tooltipName.transform;
                tnRect.anchorMin = new Vector2(0f, 1f);
                tnRect.anchorMax = new Vector2(1f, 1f);
                tnRect.pivot = new Vector2(0f, 1f);
                tnRect.anchoredPosition = new Vector2(16f, -8f);
                tnRect.sizeDelta = new Vector2(-32f, 30f);
                tooltipName.color = new Color(1f, 0.9f, 0.68f);
                tooltipName.enableWordWrapping = false;

                tooltipDesc = NewText("TipDesc", tooltipRect, "", 15f, TextAlignmentOptions.TopLeft);
                RectTransform tdRect = (RectTransform)tooltipDesc.transform;
                tdRect.anchorMin = new Vector2(0f, 0f);
                tdRect.anchorMax = new Vector2(1f, 1f);
                tdRect.pivot = new Vector2(0f, 1f);
                tdRect.offsetMin = new Vector2(16f, 128f);
                tdRect.offsetMax = new Vector2(-16f, -42f);
                tooltipDesc.color = new Color(0.85f, 0.87f, 0.93f);
                tooltipDesc.enableWordWrapping = true;
                tooltipDesc.overflowMode = TextOverflowModes.Ellipsis;

                RectTransform tiRect = NewRect("TipIcon", tooltipRect);
                tiRect.anchorMin = new Vector2(0.5f, 0f);
                tiRect.anchorMax = new Vector2(0.5f, 0f);
                tiRect.pivot = new Vector2(0.5f, 0f);
                tiRect.anchoredPosition = new Vector2(0f, 16f);
                tiRect.sizeDelta = new Vector2(96f, 96f);
                tooltipIcon = tiRect.gameObject.AddComponent<Image>();
                tooltipIcon.preserveAspect = true;
                tooltipIcon.raycastTarget = false;
                tooltipRect.gameObject.SetActive(false);

                if (categories.Count > 0)
                {
                    BuildCategoryButtons();
                }
                else
                {
                    categoryBar.gameObject.SetActive(false);
                    scrollRectT.offsetMax = new Vector2(-24f, -108f);
                }
                RefreshFilterLabel();
                ItemPickerPlugin.Log.LogInfo("物品生成器界面已创建。");
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogError("创建界面失败：" + ex);
                root = null;
            }
        }

        private static void BuildCategoryButtons()
        {
            AddCategoryButton("", "全部");
            for (int i = 0; i < categories.Count; i++)
            {
                string cat = categories[i];
                string label = Locale.GetOther("category" + cat);
                if (string.IsNullOrEmpty(label) || label == "category" + cat)
                {
                    label = CategoryFallback(cat);
                }
                AddCategoryButton(cat, label);
            }
        }

        private static string CategoryFallback(string cat)
        {
            switch (cat.ToLowerInvariant())
            {
                case "custom": return "自定义";
                case "trash": return "垃圾";
                case "unobtainable": return "不可获得";
                case "container": return "容器";
                case "drug": return "药物";
                case "food": return "食物";
                case "medical": return "医疗物品";
                case "tool": return "工具";
                case "utility": return "实用物品";
                case "water": return "饮料";
                default: return cat;
            }
        }

        private static void AddCategoryButton(string cat, string label)
        {
            GameObject go = new GameObject("Cat_" + (cat.Length == 0 ? "all" : cat),
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(categoryBar, false);
            Image img = go.GetComponent<Image>();
            img.color = new Color(0.16f, 0.17f, 0.22f, 1f);
            LayoutElement le = go.GetComponent<LayoutElement>();
            le.preferredWidth = Mathf.Max(96f, label.Length * 20f + 24f);
            le.preferredHeight = 40f;
            Button btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            string capturedCat = cat;
            btn.onClick.AddListener(delegate
            {
                currentCategory = capturedCat;
                RebuildGrid();
            });
            TextMeshProUGUI t = NewText("Label", go.transform, label, 18f, TextAlignmentOptions.Center);
            Stretch((RectTransform)t.transform, 0f, 0f, 0f, 0f);
        }

        private static void RefreshFilterLabel()
        {
            if (filterText == null)
            {
                return;
            }
            filterText.text = string.IsNullOrEmpty(filter)
                ? "筛选：直接打字即可（Esc 清空 / 关闭）"
                : ("筛选：" + filter);
        }

        private static bool Matches(ItemEntry e)
        {
            if (!string.IsNullOrEmpty(currentCategory) &&
                !string.Equals(e.Category, currentCategory, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (string.IsNullOrEmpty(filter))
            {
                return true;
            }
            string f = filter.ToLowerInvariant();
            if (e.Name != null && e.Name.ToLowerInvariant().Contains(f))
            {
                return true;
            }
            return e.Id != null && e.Id.ToLowerInvariant().Contains(f);
        }

        private static void RebuildGrid()
        {
            if (content == null)
            {
                return;
            }
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(content.GetChild(i).gameObject);
            }

            int shown = 0;
            for (int i = 0; i < allEntries.Count; i++)
            {
                if (!Matches(allEntries[i]))
                {
                    continue;
                }
                try
                {
                    CreateCell(allEntries[i]);
                }
                catch (Exception cex)
                {
                    ItemPickerPlugin.Log.LogError("创建格子失败 " + allEntries[i].Id + "：" + cex.Message);
                }
                shown++;
            }
            if (statusText != null)
            {
                statusText.text = "共 " + shown + " / " + allEntries.Count + " 项";
            }
            RefreshFilterLabel();
        }

        private static void CreateCell(ItemEntry e)
        {
            GameObject cell = new GameObject("Cell_" + e.Id,
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(CellClick));
            cell.transform.SetParent(content, false);
            Image bg = cell.GetComponent<Image>();
            bg.color = new Color(0.14f, 0.15f, 0.19f, 1f);
            Button btn = cell.GetComponent<Button>();
            btn.targetGraphic = bg;
            CellClick click = cell.GetComponent<CellClick>();
            click.Entry = e;

            RectTransform iconRect = NewRect("Icon", cell.transform);
            iconRect.anchorMin = new Vector2(0.5f, 1f);
            iconRect.anchorMax = new Vector2(0.5f, 1f);
            iconRect.pivot = new Vector2(0.5f, 1f);
            iconRect.anchoredPosition = new Vector2(0f, -6f);
            iconRect.sizeDelta = new Vector2(96f, 72f);
            Image icon = iconRect.gameObject.AddComponent<Image>();
            icon.sprite = e.Icon;
            icon.preserveAspect = true;
            icon.raycastTarget = false;
            if (e.Icon == null)
            {
                icon.color = new Color(0.4f, 0.4f, 0.45f, 0.6f);
            }

            RectTransform labelRect = NewRect("Name", cell.transform);
            labelRect.anchorMin = new Vector2(0f, 0f);
            labelRect.anchorMax = new Vector2(1f, 0f);
            labelRect.pivot = new Vector2(0.5f, 0f);
            labelRect.anchoredPosition = new Vector2(0f, 4f);
            labelRect.sizeDelta = new Vector2(-6f, 44f);
            TextMeshProUGUI label = labelRect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null)
            {
                label.font = font;
            }
            label.text = ShortName(e.Name);
            label.fontSize = 14f;
            label.alignment = TextAlignmentOptions.Top;
            label.enableWordWrapping = true;
            label.raycastTarget = false;
            label.color = new Color(0.88f, 0.9f, 0.95f);

            CellHover hover = cell.AddComponent<CellHover>();
            hover.Entry = e;
        }

        private static string ShortName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return "";
            }
            string clean = name;
            int paren = clean.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0)
            {
                clean = clean.Substring(0, paren);
            }
            if (clean.Length > 12)
            {
                clean = clean.Substring(0, 12);
            }
            return clean;
        }

        internal static void Spawn(ItemEntry e, int amount)
        {
            try
            {
                if (PlayerCamera.main == null || PlayerCamera.main.body == null)
                {
                    return;
                }
                Body body = PlayerCamera.main.body;
                Vector2 basePos = body.transform.position;
                int made = 0;
                for (int i = 0; i < amount; i++)
                {
                    Vector2 p = basePos + UnityEngine.Random.insideUnitCircle * 0.35f;
                    GameObject go = Utils.Create(e.Id, p, 0f);
                    if (go == null)
                    {
                        break;
                    }
                    Item item = go.GetComponent<Item>();
                    if (item != null)
                    {
                        item.SetCondition(1f);
                        if (Vector2.Distance(go.transform.position, body.transform.position) < 2f)
                        {
                            body.AutoPickUpItem(item);
                        }
                    }
                    made++;
                }
                if (statusText != null)
                {
                    statusText.text = "已生成：" + e.Name + " ×" + made;
                }
                ItemPickerPlugin.Log.LogInfo("生成 " + e.Id + " ×" + made);
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogError("生成物品失败：" + ex);
            }
        }
    }
}
