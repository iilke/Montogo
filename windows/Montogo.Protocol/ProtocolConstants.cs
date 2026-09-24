namespace Montogo.Protocol;

public static class ProtocolConstants
{
    public const ushort Magic          = 0x474D;
    public const byte   CurrentVersion = 9; // v9: response HMAC binds macPubKey (full-transcript auth) (v8: authenticated feedback)

    public const int VideoPort = 47921;

    public const int MaxChunkPayload        = 1400; // max plaintext H.264 bytes per chunk
    public const int GcmTagSize             = 16;   // AES-256-GCM authentication tag (bytes)
    public const int GcmNonceSize           = 12;   // AES-GCM nonce (bytes); derived from SequenceNum
    public const int HeartbeatIntervalMs    = 1000;
    public const int HandshakeRetryMs       = 500;
    public const int FrameAssemblyTimeoutMs = 100;
}
