using System.Collections.Generic;
using RTS.Data;
using RTS.Input;
using RTS.Net;
using RTS.Sim.Commands;
using RTS.Sim.Core;
using RTS.Sim.Model;
using RTS.Sim.Systems;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace RTS.UI
{
    /// <summary>
    /// The in-game mobile HUD (docs/03-CONTROLS-CAMERA.md §4), built in code with UI Toolkit:
    /// resource bar with age and shipments, minimap, idle-villager button, bottom sheet with
    /// selection cards and context actions (build ring, training with queue, research, age-up),
    /// shipment drawer, radial menu on long press, selection box, toasts and the match-end panel.
    /// Every action goes through PlayerController → ICommandSource; the HUD never touches the world.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HudController : MonoBehaviour
    {
        private IMatchSession _session;
        private PlayerController _player;
        private UIDocument _doc;
        private VisualElement _root;

        // top bar
        private Label _food, _wood, _gold, _pop, _age, _debug;
        private Button _shipmentsButton;
        // minimap
        private MinimapView _minimap;
        // bottom
        private VisualElement _sheet, _cards, _actions;
        private Label _selectionLabel;
        private Button _idleButton;
        // overlays
        private VisualElement _drawer, _radial, _box, _endPanel;
        private Label _toast, _endTitle, _endStats;
        private Button _attackToast;
        private float _toastUntil, _attackToastUntil, _radialUntil;
        private Vector3 _radialGround, _attackPos;

        private static readonly Color Panel = new Color(0.09f, 0.1f, 0.13f, 0.88f);
        private static readonly Color Accent = new Color(0.23f, 0.51f, 0.96f);
        private static readonly Color Danger = new Color(0.72f, 0.2f, 0.18f, 0.95f);

        public void Bind(IMatchSession session, PlayerController player, ViewCatalogColors colors)
        {
            _session = session;
            _player = player;
            _doc = GetComponent<UIDocument>();
            _root = _doc.rootVisualElement;
            Build(_root, colors);
            _player.SelectionChanged += RefreshActions;
            _player.ModeChanged += RefreshActions;
            _player.CommandRejected += r => Toast(RejectText(r));
            _player.RadialRequested += ShowRadial;
            RefreshActions();
        }

        /// <summary>True when a screen point lands on a HUD element (so gestures ignore it).</summary>
        public bool IsPointerOver(Vector2 screen)
        {
            if (_root == null || _root.panel == null) return false;
            VisualElement hit = _root.panel.Pick(ScreenToPanel(screen));
            return hit != null && hit != _root && hit.pickingMode != PickingMode.Ignore;
        }

        private Vector2 ScreenToPanel(Vector2 screen)
        {
            if (_root == null || _root.panel == null) return new Vector2(screen.x, Screen.height - screen.y);
            return RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(screen.x, Screen.height - screen.y));
        }

        public void HideRadial()
        {
            if (_radial != null) _radial.style.display = DisplayStyle.None;
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
            _age.text = AgeText(w, ps);
            _debug.text = "t" + w.Tick + " #" + (w.LastHash & 0xFFFF).ToString("X4");
            int avail = ps.ShipmentsAvailable;
            _shipmentsButton.text = avail > 0 ? "Shipments (" + avail + ")" : "Shipments " + Whole(ps.Xp) + "/" + Whole(ProductionSystem.ShipmentCost(w, ps, ps.ShipmentsSent));
            _shipmentsButton.style.backgroundColor = avail > 0 ? new Color(0.85f, 0.6f, 0.15f) : new Color(0.25f, 0.27f, 0.32f);

            int idle = _player.CountIdleVillagers();
            _idleButton.text = idle > 0 ? "Idle " + idle : "Idle";
            _idleButton.style.opacity = idle > 0 ? 1f : 0.45f;

            float now = Time.unscaledTime;
            if (_toast.style.display.value == DisplayStyle.Flex && now > _toastUntil) _toast.style.display = DisplayStyle.None;
            if (_attackToast.style.display.value == DisplayStyle.Flex && now > _attackToastUntil) _attackToast.style.display = DisplayStyle.None;
            if (_radial.style.display.value == DisplayStyle.Flex && now > _radialUntil) _radial.style.display = DisplayStyle.None;

            foreach (SimEvent ev in w.Events)
            {
                if (ev.Kind == SimEventKind.CommandRejected && ev.A == _session.LocalPlayer) Toast(RejectText((CommandRejectReason)ev.B));
                else if (ev.Kind == SimEventKind.UnderAttack && ev.A == _session.LocalPlayer) ShowAttackToast(w.TargetPoint(ev.Entity));
                else if (ev.Kind == SimEventKind.AgeAdvanced && ev.A == _session.LocalPlayer) Toast("Welcome to the " + w.Defs.Ages[ev.B].Def.name + " Age");
                else if (ev.Kind == SimEventKind.ResearchFinished && ev.A == _session.LocalPlayer) Toast(w.Defs.Techs[ev.B].Def.name + " researched");
                else if (ev.Kind == SimEventKind.ShipmentArrived && ev.A == _session.LocalPlayer) Toast("Shipment arrived: " + w.Defs.Techs[ev.B].Def.name);
                else if (ev.Kind == SimEventKind.MatchEnded) ShowEnd(ev.A);
                else if (ev.Kind == SimEventKind.ConstructionFinished && w.Identities.TryGet(ev.Entity, out Identity cid) && cid.Player == _session.LocalPlayer)
                    RefreshActions();
            }

            RefreshLiveLabels();
            _minimap.Tick(w, _session.LocalPlayer, _player.Camera);
            UpdateSelectionBox();
        }

        private static string Whole(Fix64 v) => v.FloorToInt().ToString();

        private static string AgeText(World w, PlayerState ps)
        {
            string roman = ps.Age == 0 ? "I" : ps.Age == 1 ? "II" : ps.Age == 2 ? "III" : "IV";
            string s = "Age " + roman + " · " + w.Defs.Ages[ps.Age].Def.name;
            if (ps.AgeUpBuilding != 0) s += "  (advancing " + Mathf.CeilToInt(ps.AgeUpRemaining / (float)SimConstants.TickRate) + " s)";
            return s;
        }

        // ---- layout -------------------------------------------------------------------------

        private void Build(VisualElement root, ViewCatalogColors colors)
        {
            root.style.flexGrow = 1;
            root.pickingMode = PickingMode.Ignore;

            // Top bar
            var top = Row(root);
            top.pickingMode = PickingMode.Position;
            top.style.justifyContent = Justify.SpaceBetween;
            top.style.flexWrap = Wrap.Wrap;
            top.style.backgroundColor = Panel;
            top.style.paddingLeft = top.style.paddingRight = 12;
            top.style.paddingTop = top.style.paddingBottom = 6;
            top.style.marginTop = SafeTop();
            _food = Chip(top, new Color(0.6f, 0.9f, 0.55f));
            _wood = Chip(top, new Color(0.85f, 0.7f, 0.45f));
            _gold = Chip(top, new Color(0.98f, 0.85f, 0.4f));
            _pop = Chip(top, Color.white);
            _age = Chip(top, new Color(0.8f, 0.85f, 0.95f));
            _shipmentsButton = SmallButton(top, "Shipments", ToggleDrawer);
            _debug = Chip(top, new Color(0.55f, 0.6f, 0.65f));
            _debug.style.fontSize = 11;

            // Middle: minimap on the left, the rest lets touches through
            var middle = Row(root);
            middle.style.flexGrow = 1;
            middle.style.alignItems = Align.FlexStart;
            _minimap = new MinimapView(_session.World, colors, OnMinimapTap);
            _minimap.Element.style.marginLeft = 8;
            _minimap.Element.style.marginTop = 8;
            middle.Add(_minimap.Element);

            // Toasts (centered, above the sheet)
            _toast = ToastLabel(root, Danger);
            _attackToast = new Button(() => { _player.Camera.CenterOn(_attackPos, 0.25f); _attackToast.style.display = DisplayStyle.None; }) { text = "Under attack! — Go" };
            StyleButton(_attackToast, Danger);
            _attackToast.style.alignSelf = Align.Center;
            _attackToast.style.marginBottom = 8;
            _attackToast.style.display = DisplayStyle.None;
            root.Add(_attackToast);

            // Idle villager button (left, just above the sheet)
            _idleButton = new Button(() => { if (_player.SelectNextIdleVillager() == 0) Toast("No idle villagers"); }) { text = "Idle" };
            StyleButton(_idleButton, new Color(0.25f, 0.55f, 0.35f));
            _idleButton.style.alignSelf = Align.FlexStart;
            _idleButton.style.marginLeft = 12;
            _idleButton.style.marginBottom = 6;
            root.Add(_idleButton);

            // Bottom sheet
            _sheet = new VisualElement();
            _sheet.style.backgroundColor = Panel;
            _sheet.style.paddingLeft = _sheet.style.paddingRight = 12;
            _sheet.style.paddingTop = 8;
            _sheet.style.paddingBottom = 8 + SafeBottom();
            _sheet.style.borderTopLeftRadius = _sheet.style.borderTopRightRadius = 16;
            root.Add(_sheet);

            _selectionLabel = new Label("Tap a villager to begin") { pickingMode = PickingMode.Ignore };
            _selectionLabel.style.color = Color.white;
            _selectionLabel.style.fontSize = 14;
            _selectionLabel.style.marginBottom = 6;
            _sheet.Add(_selectionLabel);

            _cards = Row(_sheet);
            _cards.style.flexWrap = Wrap.Wrap;
            _cards.style.marginBottom = 4;

            _actions = Row(_sheet);
            _actions.style.flexWrap = Wrap.Wrap;

            // Overlays (absolute)
            _drawer = new VisualElement();
            _drawer.style.position = Position.Absolute;
            _drawer.style.right = 8; _drawer.style.top = 60 + SafeTop();
            _drawer.style.width = 280;
            _drawer.style.backgroundColor = Panel;
            _drawer.style.paddingLeft = _drawer.style.paddingRight = _drawer.style.paddingTop = _drawer.style.paddingBottom = 10;
            _drawer.style.borderTopLeftRadius = _drawer.style.borderTopRightRadius = _drawer.style.borderBottomLeftRadius = _drawer.style.borderBottomRightRadius = 12;
            _drawer.style.display = DisplayStyle.None;
            root.Add(_drawer);

            _radial = new VisualElement();
            _radial.style.position = Position.Absolute;
            _radial.style.display = DisplayStyle.None;
            root.Add(_radial);

            _box = new VisualElement { pickingMode = PickingMode.Ignore };
            _box.style.position = Position.Absolute;
            _box.style.borderTopWidth = _box.style.borderBottomWidth = _box.style.borderLeftWidth = _box.style.borderRightWidth = 2;
            _box.style.borderTopColor = _box.style.borderBottomColor = _box.style.borderLeftColor = _box.style.borderRightColor = Color.white;
            _box.style.backgroundColor = new Color(1f, 1f, 1f, 0.12f);
            _box.style.display = DisplayStyle.None;
            root.Add(_box);

            _endPanel = new VisualElement();
            _endPanel.style.position = Position.Absolute;
            _endPanel.style.left = 0; _endPanel.style.right = 0; _endPanel.style.top = 0; _endPanel.style.bottom = 0;
            _endPanel.style.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            _endPanel.style.alignItems = Align.Center;
            _endPanel.style.justifyContent = Justify.Center;
            _endPanel.style.display = DisplayStyle.None;
            _endTitle = new Label("Victory") { pickingMode = PickingMode.Ignore };
            _endTitle.style.fontSize = 40; _endTitle.style.color = Color.white; _endTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _endStats = new Label { pickingMode = PickingMode.Ignore };
            _endStats.style.fontSize = 16; _endStats.style.color = new Color(0.85f, 0.88f, 0.92f); _endStats.style.marginTop = 8; _endStats.style.marginBottom = 20;
            var again = new Button(() => SceneManager.LoadScene(SceneManager.GetActiveScene().name)) { text = "Play again" };
            StyleButton(again, Accent);
            _endPanel.Add(_endTitle); _endPanel.Add(_endStats); _endPanel.Add(again);
            root.Add(_endPanel);
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
            l.style.fontSize = 14;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.marginRight = 10;
            parent.Add(l);
            return l;
        }

        private static Label ToastLabel(VisualElement parent, Color bg)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.alignSelf = Align.Center;
            l.style.backgroundColor = bg;
            l.style.color = Color.white;
            l.style.fontSize = 15;
            l.style.paddingLeft = l.style.paddingRight = 16;
            l.style.paddingTop = l.style.paddingBottom = 8;
            l.style.borderTopLeftRadius = l.style.borderTopRightRadius = l.style.borderBottomLeftRadius = l.style.borderBottomRightRadius = 10;
            l.style.marginBottom = 8;
            l.style.display = DisplayStyle.None;
            parent.Add(l);
            return l;
        }

        private static void StyleButton(Button b, Color bg, bool enabled = true)
        {
            b.style.minHeight = 44;
            b.style.fontSize = 13;
            b.style.marginRight = 6;
            b.style.marginBottom = 6;
            b.style.paddingLeft = b.style.paddingRight = 10;
            b.style.borderTopLeftRadius = b.style.borderTopRightRadius = b.style.borderBottomLeftRadius = b.style.borderBottomRightRadius = 10;
            b.style.backgroundColor = enabled ? bg : new Color(0.3f, 0.32f, 0.36f);
            b.style.color = Color.white;
            b.style.borderTopWidth = b.style.borderBottomWidth = b.style.borderLeftWidth = b.style.borderRightWidth = 0;
            b.SetEnabled(enabled);
        }

        private static Button SmallButton(VisualElement parent, string text, System.Action onClick)
        {
            var b = new Button(onClick) { text = text };
            StyleButton(b, Accent);
            b.style.minHeight = 32;
            b.style.marginBottom = 0;
            parent.Add(b);
            return b;
        }

        private Button ActionButton(string text, System.Action onClick, bool enabled = true, Color? color = null)
        {
            var b = new Button(onClick) { text = text };
            StyleButton(b, color ?? Accent, enabled);
            b.style.minWidth = 84;
            _actions.Add(b);
            return b;
        }

        // ---- selection sheet -------------------------------------------------------------------

        private void RefreshActions()
        {
            _cards.Clear();
            _actions.Clear();
            World w = _session.World;
            int me = _session.LocalPlayer;
            BakedDefs defs = w.DefsOf(me);
            PlayerState ps = w.Players[me];

            if (_player.Mode == TapMode.Build)
            {
                _selectionLabel.text = "Place " + defs.Buildings[_player.BuildIndex].Def.name + " — tap the ground";
                ActionButton("Cancel", () => _player.CancelBuild(), true, Danger);
                return;
            }
            if (_player.Mode == TapMode.AttackMove)
            {
                _selectionLabel.text = "Attack-move — tap where to go";
                ActionButton("Cancel", () => _player.SetMode(TapMode.Normal), true, Danger);
                return;
            }

            IReadOnlyList<int> sel = _player.Selection;
            if (sel.Count == 0)
            {
                _selectionLabel.text = "Tap a villager, then a tree or berries · long-press for orders";
                return;
            }

            int building = _player.SelectedBuilding();
            if (building != 0) { BuildingActions(w, me, defs, ps, building); return; }

            int[] units = _player.SelectedUnits();
            BuildCards(w, units);
            _selectionLabel.text = units.Length == 1 ? w.UnitDefOf(units[0]).Def.name : units.Length + " units";

            int[] builders = _player.SelectedBuilders();
            if (builders.Length > 0)
            {
                for (int i = 0; i < defs.Buildings.Length; i++)
                {
                    BakedBuilding b = defs.Buildings[i];
                    if (b.Age > ps.Age) continue;
                    if (b.Limit > 0 && w.CountBuildings(me, i, true) >= b.Limit) continue;
                    int index = i;
                    bool affordable = ps.CanAfford(b.Cost);
                    ActionButton(b.Def.name + "\n" + CostText(w, b.Cost), () => _player.BeginBuild(index), affordable);
                }
            }
            if (_player.SelectedSoldiers().Length > 0)
                ActionButton("Attack-move", () => _player.SetMode(TapMode.AttackMove), true, new Color(0.8f, 0.35f, 0.25f));
            ActionButton("Stop", () => _player.Stop(), true, new Color(0.35f, 0.38f, 0.45f));
        }

        private void BuildCards(World w, int[] units)
        {
            var counts = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (int e in units)
            {
                int def = w.Identities.Get(e).DefIndex;
                if (!counts.ContainsKey(def)) { counts[def] = 0; order.Add(def); }
                counts[def]++;
            }
            if (order.Count <= 1) return;
            foreach (int def in order)
            {
                int d = def;
                var card = new Button(() => _player.SubSelect(d)) { text = w.DefsOf(_session.LocalPlayer).Units[def].Def.name + " ×" + counts[def] };
                StyleButton(card, new Color(0.2f, 0.22f, 0.28f));
                card.style.minHeight = 36;
                _cards.Add(card);
            }
        }

        private void BuildingActions(World w, int me, BakedDefs defs, PlayerState ps, int building)
        {
            Identity id = w.Identities.Get(building);
            BakedBuilding b = defs.Buildings[id.DefIndex];
            bool site = w.Constructions.Has(building);
            _selectionLabel.text = b.Def.name + (site ? " (under construction)" : "");
            if (site)
            {
                ActionButton("Cancel site", () => _player.Submit(new CancelCommand(me, building)), true, Danger);
                return;
            }

            if (w.Queues.Has(building))
            {
                foreach (int ui in b.Trains)
                {
                    BakedUnit u = defs.Units[defs.Replace(ui)];
                    int unit = ui;
                    CommandRejectReason why = TrainCommand.Validate(w, me, building, unit);
                    bool ok = why == CommandRejectReason.None || why == CommandRejectReason.NotAffordable;
                    string label = u.Def.name + "\n" + CostText(w, u.Cost) + (why == CommandRejectReason.WrongAge ? "  (Age " + (u.Age + 1) + ")" : "");
                    ActionButton(label, () => _player.Submit(new TrainCommand(me, building, unit)), ok && why == CommandRejectReason.None);
                }
                if (w.Queues.Get(building).Count > 0)
                    ActionButton("Cancel last", () => _player.Submit(new CancelCommand(me, building)), true, Danger);
            }

            foreach (int tech in b.Researches)
            {
                BakedTech t = defs.Techs[tech];
                if (ps.Researched[tech]) continue;
                int techIndex = tech;
                CommandRejectReason why = ResearchCommand.Validate(w, me, building, techIndex);
                string label = t.Def.name + "\n" + CostText(w, t.Cost) + (why == CommandRejectReason.WrongAge ? "  (Age " + (t.Age + 1) + ")" : "");
                ActionButton(label, () => _player.Submit(new ResearchCommand(me, building, techIndex)), why == CommandRejectReason.None, new Color(0.45f, 0.35f, 0.75f));
            }

            if (ps.Age + 1 < w.Defs.Ages.Length && w.Defs.Ages[ps.Age + 1].Def.at == b.Id)
            {
                BakedAge next = w.Defs.Ages[ps.Age + 1];
                CommandRejectReason why = AgeUpCommand.Validate(w, me, building);
                ActionButton("Age up: " + next.Def.name + "\n" + CostText(w, next.Cost),
                             () => _player.Submit(new AgeUpCommand(me, building)), why == CommandRejectReason.None, new Color(0.85f, 0.6f, 0.15f));
            }
        }

        /// <summary>Queue / research progress in the selection label (cheap, every frame).</summary>
        private void RefreshLiveLabels()
        {
            int building = _player.SelectedBuilding();
            if (building == 0 || !_session.World.IsAlive(building)) return;
            World w = _session.World;
            BakedBuilding b = w.BuildingDefOf(building);
            string s = b.Def.name;
            if (w.Constructions.TryGet(building, out Construction c)) s += "  ·  building " + Mathf.RoundToInt(c.Progress.ToFloat() * 100f) + "%";
            if (w.Queues.TryGet(building, out ProductionQueue q) && q.Count > 0)
                s += "  ·  " + w.DefsOf(_session.LocalPlayer).Units[q.Get(0)].Def.name + " " + Mathf.CeilToInt(q.HeadRemaining / (float)SimConstants.TickRate) + " s" + (q.Count > 1 ? " (+" + (q.Count - 1) + ")" : "");
            if (w.Researches.TryGet(building, out Research r))
                s += "  ·  " + w.Defs.Techs[r.Tech].Def.name + " " + Mathf.CeilToInt(r.Remaining / (float)SimConstants.TickRate) + " s";
            _selectionLabel.text = s;
        }

        private static string CostText(World w, Fix64[] cost)
        {
            var parts = new List<string>();
            for (int i = 0; i < cost.Length; i++)
                if (!cost[i].IsZero) parts.Add(cost[i].FloorToInt() + " " + w.Defs.Data.Economy.resources[i]);
            return parts.Count == 0 ? "free" : string.Join(", ", parts);
        }

        // ---- shipments drawer ---------------------------------------------------------------------

        private void ToggleDrawer()
        {
            bool open = _drawer.style.display.value != DisplayStyle.Flex;
            _drawer.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (open) FillDrawer();
        }

        private void FillDrawer()
        {
            _drawer.Clear();
            World w = _session.World;
            int me = _session.LocalPlayer;
            PlayerState ps = w.Players[me];
            var title = new Label("Home City — " + Whole(ps.Xp) + " XP, next " + Whole(ProductionSystem.ShipmentCost(w, ps, ps.ShipmentsSent))) { pickingMode = PickingMode.Ignore };
            title.style.color = Color.white; title.style.fontSize = 14; title.style.unityFontStyleAndWeight = FontStyle.Bold; title.style.marginBottom = 6;
            _drawer.Add(title);
            if (ps.CivIndex < 0) return;
            foreach (string shipId in w.Defs.Data.Civs[ps.CivIndex].homeCity.deck)
            {
                if (!w.Defs.Data.TryTechIndex(shipId, out int tech)) continue;
                BakedTech t = w.Defs.Techs[tech];
                CommandRejectReason why = ShipmentCommand.Validate(w, me, tech);
                string suffix = why == CommandRejectReason.WrongAge ? "  (Age " + (t.Age + 1) + ")" : why == CommandRejectReason.AlreadyResearched ? "  (sent)" : "";
                int techIndex = tech;
                var b = new Button(() => { _player.Submit(new ShipmentCommand(me, techIndex)); _drawer.style.display = DisplayStyle.None; }) { text = t.Def.name + suffix };
                StyleButton(b, why == CommandRejectReason.None ? new Color(0.85f, 0.6f, 0.15f) : new Color(0.3f, 0.32f, 0.36f), why == CommandRejectReason.None);
                b.style.unityTextAlign = TextAnchor.MiddleLeft;
                _drawer.Add(b);
            }
            var close = new Button(() => _drawer.style.display = DisplayStyle.None) { text = "Close" };
            StyleButton(close, new Color(0.35f, 0.38f, 0.45f));
            _drawer.Add(close);
        }

        // ---- radial menu ---------------------------------------------------------------------------

        private void ShowRadial(Vector2 screen, Vector3 ground)
        {
            if (_player.Selection.Count == 0) return;
            _radialGround = ground;
            _radial.Clear();
            Vector2 c = ScreenToPanel(screen);
            const float r = 64f, size = 56f;
            AddWedge(c + new Vector2(0, -r), size, "Move", new Color(0.23f, 0.51f, 0.96f), () => _player.RadialMove(_radialGround));
            if (_player.SelectedSoldiers().Length > 0)
                AddWedge(c + new Vector2(r, 0), size, "Attack", new Color(0.8f, 0.35f, 0.25f), () => _player.RadialAttackMove(_radialGround));
            AddWedge(c + new Vector2(0, r), size, "Stop", new Color(0.35f, 0.38f, 0.45f), () => _player.Stop());
            AddWedge(c + new Vector2(-r, 0), size, "×", new Color(0.2f, 0.2f, 0.24f), null);
            _radial.style.display = DisplayStyle.Flex;
            _radialUntil = Time.unscaledTime + 4f;
        }

        private void AddWedge(Vector2 center, float size, string text, Color color, System.Action action)
        {
            var b = new Button(() => { action?.Invoke(); _radial.style.display = DisplayStyle.None; }) { text = text };
            StyleButton(b, color);
            b.style.position = Position.Absolute;
            b.style.left = center.x - size * 0.5f;
            b.style.top = center.y - size * 0.5f;
            b.style.width = size; b.style.height = size;
            b.style.minHeight = size;
            b.style.borderTopLeftRadius = b.style.borderTopRightRadius = b.style.borderBottomLeftRadius = b.style.borderBottomRightRadius = size * 0.5f;
            b.style.marginRight = b.style.marginBottom = 0;
            _radial.Add(b);
        }

        // ---- selection box, minimap, toasts, end -------------------------------------------------------

        private void UpdateSelectionBox()
        {
            if (!_player.BoxActive) { if (_box.style.display.value == DisplayStyle.Flex) _box.style.display = DisplayStyle.None; return; }
            Rect r = _player.BoxScreenRect;
            Vector2 min = ScreenToPanel(new Vector2(r.xMin, r.yMax));   // screen y up → panel y down
            Vector2 max = ScreenToPanel(new Vector2(r.xMax, r.yMin));
            _box.style.left = min.x; _box.style.top = min.y;
            _box.style.width = Mathf.Max(1f, max.x - min.x); _box.style.height = Mathf.Max(1f, max.y - min.y);
            _box.style.display = DisplayStyle.Flex;
        }

        private void OnMinimapTap(Vector2 mapCell)
        {
            _player.Camera.CenterOn(new Vector3(mapCell.x, 0f, mapCell.y), 0.2f);
        }

        private void Toast(string text)
        {
            _toast.text = text;
            _toast.style.display = DisplayStyle.Flex;
            _toastUntil = Time.unscaledTime + 2f;
        }

        private void ShowAttackToast(FixVec2 at)
        {
            _attackPos = PlayerController.ToWorld(at);
            _attackToast.style.display = DisplayStyle.Flex;
            _attackToastUntil = Time.unscaledTime + 5f;
            _minimap.Ping(at);
        }

        private void ShowEnd(int winner)
        {
            World w = _session.World;
            PlayerState ps = w.Players[_session.LocalPlayer];
            _endTitle.text = winner == _session.LocalPlayer ? "Victory" : winner == -2 ? "Draw" : "Defeat";
            _endStats.text = $"Units killed {ps.UnitsKilled} · lost {ps.UnitsLost} · buildings razed {ps.BuildingsRazed}\n" +
                             $"Age {ps.Age + 1} · shipments {ps.ShipmentsSent} · {w.Tick / SimConstants.TickRate / 60} min";
            _endPanel.style.display = DisplayStyle.Flex;
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
                case CommandRejectReason.WrongAge: return "Advance to the next Age first";
                case CommandRejectReason.AlreadyResearched: return "Already done";
                case CommandRejectReason.NotEnoughXp: return "Not enough experience";
                case CommandRejectReason.Busy: return "Already in progress";
                default: return "Can't do that";
            }
        }

        private static float SafeTop() => Mathf.Max(0f, Screen.height - Screen.safeArea.yMax) / Mathf.Max(1f, Screen.dpi / 160f);
        private static float SafeBottom() => Mathf.Max(0f, Screen.safeArea.yMin) / Mathf.Max(1f, Screen.dpi / 160f);
    }

    /// <summary>Player colours the HUD needs, decoupled from the presentation catalog.</summary>
    public sealed class ViewCatalogColors
    {
        private readonly System.Func<int, Color> _lookup;
        public ViewCatalogColors(System.Func<int, Color> lookup) { _lookup = lookup; }
        public Color For(int player) => _lookup(player);
    }

    /// <summary>
    /// Minimap: one pixel per cell, redrawn 4× per second from the simulation (terrain, nodes,
    /// units, buildings, camera footprint). Tap or drag to move the camera.
    /// </summary>
    public sealed class MinimapView
    {
        public readonly VisualElement Element;
        private readonly Texture2D _tex;
        private readonly Color32[] _base;
        private readonly Color32[] _pixels;
        private readonly ViewCatalogColors _colors;
        private readonly int _w, _h;
        private float _nextRedraw;
        private FixVec2 _ping;
        private float _pingUntil;
        private const float SizeDp = 112f;

        public MinimapView(World world, ViewCatalogColors colors, System.Action<Vector2> onTap)
        {
            _colors = colors;
            _w = world.Map.Width; _h = world.Map.Height;
            _tex = new Texture2D(_w, _h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            _base = new Color32[_w * _h];
            _pixels = new Color32[_w * _h];
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                    _base[y * _w + x] = Presentation.GroundView.TerrainColor(world.Map.TerrainAt(x, y));

            Element = new VisualElement();
            Element.style.width = SizeDp;
            Element.style.height = SizeDp * _h / _w;
            Element.style.backgroundImage = new StyleBackground(_tex);
            Element.style.borderTopWidth = Element.style.borderBottomWidth = Element.style.borderLeftWidth = Element.style.borderRightWidth = 2;
            Element.style.borderTopColor = Element.style.borderBottomColor = Element.style.borderLeftColor = Element.style.borderRightColor = new Color(0f, 0f, 0f, 0.7f);
            Element.style.opacity = 0.92f;

            void Handle(Vector2 local)
            {
                float fx = Mathf.Clamp01(local.x / Element.resolvedStyle.width);
                float fy = 1f - Mathf.Clamp01(local.y / Element.resolvedStyle.height);
                onTap(new Vector2(fx * _w, fy * _h));
            }
            Element.RegisterCallback<PointerDownEvent>(e => { Handle(e.localPosition); e.StopPropagation(); });
            Element.RegisterCallback<PointerMoveEvent>(e => { if (e.pressedButtons != 0) { Handle(e.localPosition); e.StopPropagation(); } });
        }

        public void Ping(FixVec2 at) { _ping = at; _pingUntil = Time.unscaledTime + 4f; }

        public void Tick(World w, int localPlayer, CameraRig cam)
        {
            if (Time.unscaledTime < _nextRedraw) return;
            _nextRedraw = Time.unscaledTime + 0.25f;
            System.Array.Copy(_base, _pixels, _base.Length);

            for (int i = 0; i < w.Footprints.Count; i++)
            {
                int e = w.Footprints.EntityAt(i);
                Identity id = w.Identities.Get(e);
                Footprint fp = w.Footprints.At(i);
                Color32 c = id.Kind == EntityKind.Building ? (Color32)_colors.For(id.Player) : NodeColor(w.Defs.Nodes[id.DefIndex].Id);
                for (int y = fp.Y; y < fp.Y + fp.H; y++)
                    for (int x = fp.X; x < fp.X + fp.W; x++)
                        if (x >= 0 && y >= 0 && x < _w && y < _h) _pixels[y * _w + x] = c;
            }
            for (int i = 0; i < w.Movers.Count; i++)
            {
                int e = w.Movers.EntityAt(i);
                Identity id = w.Identities.Get(e);
                FixVec2 p = w.Positions.Get(e).Value;
                int x = p.CellX, y = p.CellY;
                if (x >= 0 && y >= 0 && x < _w && y < _h) _pixels[y * _w + x] = id.Player == localPlayer ? (Color32)Color.white : (Color32)_colors.For(id.Player);
            }
            // Camera footprint outline
            if (cam != null)
            {
                Vector2[] corners = { new Vector2(0, 0), new Vector2(Screen.width, 0), new Vector2(Screen.width, Screen.height), new Vector2(0, Screen.height) };
                for (int k = 0; k < 4; k++)
                    if (cam.ScreenToGround(corners[k], out Vector3 a) && cam.ScreenToGround(corners[(k + 1) % 4], out Vector3 b))
                        Line(a, b, new Color32(255, 255, 255, 160));
            }
            if (Time.unscaledTime < _pingUntil && ((int)(Time.unscaledTime * 4f) & 1) == 0)
            {
                int px = _ping.CellX, py = _ping.CellY;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int x = px + dx, y = py + dy;
                        if (x >= 0 && y >= 0 && x < _w && y < _h && (dx == 0 || dy == 0 || Mathf.Abs(dx) == Mathf.Abs(dy))) _pixels[y * _w + x] = new Color32(255, 60, 40, 255);
                    }
            }
            _tex.SetPixels32(_pixels);
            _tex.Apply(false);
        }

        private void Line(Vector3 a, Vector3 b, Color32 c)
        {
            int steps = Mathf.CeilToInt(Vector3.Distance(a, b));
            for (int s = 0; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(a, b, steps == 0 ? 0f : s / (float)steps);
                int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.z);
                if (x >= 0 && y >= 0 && x < _w && y < _h) _pixels[y * _w + x] = c;
            }
        }

        private static Color32 NodeColor(string id)
        {
            switch (id)
            {
                case "res.tree": return new Color32(30, 90, 40, 255);
                case "res.mine": return new Color32(220, 180, 50, 255);
                case "res.berries": return new Color32(150, 40, 90, 255);
                default: return new Color32(140, 90, 50, 255);
            }
        }
    }
}
