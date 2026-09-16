namespace Montogo.Protocol;

public enum PacketType : byte
{
    VideoChunk        = 0x01,
    Heartbeat         = 0x10,
    HandshakeRequest  = 0x11,
    HandshakeResponse = 0x12,
    Feedback          = 0x13,   // Mac → Windows: measured packet loss, drives adaptive bitrate
}
