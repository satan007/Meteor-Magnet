using HarmonyLib;
using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MeteorMagnet
{
    /// <summary>
    /// Resource filter, similar to choosing the ore of an extractor: aim at a Mk2/Mk3 magnet (what goes
    /// to storage) or a scanner (which meteors it tracks), press the filter key and tick the resources.
    /// The choice is kept in the building's linked groups, so it is saved with the world.
    /// </summary>
    internal class FilterWindow : MonoBehaviour
    {
        static readonly AccessTools.FieldRef<WindowsHandler, DataConfig.UiType> fWindowsHandlerOpenedUi =
            AccessTools.FieldRefAccess<WindowsHandler, DataConfig.UiType>("_openedUi");

        InputAction openAction;
        string boundKey;
        InputAction moduleAction;
        string boundModuleKey;

        WorldObject magnet;
        bool isScanner;
        // Building whose module slots are open (0 = none): its summary is shown next to the window.
        static int modulesOf;
        readonly List<Group> candidates = [];
        readonly HashSet<string> selected = [];
        Vector2 scroll;

        void Update()
        {
            if (boundKey != Plugin.FilterKey)
            {
                openAction?.Disable();
                boundKey = Plugin.FilterKey;
                openAction = new InputAction("Meteor Magnet Filter", binding: boundKey);
                openAction.Enable();
            }

            if (boundModuleKey != Plugin.ModuleKey)
            {
                moduleAction?.Disable();
                boundModuleKey = Plugin.ModuleKey;
                moduleAction = new InputAction("Meteor Magnet Modules", binding: boundModuleKey);
                moduleAction.Enable();
            }

            var wh = Managers.GetManager<WindowsHandler>();
            if (wh == null)
            {
                magnet = null;
                modulesOf = 0;
                return;
            }
            if (modulesOf != 0 && !wh.GetHasUiOpen())
            {
                modulesOf = 0;
            }

            if (magnet != null)
            {
                // Closed from outside (another window, menu) or Escape.
                if (!wh.GetHasUiOpen())
                {
                    magnet = null;
                }
                else if (Keyboard.current != null && Keyboard.current[Key.Escape].wasPressedThisFrame)
                {
                    Close(wh);
                }
                return;
            }

            if (!Plugin.Enabled || wh.GetHasUiOpen())
            {
                return;
            }
            if (moduleAction.WasPressedThisFrame())
            {
                OpenModules(wh);
                return;
            }
            if (!openAction.WasPressedThisFrame())
            {
                return;
            }
            var target = FindAimedFilterable();
            if (target != null)
            {
                Open(target, wh);
            }
        }

        /// <summary>
        /// Opens the module slots of the aimed scanner or magnet in the game's own container window.
        /// </summary>
        static void OpenModules(WindowsHandler wh)
        {
            var wo = FindAimedBuilding();
            if (wo == null)
            {
                Plugin.Log("Module key: not aiming at a scanner or magnet.");
                return;
            }
            var gid = wo.GetGroup()?.GetId();
            var slots = Content.TierOf(gid)?.moduleSlots ?? Content.ScannerOf(gid)?.moduleSlots
                ?? (gid == Content.Beacon.id ? Content.Beacon.moduleSlots : 0);
            var inv = ModuleSlots.Get(wo, slots);
            if (inv == null)
            {
                Plugin.Log("Module key: slots of #" + wo.GetId() + " are not ready yet.");
                Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer()
                    ?.AddInformation(2f, Localization.GetLocalizedString("MeteorMagnet_ModulesNotReady"), DataConfig.UiInformationsType.OutInventory, null);
                return;
            }
            var backpack = Managers.GetManager<PlayersManager>()?.GetActivePlayerController()?.GetPlayerBackpack()?.GetInventory();
            if (backpack == null)
            {
                return;
            }
            var window = (UiWindowContainer)wh.OpenAndReturnUi(DataConfig.UiType.Container);
            if (window != null)
            {
                window.SetInventories(backpack, inv, false);
                window.SetSettingsData(null, 0, DataConfig.UiSettingType.Null);
                modulesOf = wo.GetId();
                Plugin.Log("Opened module slots of " + gid + " #" + wo.GetId() + " (inventory " + inv.GetId() + ")");
            }
        }

        static WorldObject FindAimedBuilding()
        {
            var wo = AimedWorldObject();
            var gid = wo?.GetGroup()?.GetId();
            return Content.TierOf(gid) != null || Content.ScannerOf(gid) != null || gid == Content.Beacon.id ? wo : null;
        }

        /// <summary>A Mk2/Mk3 magnet or any scanner under the crosshair.</summary>
        static WorldObject FindAimedFilterable()
        {
            var wo = AimedWorldObject();
            var gid = wo?.GetGroup()?.GetId();
            var tier = Content.TierOf(gid);
            return (tier != null && tier.hasStorage) || Content.ScannerOf(gid) != null ? wo : null;
        }

        /// <summary>The first world object under the crosshair within reach.</summary>
        static WorldObject AimedWorldObject()
        {
            var pc = Managers.GetManager<PlayersManager>()?.GetActivePlayerController();
            var ray = pc?.GetAimController()?.GetAimRay();
            if (ray == null)
            {
                return null;
            }
            var hits = Physics.RaycastAll(ray.Value, 8f, ~LayerMask.GetMask(GameConfig.commonIgnoredAndWater));
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var woa = hit.transform.GetComponentInParent<WorldObjectAssociated>();
                var wo = woa?.GetWorldObject();
                if (wo != null)
                {
                    return wo;
                }
            }
            return null;
        }

        void Open(WorldObject wo, WindowsHandler wh)
        {
            magnet = wo;
            isScanner = Content.ScannerOf(wo.GetGroup()?.GetId()) != null;
            scroll = Vector2.zero;

            selected.Clear();
            foreach (var g in wo.GetLinkedGroups() ?? [])
            {
                if (g != null)
                {
                    selected.Add(g.GetId());
                }
            }
            CollectCandidates();

            fWindowsHandlerOpenedUi(wh) = DataConfig.UiType.TextInput;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        void Close(WindowsHandler wh)
        {
            magnet = null;
            wh.CloseAllWindows();
        }

        /// <summary>
        /// The resources meteors can bring on the current planet, plus whatever is already selected.
        /// </summary>
        void CollectCandidates()
        {
            candidates.Clear();
            var ids = PlanetMeteors.ResourceIds();
            ids.UnionWith(selected);

            foreach (var id in ids)
            {
                var g = GroupsHandler.GetGroupViaId(id);
                if (g != null)
                {
                    candidates.Add(g);
                }
            }
            candidates.Sort((a, b) => string.Compare(Readable.GetGroupName(a), Readable.GetGroupName(b), System.StringComparison.CurrentCulture));
        }

        void Apply()
        {
            var list = new List<Group>();
            foreach (var g in candidates)
            {
                if (selected.Contains(g.GetId()))
                {
                    list.Add(g);
                }
            }
            magnet.SetLinkedGroups(list);
            Plugin.Log("Filter of " + magnet.GetGroup()?.GetId() + " #" + magnet.GetId() + ": "
                + (list.Count == 0 ? "none" : string.Join("/", list.ConvertAll(g => g.GetId()))));
        }

        void OnGUI()
        {
            if (modulesOf != 0)
            {
                DrawSummary();
            }
            if (magnet == null)
            {
                return;
            }

            var scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var w = 520f;
            var h = 640f;
            var rect = new Rect((Screen.width / scale - w) / 2f, (1080f - h) / 2f, w, h);

            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 16, rect.y + 12, w - 32, h - 24));

            var title = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold };
            var text = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
            var toggle = new GUIStyle(GUI.skin.toggle) { fontSize = 18 };
            var button = new GUIStyle(GUI.skin.button) { fontSize = 18 };

            GUILayout.Label(Localization.GetLocalizedString(isScanner ? "MeteorMagnet_ScannerFilterTitle" : "MeteorMagnet_FilterTitle"), title);
            GUILayout.Label(Localization.GetLocalizedString(isScanner ? "MeteorMagnet_ScannerFilterHint" : "MeteorMagnet_FilterHint"), text);
            GUILayout.Space(8);

            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(h - 200));
            if (candidates.Count == 0)
            {
                GUILayout.Label(Localization.GetLocalizedString("MeteorMagnet_FilterEmpty"), text);
            }
            foreach (var g in candidates)
            {
                GUILayout.BeginHorizontal();
                DrawIcon(g);
                var id = g.GetId();
                var was = selected.Contains(id);
                var now = GUILayout.Toggle(was, " " + Readable.GetGroupName(g), toggle, GUILayout.Height(36));
                GUILayout.EndHorizontal();
                if (now != was)
                {
                    if (now)
                    {
                        selected.Add(id);
                    }
                    else
                    {
                        selected.Remove(id);
                    }
                    Apply();
                }
            }
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(Localization.GetLocalizedString(isScanner ? "MeteorMagnet_ScannerFilterAll" : "MeteorMagnet_FilterAll"), button, GUILayout.Height(40)))
            {
                selected.Clear();
                Apply();
            }
            if (GUILayout.Button(Localization.GetLocalizedString("MeteorMagnet_FilterClose"), button, GUILayout.Height(40)))
            {
                var wh = Managers.GetManager<WindowsHandler>();
                if (wh != null)
                {
                    Close(wh);
                }
                else
                {
                    magnet = null;
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        /// <summary>
        /// Totals of the building's modules, to the right of the module window; refreshed with the network (about every second).
        /// </summary>
        static void DrawSummary()
        {
            if (!MagnetController.Summaries.TryGetValue(modulesOf, out var summary))
            {
                return;
            }
            var scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            var text = new GUIStyle(GUI.skin.label) { fontSize = 17, wordWrap = true };
            var title = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, wordWrap = true };
            var cheer = new GUIStyle(text) { fontStyle = FontStyle.Bold, fontSize = 19 };
            cheer.normal.textColor = new Color(1f, 0.82f, 0.25f);

            const float w = 400f;
            var x = Screen.width / scale - w - 24f;
            var height = 56f;
            foreach (var line in summary.lines)
            {
                height += text.CalcHeight(new GUIContent(line), w - 28f) + 2f;
            }
            foreach (var line in summary.cheers)
            {
                height += cheer.CalcHeight(new GUIContent(line), w - 28f) + 4f;
            }
            var rect = new Rect(x, 140f, w, height);
            GUI.Box(rect, GUIContent.none);
            GUI.Box(rect, GUIContent.none);
            GUILayout.BeginArea(new Rect(rect.x + 14, rect.y + 10, w - 28, height - 16));
            GUILayout.Label(summary.title, title);
            foreach (var line in summary.cheers)
            {
                GUILayout.Label("» " + line, cheer);
            }
            foreach (var line in summary.lines)
            {
                GUILayout.Label(line, text);
            }
            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }

        static void DrawIcon(Group g)
        {
            var r = GUILayoutUtility.GetRect(36, 36, GUILayout.Width(36), GUILayout.Height(36));
            var sprite = g.GetImage();
            if (sprite == null || sprite.texture == null)
            {
                return;
            }
            var tr = sprite.textureRect;
            var tex = sprite.texture;
            GUI.DrawTextureWithTexCoords(r, tex,
                new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height));
        }
    }
}
