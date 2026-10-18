using HarmonyLib;
using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MeteorMagnet
{
    /// <summary>
    /// Resource filter for Mk2/Mk3 magnets, similar to choosing the ore of an extractor:
    /// aim at the magnet, press the filter key and tick the resources that should go to storage.
    /// The choice is kept in the magnet's linked groups, so it is saved with the world.
    /// </summary>
    internal class FilterWindow : MonoBehaviour
    {
        static readonly AccessTools.FieldRef<WindowsHandler, DataConfig.UiType> fWindowsHandlerOpenedUi =
            AccessTools.FieldRefAccess<WindowsHandler, DataConfig.UiType>("_openedUi");

        static readonly AccessTools.FieldRef<MeteoHandler, List<MeteoEventData>> fMeteoHandlerMeteoEvents =
            AccessTools.FieldRefAccess<MeteoHandler, List<MeteoEventData>>("_meteoEvents");

        InputAction openAction;
        string boundKey;

        WorldObject magnet;
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

            var wh = Managers.GetManager<WindowsHandler>();
            if (wh == null)
            {
                magnet = null;
                return;
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

            if (!Plugin.Enabled || wh.GetHasUiOpen() || !openAction.WasPressedThisFrame())
            {
                return;
            }
            var target = FindAimedMagnet();
            if (target != null)
            {
                Open(target, wh);
            }
        }

        static WorldObject FindAimedMagnet()
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
                if (wo == null)
                {
                    continue;
                }
                var tier = Content.TierOf(wo.GetGroup()?.GetId());
                return tier != null && tier.hasStorage ? wo : null;
            }
            return null;
        }

        void Open(WorldObject wo, WindowsHandler wh)
        {
            magnet = wo;
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
            var ids = new HashSet<string>();

            var mh = Managers.GetManager<MeteoHandler>();
            var events = mh != null ? fMeteoHandlerMeteoEvents(mh) : null;
            foreach (var me in events ?? [])
            {
                var groups = me?.asteroidEventData?.asteroidGameObject?.GetComponent<Asteroid>()?.groupsSelected;
                foreach (var gd in groups ?? [])
                {
                    if (gd != null)
                    {
                        ids.Add(gd.id);
                    }
                }
            }
            foreach (var id in Content.BarrenLoot)
            {
                ids.Add(id);
            }
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
        }

        void OnGUI()
        {
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

            GUILayout.Label(Localization.GetLocalizedString("MeteorMagnet_FilterTitle"), title);
            GUILayout.Label(Localization.GetLocalizedString("MeteorMagnet_FilterHint"), text);
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
            if (GUILayout.Button(Localization.GetLocalizedString("MeteorMagnet_FilterAll"), button, GUILayout.Height(40)))
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
