using RTS.Input;
using RTS.Presentation;
using UnityEngine;

namespace RTS.UI
{
    /// <summary>
    /// Composition root of the Skirmish scene: the only script allowed to know about
    /// Presentation, Input and UI at once. Wires them together once the match exists.
    /// </summary>
    public sealed class SkirmishGlue : MonoBehaviour
    {
        [SerializeField] private GameBootstrap bootstrap;
        [SerializeField] private PlayerController player;
        [SerializeField] private GestureRecognizer gestures;
        [SerializeField] private HudController hud;

        private void Awake()
        {
            if (bootstrap == null) bootstrap = FindFirstObjectByType<GameBootstrap>();
            if (player == null) player = FindFirstObjectByType<PlayerController>();
            if (gestures == null) gestures = FindFirstObjectByType<GestureRecognizer>();
            if (hud == null) hud = FindFirstObjectByType<HudController>();
            bootstrap.MatchStarted += OnMatchStarted;
        }

        private void OnMatchStarted(GameBootstrap b)
        {
            player.Bind(b, FallbackMaterials.GhostOk, FallbackMaterials.GhostBad);
            ViewCatalog catalog = b.View.Catalog;
            hud.Bind(b, player, new ViewCatalogColors(p => catalog != null ? catalog.PlayerColor(p) : (p == 0 ? Color.cyan : Color.red)));
            gestures.IsPointerOverUI = hud.IsPointerOver;
            gestures.Tap += _ => hud.HideRadial();
            gestures.LongPressDrag += (_, __) => hud.HideRadial();
            player.SelectionChanged += RefreshSelectionRings;
        }

        private void RefreshSelectionRings()
        {
            foreach (var kv in bootstrap.View.Views) kv.Value.SetSelected(false);
            foreach (int e in player.Selection)
                if (bootstrap.View.TryGetView(e, out EntityView v)) v.SetSelected(true);
        }
    }
}
