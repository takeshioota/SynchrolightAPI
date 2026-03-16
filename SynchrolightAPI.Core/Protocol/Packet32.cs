namespace SynchrolightAPI.Protocol;

/// <summary>
/// 32バイト固定長フレームの値型
/// </summary>
public readonly record struct Packet32
{
    public byte[] Data { get; }

    public Packet32(byte[] data)
    {
        if (data is null || data.Length != 32)
            throw new ArgumentException("Packet32 requires exactly 32 bytes.", nameof(data));
        Data = data;
    }

    /// <summary>HEX文字列表現</summary>
    public string ToHex() => LightProtocol.ToHex(Data);

    /// <summary>byte[] から暗黙変換</summary>
    public static implicit operator Packet32(byte[] data) => new(data);

    /// <summary>Packet32 から byte[] への暗黙変換</summary>
    public static implicit operator byte[](Packet32 packet) => packet.Data;
}
