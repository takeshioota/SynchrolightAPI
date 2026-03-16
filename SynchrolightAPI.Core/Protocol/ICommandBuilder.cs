using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Protocol;

/// <summary>
/// ライト制御コマンドビルダーインターフェース
/// </summary>
public interface ICommandBuilder
{
    /// <summary>制御対象に色を設定するコマンドを構築</summary>
    Packet32 BuildSetColor(Target target, Rgb color);

    /// <summary>送信機の無線チャネル設定コマンドを構築 (FA)</summary>
    byte[] BuildTxSetChannel(byte ch);

    /// <summary>送信機の送信電力設定コマンドを構築 (FB)</summary>
    byte[] BuildTxSetPower(byte pwr);
}
