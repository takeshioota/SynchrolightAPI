using SynchrolightAPI.Domain;

namespace SynchrolightAPI.Protocol;

/// <summary>
/// ICommandBuilder実装: Target型に応じて適切なプロトコルコマンドを選択
/// </summary>
public class CommandBuilder : ICommandBuilder
{
    public Packet32 BuildSetColor(Target target, Rgb color)
    {
        return target switch
        {
            Target.All(var field) =>
                LightProtocol.BuildA2_GlobalColor(field, color.R, color.G, color.B),

            Target.Rows(var field, var startRow, var len) =>
                LightProtocol.BuildA3_Rows(field, startRow, len, color.R, color.G, color.B),

            Target.Cols(var field, var startCol, var len) =>
                LightProtocol.BuildA4_Cols(field, startCol, len, color.R, color.G, color.B),

            Target.Points(var field, var startRow, var startCol, var len) =>
                LightProtocol.BuildA0_Points(field, startRow, startCol, len, CreateColorArray(color, len)),

            Target.MultiCols(var field, var startCol, var colLen) =>
                LightProtocol.BuildA8_MultiColsSameColor(field, startCol, colLen, color.R, color.G, color.B),

            Target.MultiRows(var field, var startRow, var rowLen) =>
                LightProtocol.BuildAA_MultiRowsSameColor(field, startRow, rowLen, color.R, color.G, color.B),

            Target.Block(var progNo, var blockNo) =>
                LightProtocol.BuildAC_BlockColor(progNo, blockNo, color.R, color.G, color.B),

            Target.BlockSector(var progNo, var blockNo) =>
                LightProtocol.BuildAE_BlockColorSector(progNo, blockNo, color.R, color.G, color.B),

            _ => throw new ArgumentException($"Unknown target type: {target.GetType().Name}", nameof(target))
        };
    }

    public byte[] BuildTxSetChannel(byte ch) => LightProtocol.BuildTxSetChannel(ch);

    public byte[] BuildTxSetPower(byte pwr) => LightProtocol.BuildTxSetPower(pwr);

    private static (byte r, byte g, byte b)[] CreateColorArray(Rgb color, int len)
    {
        var colors = new (byte r, byte g, byte b)[len];
        Array.Fill(colors, color.ToTuple());
        return colors;
    }
}
