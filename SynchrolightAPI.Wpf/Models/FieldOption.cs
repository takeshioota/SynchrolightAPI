namespace SynchrolightAPI.Wpf.Models;

/// <summary>
/// Field値の選択肢（ComboBox用）
/// </summary>
public record FieldOption(byte Value, string Label, string Description);

/// <summary>
/// 時間間隔の選択肢（ComboBox用）
/// </summary>
public record IntervalOption(double Seconds, string Label);
