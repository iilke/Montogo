namespace Montogo.Protocol;

public enum PacketType : byte
{
    VideoChunk        = 0x01,
    Heartbeat         = 0x10,
    HandshakeRequest  = 0x11,
    HandshakeResponse = 0x12,
}
