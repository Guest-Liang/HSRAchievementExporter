using HSRae.Protocol.Capture;
using HSRae.Protocol.Protobuf;

namespace HSRae.Protocol.Identity;

public static class PlayerIdentityDecoder
{
    public const uint PlayerGetTokenScRspCommandId = 81;
    private const uint LegacyPlayerGetTokenScRspCommandId = 91;
    private const uint UidFieldNumber = 15;
    private const uint MinimumPlausibleUid = 100_000_000;
    private const uint MaximumPlausibleUid = 999_999_999;

    public static bool TryDecode(CapturedPacket packet, out uint uid)
    {
        return TryDecode(packet, out uid, out _);
    }

    public static bool TryDecode(CapturedPacket packet, out uint uid, out uint fieldNumber)
    {
        ArgumentNullException.ThrowIfNull(packet);
        uid = 0;
        fieldNumber = 0;

        if (
            packet.CommandId is not PlayerGetTokenScRspCommandId and not LegacyPlayerGetTokenScRspCommandId
            || !ProtoWire.TryParse(packet.Body, out var message)
            || message is null
        )
        {
            return false;
        }

        var found = false;
        foreach (var field in message.Fields)
        {
            if (field.Number != UidFieldNumber || field.WireType != ProtoWireType.Varint)
            {
                continue;
            }

            if (field.Varint is < MinimumPlausibleUid or > MaximumPlausibleUid)
            {
                continue;
            }

            if (found)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = (uint)field.Varint;
            fieldNumber = field.Number;
            found = true;
        }

        if (found)
        {
            return true;
        }

        // The command ID is stable enough to identify the login response, while
        // protobuf field numbers can be obfuscated between game builds. If field
        // 15 moves, accept only one unambiguous nine-digit varint from this response.
        foreach (var field in message.Fields)
        {
            if (
                field.WireType != ProtoWireType.Varint
                || field.Varint is < MinimumPlausibleUid or > MaximumPlausibleUid
            )
            {
                continue;
            }

            if (found)
            {
                uid = 0;
                fieldNumber = 0;
                return false;
            }

            uid = (uint)field.Varint;
            fieldNumber = field.Number;
            found = true;
        }

        return found;
    }
}
