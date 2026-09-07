using ProtoBuf;

namespace Witchlight;

/// <summary>Asks a player's client to draw them. The server sends this.</summary>
[ProtoContract]
public class PortraitRequest
{
}

/// <summary>
/// Carries a player's own likeness to the server.
///
/// The payload is a PNG rather than anything the server could reconstruct. The
/// client settles what a seraph looks like. Its skin parts are textures a
/// dedicated server does not ship, and its clothes and armour are a rendered
/// scene rather than a list of colours, so only the client can draw it.
/// </summary>
[ProtoContract]
public class PlayerPortrait
{
    /// <summary>
    /// The picture. The code that files a portrait bounds its size, and the code
    /// that receives one bounds how often it may arrive.
    /// </summary>
    [ProtoMember(1)]
    public byte[]? Png { get; set; }
}
