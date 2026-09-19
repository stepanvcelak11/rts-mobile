using System.Collections.Generic;
using RTS.Input;
using RTS.Net;
using RTS.Sim.Core;
using RTS.Sim.Model;
using UnityEngine;
using UnityEngine.UIElements;

namespace RTS.UI
{
    /// <summary>
    /// Phase-2 HUD built in code with UI Toolkit: resource bar on top, a bottom sheet with the
    /// selection and its actions, and a toast for rejected commands. Phase 4 replaces the
    /// layout with UXML/USS and adds the minimap and queues; the bindings stay.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudController : MonoBehaviour
    {
        private IMatchSession _session;
        private PlayerController _player;
        private UIDocument _doc;

        private Label _food, _wood, _gold, _pop, _debug, _selectionLabel, _toast;
        private VisualElement _sheet, _actions;
        private float _toastUntil;

        private static readonly Color Panel = new Color(0.09f, 0.1f, 0.13f, 0.86f);

        public void Bind(IMatchSession session, PlayerController player)
        {
            _session = session;
            _player = player;
            _doc = GetComponent<UIDocument>();
            Build(_doc.rootVisualElement);
            _player.SelectionChanged += RefreshActions;
            _player.CommandRejected += r => Toast(RejectText(r));
            RefreshActions();
        }

        /// <summary>True when a screen point lands on a HUD element (so gestures ignore it).</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (_doc == null || _doc.rootVisualElement == null || _doc.rootVisualElement.panel == null) return false;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(_doc.rootVisualElement.panel, new Vector2(screen.x, Screen.height - screen.y));
            VisualElement hit = _doc.rootVisualElement.panel.Pick(panelPos);
            return hit != null && hit != _doc.rootVisualElement && hit.pickingMode != PickingMode.Ignore;
        }

        private void Update()
        {
            if (_session == null) return;
            World w = _session.World;
            PlayerState ps = w.Players[_session.LocalPlayer];
            _food.text = "Food " + Whole(ps.Stockpile[0]);
            _wood.text = "Wood " + Whole(ps.Stockpile[1]);
            _gold.text = "Gold " + Whole(ps.Stockpile[2]);
            _pop.text = ps.Population + "/" + ps.PopulationCap;
            _debug.text = "t " + w.Tick + "  #" + (w.LastHash & 0xFFFF).ToString("X4");

            if (_toast.style.display.value == DisplayStyle.Flex && Time.unscaledTime > _toastUntil)
                _toast.style.display = DisplayStyle.None;

            // Rejections raised inside the simulation (affordability at execution time).
            foreach (SimEvent ev in w.Events)
                if (ev.Kind == SimEventKind.CommandRejected && ev.A == _session.LocalPlayer)
                    Toast(RejectText((CommandRejectReason)ev.B));

            RefreshQueueLabel();
        }

        private static string Whole(Fix64 v) => v.FloorToInt().ToString();

        // ---- layout -------------------------------------------------------------------------

        private void Build(VisualElement root)
        {
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;

            // Top bar
            var top = Row(root);
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.backgroundColor = Panel;
            top.style.paddingLeft = top.style.paddingRight = 16;
            top.style.paddingTop = top.style.paddingBottom = 10;
            top.style.marginTop = SafeTop();
            _food = Chip(top, new Color(0.6f, 0.9f, 0.55f));
            _wood = Chip(top, new Color(0.85f, 0.7f, 0.45f));
            _gold = Chip(top, new Color(0.98f, 0.85f, 0.4f));
            _pop = Chip(top, Color.white);
            _debug = Chip(top, new Color(0.6f, 0.65f, 0.7f));
            _debug.style.fontSize = 12;

            // Spacer that lets touches through to the world
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.style.flexGrow = 1;
            root.Add(spacer);

            // Toast
            _toast = new Label { pickingMode = PickingMode.Ignore };
            _toast.style.alignSelf = Align.Center;
            _toast.style.backgroundColor = new Color(0.72f, 0.2f, 0.18f, 0.95f);
            _toast.style.color = Color.white;
            _toast.style.fontSize = 16;
            _toast.style.paddingLeft = _toast.style.paddingRight = 18;
            _toast.style.paddingTop = _toast.style.paddingBottom = 10;
            _toast.style.borderTopLeftRadius = _toast.style.borderTopRightRadius = _toast.style.borderBottomLeftRadius = _toast.style.borderBottomRightRadius = 10;
            _toast.style.marginBottom = 12;
            _toast.style.display = DisplayStyle.None;
            root.Add(_toast);

            // Bottom sheet
            _sheet = new VisualElement();
            _sheet.style.backgroundColor = Panel;
            _sheet.style.paddingLeft = _sheet.style.paddingRight = 16;
            _sheet.style.paddingTop = 10;
            _sheet.style.paddingBottom = 10 + SafeBottom();
            _sheet.style.borderTopLeftRadius = _sheet.style.borderTopRightRadius = 16;
            root.Add(_sheet);

            _selectionLabel = new Label("Tap a villager to begin") { pickingMode = PickingMode.Ignore };
            _selectionLabel.style.color = Color.white;
            _selectionLabel.style.fontSize = 15;
            _selectionLabel.style.marginBottom = 8;
            _sheet.Add(_selectionLabel);

            _actions = Row(_sheet);
            _actions.style.flexWrap = Wrap.Wrap;
        }

        private static VisualElement Row(VisualElement parent)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            parent.Add(row);
            return row;
        }

        private static Label Chip(VisualElement parent, Color color)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.color = color;
            l.style.fontSize = 15;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginRight = 12;
            parent.Add(l);
            return l;
        }

        private Button ActionButton(string text, System.Action onClick, bool enabled = true)
        {
            var b = new Button(onClick) { text = text };
            b.style.minHeight = 48;
            b.style.minWidth = 96;
            b.style.fontSize = 14;
            b.style.marginRight = 8;
            b.style.marginBottom = 8;
            b.style.paddingLeft = b.style.paddingRight = 12;
            b.style.borderTopLeftRadius = b.style.borderTopRightRadius = b.style.borderBottomLeftRadius = b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = enabled ? new Color(0.23f, 0.51f, 0.96f) : new Color(0.3f, 0.32f, 0.36f);
            b.style.color = Color.white;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            b.SetEnabled(enabled);
            _actions.Add(b);
            return b;
        }

        // ---- content --------------------------------------------------------------------------

        private void RefreshActions()
        {
            _actions.Clear();
            World w = _session.World;
            int me = _session.LocalPlayer;

            if (_player.InBuildMode)
            {
                _selectionLabel.text = "Place " + w.Defs.Buildings[_player.BuildIndex].Def.name + " — tap the ground";
                ActionButton("Cancel", () => { _player.CancelBuild(); RefreshActions(); });
                return;
            }

            IReadOnlyList<int> sel = _player.Selection;
            if (sel.Count == 0)
            {
                _selectionLabel.text = "Tap a villager, then a tree or berries";
                return;
            }

            int building = _player.SelectedBuilding();
            if (building != 0)
            {
                BakedBuilding b = w.Defs.Buildings[w.Identities.Get(building).DefIndex];
                _selectionLabel.text = b.Def.name + (w.Constructions.Has(building) ? " (under construction)" : "");
                if (w.Queues.Has(building))
                {
                    foreach (int ui in b.Trains)
                    {
                        BakedUnit u = w.Defs.Units[ui];
                        int unit = ui;
                        ActionButton(u.Def.name + "  " + CostText(w, u.Cost),
                                     () => _player.Submit(new Sim.Commands.TrainCommand(me, building, unit)));
                    }
                }
                return;
            }

            int[] units = _player.SelectedUnits();
            _selectionLabel.text = units.Length == 1
                ? w.Defs.Units[w.Identities.Get(units[0]).DefIndex].Def.name
                : units.Length + " units";

            int[] builders = _player.SelectedBuilders();
            if (builders.Length > 0)
            {
                for (int i = 0; i < w.Defs.Buildings.Length; i++)
                {
                    BakedBuilding b = w.Defs.Buildings[i];
                    if (b.Def.age != "age.1" && b.Def.id != "bld.barracks") continue;   // Phase 2: age-I set + barracks
                    int index = i;
                    bool affordable = w.Players[me].CanAfford(b.Cost);
                    ActionButton(b.Def.name + "  " + CostText(w, b.Cost), () => { _player.BeginBuild(index); RefreshActions(); }, affordable);
                }
            }
            ActionButton("Stop", () => _player.Submit(new Sim.Commands.StopCommand(me, units)));
        }

        private void RefreshQueueLabel()
        {
            int building = _player.SelectedBuilding();
            if (building == 0 || !_session.World.Queues.Has(building)) return;
            ProductionQueue q = _session.World.Queues.Get(building);
            BakedBuilding b = _session.World.Defs.Buildings[_session.World.Identities.Get(building).DefIndex];
            _selectionLabel.text = q.Count == 0
                ? b.Def.name
                : b.Def.name + "  ·  queue " + q.Count + "  ·  " + Mathf.CeilToInt(q.HeadRemaining / (float)SimConstants.TickRate) + " s";
        }

        private static string CostText(World w, Fix64[] cost)
        {
            var parts = new List<string>();
            for (int i = 0; i < cost.Length; i++)
                if (!cost[i].IsZero) parts.Add(cost[i].FloorToInt() + " " + w.Defs.Data.Economy.resources[i]);
            return string.Join(", ", parts);
        }

        private void Toast(string text)
        {
            _toast.text = text;
            _toast.style.display = DisplayStyle.Flex;
            _toastUntil = Time.unscaledTime + 2f;
        }

        private static string RejectText(CommandRejectReason r)
        {
            switch (r)
            {
                case CommandRejectReason.NotAffordable: return "Not enough resources";
                case CommandRejectReason.BadPlacement: return "Can't build there";
                case CommandRejectReason.LimitReached: return "Limit reached";
                case CommandRejectReason.QueueFull: return "Queue is full";
                case CommandRejectReason.PopulationCap: return "Build more houses";
                default: return "Can't do that";
            }
        }

        private static float SafeTop() => Mathf.Max(0f, Screen.height - Screen.safeArea.yMax) / Mathf.Max(1f, Screen.dpi / 160f);
        private static float SafeBottom() => Mathf.Max(0f, Screen.safeArea.yMin) / Mathf.Max(1f, Screen.dpi / 160f);
    }
}
