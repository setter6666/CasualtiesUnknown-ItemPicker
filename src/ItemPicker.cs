using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
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
    [BepInPlugin("qol.itempicker", "Item Picker", "1.2.0")]
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
        public bool IsMod;
        public string OwnerGuid;
        public string Pinyin;
        public string Initials;
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

    // 拼音索引：内嵌压缩字典（U+4E00~U+9FFF 常用字），用于拼音/首字母搜索
    internal static class PinyinIndex
    {
        private static Dictionary<char, string> map;

        public static bool EnsureLoaded()
        {
            if (map != null)
            {
                return map.Count > 0;
            }
            Dictionary<char, string> dict = new Dictionary<char, string>();
            try
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ItemPicker.pinyin.bin");
                if (stream != null)
                {
                    using (stream)
                    using (DeflateStream ds = new DeflateStream(stream, CompressionMode.Decompress))
                    using (StreamReader reader = new StreamReader(ds, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            if (line.Length < 2)
                            {
                                continue;
                            }
                            char ch = line[0];
                            string py = line.Substring(1).Trim();
                            if (py.Length > 0)
                            {
                                dict[ch] = py;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogWarning("拼音索引加载失败：" + ex.Message);
            }
            map = dict;
            if (dict.Count > 0)
            {
                ItemPickerPlugin.Log.LogInfo("拼音索引已加载：" + dict.Count + " 个汉字。");
            }
            return dict.Count > 0;
        }

        public static string GetPinyin(string text)
        {
            if (string.IsNullOrEmpty(text) || map == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(text.Length * 4);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string py;
                if (map.TryGetValue(c, out py))
                {
                    sb.Append(py);
                }
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(c);
                }
                else if (c >= 'A' && c <= 'Z')
                {
                    sb.Append((char)(c + 32));
                }
            }
            return sb.ToString();
        }

        public static string GetInitials(string text)
        {
            if (string.IsNullOrEmpty(text) || map == null)
            {
                return "";
            }
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                string py;
                if (map.TryGetValue(c, out py) && py.Length > 0)
                {
                    sb.Append(py[0]);
                }
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }
    }

    // 与 CUCoreLib 的桥接：反射读取注册进来的 mod 物品（这些物品不在 Resources 里，
    // 必须用 CUCoreLib.CustomInstantiate 生成）
    internal static class CuCoreBridge
    {
        private static bool initialized;
        private static bool available;
        private static Type itemRegistryType;
        private static Type customInstantiateType;
        private static MethodInfo getIdsMethod;
        private static MethodInfo tryGetIconMethod;
        private static MethodInfo tryGetInfoMethod;
        private static MethodInfo tryGetOwnerMethod;
        private static MethodInfo instantiateMethod;

        private static Type FindTypeByName(string simpleName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type[] types;
                try
                {
                    types = asms[i].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }
                if (types == null)
                {
                    continue;
                }
                for (int j = 0; j < types.Length; j++)
                {
                    Type t = types[j];
                    if (t != null && t.Name == simpleName && t.Namespace != null && t.Namespace.StartsWith("CUCoreLib", StringComparison.Ordinal))
                    {
                        return t;
                    }
                }
            }
            return null;
        }

        public static bool Available
        {
            get { Init(); return available; }
        }

        public static bool CanSpawn
        {
            get { Init(); return instantiateMethod != null; }
        }

        private static void Init()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
            try
            {
                itemRegistryType = FindTypeByName("ItemRegistry");
                customInstantiateType = FindTypeByName("CustomInstantiate");
                if (itemRegistryType != null)
                {
                    getIdsMethod = itemRegistryType.GetMethod("GetRegisteredItemIds", Type.EmptyTypes);
                    tryGetIconMethod = itemRegistryType.GetMethod("TryGetIcon", new Type[] { typeof(string), typeof(Sprite).MakeByRefType() });
                    tryGetInfoMethod = itemRegistryType.GetMethod("TryGetItemInfo", new Type[] { typeof(string), typeof(ItemInfo).MakeByRefType() });
                    tryGetOwnerMethod = itemRegistryType.GetMethod("TryGetOwnerModGuid", new Type[] { typeof(string), typeof(string).MakeByRefType() });
                }
                if (customInstantiateType != null)
                {
                    instantiateMethod = customInstantiateType.GetMethod("InstantiateReturn");
                }
                if (instantiateMethod == null)
                {
                    ItemPickerPlugin.Log.LogWarning("找到了 CUCoreLib，但没找到 CustomInstantiate.InstantiateReturn，mod 物品只能列出不能生成。");
                }
                available = itemRegistryType != null && getIdsMethod != null;
                if (available)
                {
                    ItemPickerPlugin.Log.LogInfo("检测到 CUCoreLib，已启用 mod 物品支持。");
                }
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogWarning("CUCoreLib 兼容初始化失败：" + ex.Message);
            }
        }

        public static List<string> GetItemIds()
        {
            Init();
            List<string> result = new List<string>();
            if (getIdsMethod == null)
            {
                return result;
            }
            try
            {
                IEnumerable<string> ids = getIdsMethod.Invoke(null, null) as IEnumerable<string>;
                if (ids != null)
                {
                    foreach (string id in ids)
                    {
                        if (!string.IsNullOrEmpty(id))
                        {
                            result.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogWarning("读取 CUCoreLib 物品列表失败：" + ex.Message);
            }
            return result;
        }

        public static Sprite GetIcon(string id)
        {
            Init();
            if (tryGetIconMethod == null)
            {
                return null;
            }
            try
            {
                object[] args = new object[] { id, null };
                object ok = tryGetIconMethod.Invoke(null, args);
                if (ok is bool && (bool)ok)
                {
                    return args[1] as Sprite;
                }
            }
            catch
            {
            }
            return null;
        }

        public static bool TryGetInfo(string id, out string name, out string desc, out string category)
        {
            name = null;
            desc = null;
            category = null;
            Init();
            if (tryGetInfoMethod == null)
            {
                return false;
            }
            try
            {
                object[] args = new object[] { id, null };
                object ok = tryGetInfoMethod.Invoke(null, args);
                if (ok is bool && (bool)ok)
                {
                    ItemInfo info = args[1] as ItemInfo;
                    if (info != null)
                    {
                        name = info.fullName;
                        desc = info.description;
                        category = info.category;
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public static string GetOwner(string id)
        {
            Init();
            if (tryGetOwnerMethod == null)
            {
                return "";
            }
            try
            {
                object[] args = new object[] { id, null };
                object ok = tryGetOwnerMethod.Invoke(null, args);
                if (ok is bool && (bool)ok)
                {
                    return args[1] as string;
                }
            }
            catch
            {
            }
            return "";
        }

        public static GameObject Spawn(string id, Vector3 position)
        {
            Init();
            if (instantiateMethod == null)
            {
                return null;
            }
            try
            {
                object[] args = new object[] { id, position, Quaternion.identity, 1f };
                return instantiateMethod.Invoke(null, args) as GameObject;
            }
            catch (Exception ex)
            {
                ItemPickerPlugin.Log.LogWarning("生成 mod 物品失败（" + id + "）：" + ex.Message);
                return null;
            }
        }

        public static string OwnerDisplayName(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return "";
            }
            try
            {
                PluginInfo info;
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out info) && info != null &&
                    info.Metadata != null && !string.IsNullOrEmpty(info.Metadata.Name))
                {
                    return info.Metadata.Name;
                }
            }
            catch
            {
            }
            return guid;
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
        private static RectTransform modBar;
        private static RectTransform scrollArea;

        private static readonly List<ItemEntry> allEntries = new List<ItemEntry>();
        private static readonly List<string> categories = new List<string>();
        private static readonly List<string> modOwners = new List<string>();
        private static int modItemCount;
        private static bool itemsLoaded;
        private static string currentCategory = "";
        private static string currentOwner = "";
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
            string body = string.IsNullOrEmpty(e.Desc) ? "" : e.Desc;
            if (e.IsMod)
            {
                string owner = CuCoreBridge.OwnerDisplayName(e.OwnerGuid);
                if (!string.IsNullOrEmpty(owner))
                {
                    body = "<color=#8fd3ff>来源：" + owner + "</color>\n" + body;
                }
            }
            tooltipDesc.text = body;
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

                // ---- CUCoreLib 注册的 mod 物品（不在 Resources 里，必须用 CUCoreLib 实例化）----
                if (CuCoreBridge.Available)
                {
                    List<string> modIds = CuCoreBridge.GetItemIds();
                    for (int i = 0; i < modIds.Count; i++)
                    {
                        string id = modIds[i];
                        if (seen.Contains(id))
                        {
                            continue;
                        }
                        ItemEntry entry = new ItemEntry();
                        entry.Id = id;
                        entry.IsMod = true;
                        string modName;
                        string modDesc;
                        string modCategory;
                        if (CuCoreBridge.TryGetInfo(id, out modName, out modDesc, out modCategory))
                        {
                            entry.Name = string.IsNullOrEmpty(modName) ? Locale.GetItem(id) : modName;
                            entry.Desc = modDesc;
                            entry.Category = modCategory;
                        }
                        else
                        {
                            entry.Name = Locale.GetItem(id);
                            entry.Desc = Locale.GetItem(id + "dsc");
                            entry.Category = "";
                        }
                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            entry.Name = id;
                        }
                        entry.Icon = CuCoreBridge.GetIcon(id);
                        entry.OwnerGuid = CuCoreBridge.GetOwner(id);
                        if (!string.IsNullOrEmpty(entry.OwnerGuid) && !modOwners.Contains(entry.OwnerGuid))
                        {
                            modOwners.Add(entry.OwnerGuid);
                        }
                        seen.Add(id);
                        allEntries.Add(entry);
                        modItemCount++;
                    }
                    ItemPickerPlugin.Log.LogInfo("CUCoreLib mod 物品：" + modItemCount + " 个，来自 " + modOwners.Count + " 个 mod。");
                }

                allEntries.Sort(delegate (ItemEntry a, ItemEntry b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
                });

                if (PinyinIndex.EnsureLoaded())
                {
                    for (int i = 0; i < allEntries.Count; i++)
                    {
                        allEntries[i].Pinyin = PinyinIndex.GetPinyin(allEntries[i].Name);
                        allEntries[i].Initials = PinyinIndex.GetInitials(allEntries[i].Name);
                    }

                }

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
                modOwners.Sort(delegate (string a, string b)
                {
                    return string.Compare(CuCoreBridge.OwnerDisplayName(a), CuCoreBridge.OwnerDisplayName(b), StringComparison.CurrentCulture);
                });

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
                    "左键 1 个 · Shift+左键 10 个 · Ctrl+左键 100 个 · 打字筛选（中文 / 拼音 / ID）· Esc 关闭", 18f, TextAlignmentOptions.Right);
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

                modBar = NewRect("ModSources", panel);
                modBar.anchorMin = new Vector2(0f, 1f);
                modBar.anchorMax = new Vector2(1f, 1f);
                modBar.pivot = new Vector2(0.5f, 1f);
                modBar.anchoredPosition = new Vector2(0f, -148f);
                modBar.sizeDelta = new Vector2(-48f, 40f);
                HorizontalLayoutGroup modLayout = modBar.gameObject.AddComponent<HorizontalLayoutGroup>();
                modLayout.spacing = 8f;
                modLayout.childAlignment = TextAnchor.MiddleLeft;
                modLayout.childControlWidth = true;
                modLayout.childControlHeight = true;
                modLayout.childForceExpandWidth = false;
                modLayout.childForceExpandHeight = false;

                RectTransform scrollRectT = NewRect("Scroll", panel);
                scrollRectT.anchorMin = new Vector2(0f, 0f);
                scrollRectT.anchorMax = new Vector2(1f, 1f);
                scrollRectT.pivot = new Vector2(0.5f, 0.5f);
                scrollRectT.offsetMin = new Vector2(24f, 24f);
                scrollRectT.offsetMax = new Vector2(-24f, -156f);
                scrollArea = scrollRectT;
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

                if (categories.Count > 0 || modItemCount > 0)
                {
                    BuildCategoryButtons();
                }
                else
                {
                    categoryBar.gameObject.SetActive(false);
                }

                if (modItemCount > 0)
                {
                    BuildModButtons();
                }
                else
                {
                    modBar.gameObject.SetActive(false);
                }

                ApplyFilterLayout();

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
            if (modItemCount > 0)
            {
                AddCategoryButton("__mod__", "mod物品");
            }
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
                if (capturedCat != "__mod__")
                {
                    currentOwner = "";
                }
                ApplyFilterLayout();
                RebuildGrid();
            });
            TextMeshProUGUI t = NewText("Label", go.transform, label, 18f, TextAlignmentOptions.Center);
            Stretch((RectTransform)t.transform, 0f, 0f, 0f, 0f);
        }

        private static void BuildModButtons()
        {
            AddModButton("", "全部来源");
            for (int i = 0; i < modOwners.Count; i++)
            {
                AddModButton(modOwners[i], CuCoreBridge.OwnerDisplayName(modOwners[i]));
            }
        }

        private static void AddModButton(string owner, string label)
        {
            string shortLabel = label;
            if (!string.IsNullOrEmpty(shortLabel) && shortLabel.Length > 14)
            {
                shortLabel = shortLabel.Substring(0, 13) + "…";
            }
            GameObject go = new GameObject("Mod_" + (owner.Length == 0 ? "all" : owner),
                typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(modBar, false);
            Image img = go.GetComponent<Image>();
            img.color = new Color(0.13f, 0.16f, 0.21f, 1f);
            LayoutElement le = go.GetComponent<LayoutElement>();
            le.preferredWidth = Mathf.Max(92f, (string.IsNullOrEmpty(shortLabel) ? 4 : shortLabel.Length) * 15f + 26f);
            le.preferredHeight = 36f;
            Button btn = go.GetComponent<Button>();
            btn.targetGraphic = img;
            string captured = owner;
            btn.onClick.AddListener(delegate
            {
                currentOwner = captured;
                RebuildGrid();
            });
            TextMeshProUGUI t = NewText("Label", go.transform, shortLabel, 16f, TextAlignmentOptions.Center);
            Stretch((RectTransform)t.transform, 0f, 0f, 0f, 0f);
        }

        private static void ApplyFilterLayout()
        {
            bool showModBar = modBar != null && modItemCount > 0 && currentCategory == "__mod__";
            if (modBar != null && modBar.gameObject.activeSelf != showModBar)
            {
                modBar.gameObject.SetActive(showModBar);
            }
            float topInset = 108f;
            if (categoryBar != null && categoryBar.gameObject.activeSelf)
            {
                topInset += 48f;
            }
            if (showModBar)
            {
                topInset += 44f;
            }
            if (scrollArea != null)
            {
                scrollArea.offsetMax = new Vector2(-24f, -topInset);
            }
        }

        private static void RefreshFilterLabel()
        {
            if (filterText == null)
            {
                return;
            }
            filterText.text = string.IsNullOrEmpty(filter)
                ? "筛选：中文 / 拼音 / 英文 ID（Esc 清空 / 关闭）"
                : ("筛选：" + filter);
        }

        private static bool Matches(ItemEntry e)
        {
            if (!string.IsNullOrEmpty(currentOwner))
            {
                if (!e.IsMod || !string.Equals(e.OwnerGuid, currentOwner, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            if (currentCategory == "__mod__")
            {
                if (!e.IsMod)
                {
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(currentCategory) &&
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
            if (e.Id != null && e.Id.ToLowerInvariant().Contains(f))
            {
                return true;
            }
            string letters = "";
            for (int i = 0; i < f.Length; i++)
            {
                char c = f[i];
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    letters += c;
                }
            }
            if (letters.Length == 0)
            {
                return false;
            }
            if (e.Pinyin != null && e.Pinyin.IndexOf(letters, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
            return e.Initials != null && e.Initials.IndexOf(letters, StringComparison.Ordinal) >= 0;
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
                    GameObject go;
                    if (e.IsMod)
                    {
                        // mod 物品不在 Resources 里，只能通过 CUCoreLib 实例化
                        go = CuCoreBridge.Spawn(e.Id, new Vector3(p.x, p.y, 0f));
                        if (go == null)
                        {
                            // 兜底：个别 mod 物品可能同时提供 Resources 预制体
                            go = Utils.Create(e.Id, p, 0f);
                        }
                    }
                    else
                    {
                        go = Utils.Create(e.Id, p, 0f);
                    }
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
