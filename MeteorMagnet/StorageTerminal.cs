using HarmonyLib;
using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MeteorMagnet
{
    /// <summary>
    /// The storage terminal window: everything on the network's disks, one icon per kind with its count,
    /// and the backpack below it. Click takes or stores one item, Shift+click all of that kind.
    /// Items move by being removed on one side and created on the other, so items carrying their own
    /// inventory or text (disks, written items) are not stored.
    /// </summary>
    internal class StorageTerminal : MonoBehaviour
    {
        static readonly AccessTools.FieldRef<WindowsHandler, DataConfig.UiType> fWindowsHandlerOpenedUi =
            AccessTools.FieldRefAccess<WindowsHandler, DataConfig.UiType>("_openedUi");

        static WorldObject terminal;
        static string search = "";
        static Vector2 scrollNet, scrollPack;

        internal static void Open(WorldObject wo)
        {
            var wh = Managers.GetManager<WindowsHandler>();
            if (wh == null || wh.GetHasUiOpen())
            {
                return;
            }
            terminal = wo;
            search = "";
            fWindowsHandlerOpenedUi(wh) = DataConfig.UiType.TextInput;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Plugin.Log("Storage terminal #" + wo.GetId() + " opened");
        }

        static void Close()
        {
            terminal = null;
            Managers.GetManager<WindowsHandler>()?.CloseAllWindows();
        }

        void Update()
        {
            if (terminal == null)
            {
                return;
            }
            var wh = Managers.GetManager<WindowsHandler>();
            if (wh == null || !wh.GetHasUiOpen())
            {
                terminal = null;
            }
            else if (Keyboard.current != null && Keyboard.current[Key.Escape].wasPressedThisFrame)
            {
                Close();
            }
        }

        static Inventory Backpack() =>
            Managers.GetManager<PlayersManager>()?.GetActivePlayerController()?.GetPlayerBackpack()?.GetInventory();

        /// <summary>Items that cannot be stored: disks (their contents hang on the item) and items with own data.</summary>
        static bool Storable(WorldObject wo)
        {
            var def = Content.StorageItemOf(wo.GetGroup()?.GetId());
            return (def == null || !def.IsDisk) && wo.GetLinkedInventoryId() <= 0 && string.IsNullOrEmpty(wo.GetText());
        }

        static void Take(StorageNet net, string kind, int count)
        {
            var backpack = Backpack();
            if (backpack == null || !StorageNetwork.Contents(net).TryGetValue(kind, out var items))
            {
                return;
            }
            var free = backpack.GetSize() - backpack.GetInsideWorldObjects().Count;
            var moved = 0;
            foreach (var (disk, item) in items)
            {
                if (moved >= count || moved >= free)
                {
                    break;
                }
                var group = item.GetGroup();
                var source = disk.inventory;
                InventoriesHandler.Instance.RemoveItemFromInventory(item, source, true, null);
                InventoriesHandler.Instance.AddItemToInventory(group, backpack, (success, id) =>
                {
                    if (!success)
                    {
                        // The backpack filled up meanwhile: put it back on the disk.
                        DestroyFailed(id);
                        InventoriesHandler.Instance.AddItemToInventory(group, source, null);
                    }
                });
                moved++;
            }
            Plugin.Log("Terminal: took " + moved + " " + kind);
        }

        static void Store(StorageNet net, string kind, int count)
        {
            var backpack = Backpack();
            if (backpack == null)
            {
                return;
            }
            var planned = new Dictionary<NetworkDisk, (int count, HashSet<string> kinds)>();
            var moved = 0;
            var noRoom = false;
            foreach (var item in new List<WorldObject>(backpack.GetInsideWorldObjects()))
            {
                if (moved >= count)
                {
                    break;
                }
                var group = item?.GetGroup();
                if (group == null || (kind != null && group.GetId() != kind) || !Storable(item))
                {
                    continue;
                }
                var disk = StorageNetwork.DiskFor(net, group.GetId(), planned);
                if (disk == null)
                {
                    noRoom = true;
                    if (kind != null)
                    {
                        break;
                    }
                    continue;
                }
                var p = planned[disk];
                p.kinds.Add(group.GetId());
                planned[disk] = (p.count + 1, p.kinds);
                var target = disk.inventory;
                InventoriesHandler.Instance.RemoveItemFromInventory(item, backpack, true, null);
                InventoriesHandler.Instance.AddItemToInventory(group, target, (success, id) =>
                {
                    if (!success)
                    {
                        DestroyFailed(id);
                        InventoriesHandler.Instance.AddItemToInventory(group, backpack, null);
                    }
                });
                moved++;
            }
            Plugin.Log("Terminal: stored " + moved + " " + (kind ?? "items") + (noRoom ? " (no room for some: disk full or out of kinds)" : ""));
        }

        static void DestroyFailed(int id)
        {
            if (id != 0)
            {
                WorldObjectsHandler.Instance.DestroyWorldObject(id);
            }
        }

        static bool Ru => Localization.GetLocalizedString("MeteorMagnet_DisplayTitle") != "Meteor network";

        static string T(string ru, string en) => Ru ? ru : en;

        void OnGUI()
        {
            if (terminal == null)
            {
                return;
            }
            var scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            const float w = 980f, h = 820f;
            var rect = new Rect((Screen.width / scale - w) / 2f, (1080f - h) / 2f, w, h);
            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 16, rect.y + 12, w - 32, h - 24));

            var title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            var text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            var button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
            GUILayout.Label(Readable.GetGroupName(terminal.GetGroup()), title);

            var net = StorageNetwork.NetOf(terminal);
            string problem = null;
            if (net == null)
            {
                problem = T("Терминал не подключён: нужен контроллер хранилища с ядром в 40 м (или через реле) и свободный канал.",
                    "The terminal is not connected: it needs a storage controller with a core within 40 m (or via relays) and a free channel.");
            }
            else if (!MagnetController.NetworkPowered)
            {
                problem = T("Сети не хватает энергии.", "The network lacks power.");
            }
            if (problem != null)
            {
                GUILayout.Label(problem, text);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(T("Закрыть", "Close"), button, GUILayout.Height(38)))
                {
                    Close();
                }
                GUILayout.EndArea();
                GUI.matrix = Matrix4x4.identity;
                return;
            }

            var contents = StorageNetwork.Contents(net);
            var kinds = 0;
            foreach (var d in net.disks)
            {
                kinds += d.Kinds().Count;
            }
            GUILayout.Label(T("Предметов: ", "Items: ") + net.Stored + "/" + net.Capacity
                + T("   Видов: ", "   Kinds: ") + kinds + "/" + net.KindSlots
                + T("   Дисков: ", "   Disks: ") + net.disks.Count
                + T("   Каналы: ", "   Channels: ") + (net.drives.Count + net.terminals.Count) + "/" + net.channels
                + (net.unserved.Count > 0 ? T("   без канала: ", "   without a channel: ") + net.unserved.Count : ""), text);
            GUILayout.Label(T("Клик — 1 предмет, Shift+клик — все этого вида.", "Click: 1 item, Shift+click: all of that kind."), text);
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Поиск:", "Search:"), text, GUILayout.Width(70));
            search = GUILayout.TextField(search, new GUIStyle(GUI.skin.textField) { fontSize = 16 }, GUILayout.Height(28));
            GUILayout.EndHorizontal();

            var shift = Keyboard.current != null && (Keyboard.current[Key.LeftShift].isPressed || Keyboard.current[Key.RightShift].isPressed);

            var netKinds = new List<(Group group, int count)>();
            foreach (var kv in contents)
            {
                var g = kv.Value[0].item.GetGroup();
                if (Matches(g))
                {
                    netKinds.Add((g, kv.Value.Count));
                }
            }
            netKinds.Sort((a, b) => string.Compare(Readable.GetGroupName(a.group), Readable.GetGroupName(b.group), System.StringComparison.CurrentCulture));
            scrollNet = GUILayout.BeginScrollView(scrollNet, GUILayout.Height(390));
            var clicked = Grid(netKinds, w - 60);
            GUILayout.EndScrollView();
            if (clicked != null)
            {
                Take(net, clicked.GetId(), shift ? int.MaxValue : 1);
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("Рюкзак", "Backpack"), title);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(T("Положить всё", "Store everything"), button, GUILayout.Height(34), GUILayout.Width(220)))
            {
                Store(net, null, int.MaxValue);
            }
            GUILayout.EndHorizontal();
            var packKinds = new Dictionary<string, (Group group, int count)>();
            foreach (var wo in Backpack()?.GetInsideWorldObjects() ?? (IEnumerable<WorldObject>)[])
            {
                var g = wo?.GetGroup();
                if (g == null || !Storable(wo) || !Matches(g))
                {
                    continue;
                }
                packKinds[g.GetId()] = (g, packKinds.TryGetValue(g.GetId(), out var c) ? c.count + 1 : 1);
            }
            var packList = new List<(Group group, int count)>(packKinds.Values);
            packList.Sort((a, b) => string.Compare(Readable.GetGroupName(a.group), Readable.GetGroupName(b.group), System.StringComparison.CurrentCulture));
            scrollPack = GUILayout.BeginScrollView(scrollPack, GUILayout.Height(180));
            var stored = Grid(packList, w - 60);
            GUILayout.EndScrollView();
            if (stored != null)
            {
                Store(net, stored.GetId(), shift ? int.MaxValue : 1);
            }

            GUILayout.FlexibleSpace();
            if (GUILayout.Button(T("Закрыть", "Close"), button, GUILayout.Height(38)))
            {
                Close();
            }
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        static bool Matches(Group g)
        {
            return search.Length == 0 || Readable.GetGroupName(g).IndexOf(search, System.StringComparison.CurrentCultureIgnoreCase) >= 0;
        }

        /// <summary>Icons with counts in rows; returns the kind clicked this frame.</summary>
        static Group Grid(List<(Group group, int count)> kinds, float width)
        {
            const float cell = 78f;
            var perRow = Mathf.Max(1, (int)(width / cell));
            var count = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerRight };
            Group clicked = null;
            for (int i = 0; i < kinds.Count; i += perRow)
            {
                GUILayout.BeginHorizontal();
                for (int j = i; j < Mathf.Min(i + perRow, kinds.Count); j++)
                {
                    var (g, n) = kinds[j];
                    var r = GUILayoutUtility.GetRect(cell - 6, cell - 6, GUILayout.Width(cell - 6), GUILayout.Height(cell - 6));
                    if (GUI.Button(r, new GUIContent("", Readable.GetGroupName(g))))
                    {
                        clicked = g;
                    }
                    var sprite = g.GetImage();
                    if (sprite != null && sprite.texture != null)
                    {
                        var tr = sprite.textureRect;
                        var tex = sprite.texture;
                        GUI.DrawTextureWithTexCoords(new Rect(r.x + 6, r.y + 4, r.width - 12, r.height - 12), tex,
                            new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
                    }
                    GUI.Label(new Rect(r.x, r.y, r.width - 4, r.height - 2), "×" + n, count);
                }
                GUILayout.EndHorizontal();
            }
            if (kinds.Count == 0)
            {
                GUILayout.Label("—");
            }
            if (!string.IsNullOrEmpty(GUI.tooltip))
            {
                GUILayout.Label(GUI.tooltip);
            }
            return clicked;
        }
    }
}
