using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;

namespace Witchlight;

/// <summary>
/// Watches what this player looks like and sends a new picture once it settles.
///
/// A seraph changes in bursts. Somebody trying on a hat moves it between two slots
/// half a dozen times in as many seconds, and sending on each move would render a
/// picture, send a packet and write a file for a state nobody stayed in. So a
/// change restarts the wait rather than sending, and the picture is drawn once the
/// wait runs out. A whole afternoon at the dressing table costs one portrait.
///
/// Watches the two things that decide the face: the character inventory, which
/// holds every piece of clothing and armour worn, and the skin configuration.
/// Watches neither the hotbar nor the backpack, since a portrait is cropped to the
/// head and shoulders and what is carried never appears in it.
///
/// A slot event is only the signal to look. Armour wearing down and a garment
/// being repaired both modify the slot they sit in without changing what is worn,
/// and each sent the same picture every few minutes all session. So this compares
/// what is worn, as the item in each slot and the skin chosen in one string, and a
/// settled burst that leaves the string unchanged sends nothing. The first look
/// after joining only records the string, and the server asks for a picture itself
/// where it has none.
/// </summary>
public sealed class PortraitWatch
{
    /// <summary>
    /// How often the wait is measured and the subscriptions renewed.
    ///
    /// Both run on the same beat. Renewing on a tick rather than on a join event
    /// means a relog, a respawn and an inventory arriving late all mend themselves
    /// without being predicted.
    /// </summary>
    private const int CheckMs = 1000;

    private readonly ICoreClientAPI _capi;
    private readonly Action _send;

    private IInventory? _wearing;
    private SyncedTreeAttribute? _skin;

    /// <summary>When this character last changed, or null when it has settled.</summary>
    private DateTime? _changedAt;

    /// <summary>What was worn at the last look, or null before the first look.</summary>
    private string? _worn;

    public PortraitWatch(ICoreClientAPI capi, Action send)
    {
        _capi = capi;
        _send = send;
        capi.Event.RegisterGameTickListener(_ => Tick(), CheckMs);
    }

    /// <summary>
    /// Records a change without sending anything.
    ///
    /// Called wherever a picture has just been sent by other means, so a change
    /// arriving moments later is waited out from now rather than from when the
    /// burst began.
    /// </summary>
    public void Settled() => _changedAt = null;

    private void Tick()
    {
        Follow();

        if (_changedAt is not { } since
            || DateTime.UtcNow - since < TimeSpan.FromMilliseconds(Portraits.QuietMs))
        {
            return;
        }

        _changedAt = null;
        var worn = Worn();
        var first = _worn is null;
        if (worn == _worn)
        {
            return;
        }

        _worn = worn;
        if (!first)
        {
            _send();
        }
    }

    /// <summary>
    /// Returns what this player looks like, as one string that changes when the
    /// look does.
    ///
    /// Uses the item code in each clothing slot rather than the stack, whose
    /// durability moves with every hit taken, plus the skin configuration as the
    /// game serialises it. Reads the slots in the inventory's own order, which is
    /// fixed for a character.
    /// </summary>
    private string Worn()
    {
        var parts = new System.Text.StringBuilder();
        if (_wearing is not null)
        {
            foreach (var slot in _wearing)
            {
                parts.Append(slot?.Itemstack?.Collectible?.Code?.ToString() ?? "-").Append('|');
            }
        }

        parts.Append(_skin?.GetTreeAttribute("skinConfig")?.ToJsonToken() ?? "");
        return parts.ToString();
    }

    /// <summary>
    /// Subscribes to this player's character and unsubscribes from the last one.
    ///
    /// Idempotent and cheap when nothing moved, since the common tick compares two
    /// references and stops. Swapping character resets the wait rather than
    /// starting it, because another character is not a change to this one.
    /// </summary>
    private void Follow()
    {
        var wearing = _capi.World?.Player?.InventoryManager?
            .GetOwnInventory(GlobalConstants.characterInvClassName);
        if (!ReferenceEquals(wearing, _wearing))
        {
            if (_wearing is not null)
            {
                _wearing.SlotModified -= OnSlotModified;
            }

            _wearing = wearing;
            if (_wearing is not null)
            {
                _wearing.SlotModified += OnSlotModified;
            }

            // Look at a new character afresh. What the last one wore says nothing
            // about this one, and the first look records rather than sends.
            _worn = null;
            _changedAt = DateTime.UtcNow;
        }

        var skin = _capi.World?.Player?.Entity?.WatchedAttributes;
        if (!ReferenceEquals(skin, _skin))
        {
            _skin?.UnregisterListener(OnSkinChanged);
            _skin = skin;
            _skin?.RegisterModifiedListener("skinConfig", OnSkinChanged);
            // Subscribing to a skin is not a change to it, so this starts no wait
            // of its own. It cannot cancel one either: both halves of a character
            // arrive on the same tick when a world loads, and clearing the wait
            // here threw away the first look the branch above had just armed. The
            // watch then held nothing to compare against, so the next real change
            // read as its first look and sent nothing either.
            if (_worn is not null)
            {
                _changedAt = null;
            }
        }
    }

    private void OnSlotModified(int slot) => _changedAt = DateTime.UtcNow;

    private void OnSkinChanged() => _changedAt = DateTime.UtcNow;
}
