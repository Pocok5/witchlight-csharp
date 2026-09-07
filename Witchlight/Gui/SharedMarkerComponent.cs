using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Draws one shared marker on the in-game map.
///
/// Subclasses the game's own waypoint component and gives it a waypoint built
/// from what the server said, plus the game's waypoint layer to borrow pictures
/// from, so a marker somebody else made looks like one of the player's own.
///
/// Only the click differs. The game's component opens a window that names the
/// waypoint by its place in the player's list, and a shared marker has no such
/// place, so a click opens this mod's window, which names it by key.
/// </summary>
public sealed class SharedMarkerComponent : WaypointMapComponent
{
    /// <summary>How close a click has to land, using the game's own reach.</summary>
    private const float ReachPx = 8f;

    private readonly WaypointMapLayer _game;
    private readonly Waypoint _waypoint;
    private readonly SharedMarker _marker;
    private readonly Action<SharedMarker> _open;

    public SharedMarkerComponent(
        ICoreClientAPI capi, WaypointMapLayer game, SharedMarker marker, Action<SharedMarker> open)
        : this(capi, game, marker, open, Drawn(marker))
    {
    }

    private SharedMarkerComponent(
        ICoreClientAPI capi,
        WaypointMapLayer game,
        SharedMarker marker,
        Action<SharedMarker> open,
        Waypoint waypoint)
        : base(0, waypoint, game, capi)
    {
        _game = game;
        _waypoint = waypoint;
        _marker = marker;
        _open = open;
    }

    /// <summary>
    /// The waypoint the game's drawing reads. It is never in the game's waypoint
    /// list.
    /// </summary>
    private static Waypoint Drawn(SharedMarker marker) => new()
    {
        Position = new Vec3d(marker.X, marker.Y, marker.Z),
        Title = Label(marker),
        Icon = marker.Icon,
        Color = marker.Color,
        OwningPlayerUid = null,
        Pinned = marker.Pinned,
        Guid = "witchlight:" + marker.Key,
    };

    /// <summary>
    /// The owner's name, shown on the marker. Every death marker is called "You
    /// died here", which needs a name against it on a shared map.
    /// </summary>
    public static string Label(SharedMarker marker)
    {
        var title = Markers.Title(marker.Title);
        return string.IsNullOrEmpty(marker.Owner) ? title : $"{title} ({marker.Owner})";
    }

    /// <summary>
    /// Draws the marker once the game has the pictures to draw with.
    ///
    /// The waypoint layer loads them when the map opens. Before that the game's
    /// own drawing throws reaching for them, so this draws nothing.
    /// </summary>
    public override void Render(GuiElementMap map, float dt)
    {
        if (_game.texturesByIcon is null || _game.quadModel is null)
        {
            return;
        }
        base.Render(map, dt);
    }

    /// <summary>
    /// Runs the game's own hover, which makes the marker swell under the mouse,
    /// with this marker's words in place of the game's. The game would say
    /// "Waypoint 0", naming a place in a list this marker is not in.
    /// </summary>
    public override void OnMouseMove(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        base.OnMouseMove(args, mapElem, new StringBuilder());
        if (Under(args, mapElem))
        {
            hoverText.AppendLine(_waypoint.Title);
        }
    }

    /// <summary>Opens this mod's window on the marker for a right click.</summary>
    public override void OnMouseUpOnElement(MouseEvent args, GuiElementMap mapElem)
    {
        if (args.Button != EnumMouseButton.Right || !Under(args, mapElem))
        {
            return;
        }
        _open(_marker);
        args.Handled = true;
    }

    /// <summary>
    /// Returns true when the mouse is on the marker. Uses the game's own
    /// arithmetic, including where a pinned marker is held against the map's edge.
    /// </summary>
    private bool Under(MouseEvent args, GuiElementMap mapElem)
    {
        var view = new Vec2f();
        mapElem.TranslateWorldPosToViewPos(_waypoint.Position, ref view);
        var bounds = mapElem.Bounds;
        double x = view.X + bounds.renderX;
        double y = view.Y + bounds.renderY;
        if (_waypoint.Pinned)
        {
            mapElem.ClampButPreserveAngle(ref view, 2);
            x = GameMath.Clamp(view.X + bounds.renderX,
                bounds.renderX + 2, bounds.renderX + bounds.InnerWidth - 2);
            y = GameMath.Clamp(view.Y + bounds.renderY,
                bounds.renderY + 2, bounds.renderY + bounds.InnerHeight - 2);
        }

        var reach = RuntimeEnv.GUIScale * ReachPx;
        return Math.Abs(args.X - x) < reach && Math.Abs(args.Y - y) < reach;
    }
}
