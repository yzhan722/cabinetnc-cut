using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace CabinetNC.Desktop;

/// <summary>Editable stock parameters for one cnjob material kind (stock-stage card).</summary>
public sealed class StockMaterialKindVm : INotifyPropertyChanged
{
    string _widthMmText = "1200";
    string _lengthMmText = "2400";
    string _spacingMmText = "12";
    string _borderMmText = "15";
    bool _allowRotate90 = true;
    bool _allowPartsInPart;
    bool _useLeftoverPieces;
    string _leftoverXMmText = "";
    string _leftoverYMmText = "";

    string _label = "";

    public required string MaterialId { get; init; }
    public string AutoLabel { get; init; } = "";
    public string Label
    {
        get => _label;
        set
        {
            var next = value ?? "";
            if (_label == next) return;
            _label = next;
            OnPropertyChanged();
        }
    }
    public double ThicknessMm { get; init; }
    public int PanelCount { get; init; }

    public string ThicknessHint =>
        ThicknessMm > 0
            ? $"厚度 {ThicknessMm.ToString("0.##", CultureInfo.InvariantCulture)} mm · 可继续加纹理/双面等参数"
            : "厚度未指定 · 可继续加参数";

    public string WidthMmText
    {
        get => _widthMmText;
        set
        {
            if (_widthMmText == value) return;
            _widthMmText = value;
            OnPropertyChanged();
        }
    }

    public string LengthMmText
    {
        get => _lengthMmText;
        set
        {
            if (_lengthMmText == value) return;
            _lengthMmText = value;
            OnPropertyChanged();
        }
    }

    public string SpacingMmText
    {
        get => _spacingMmText;
        set
        {
            if (_spacingMmText == value) return;
            _spacingMmText = value;
            OnPropertyChanged();
        }
    }

    public string BorderMmText
    {
        get => _borderMmText;
        set
        {
            if (_borderMmText == value) return;
            _borderMmText = value;
            OnPropertyChanged();
        }
    }

    public bool AllowRotate90
    {
        get => _allowRotate90;
        set
        {
            if (_allowRotate90 == value) return;
            _allowRotate90 = value;
            OnPropertyChanged();
        }
    }

    public bool AllowPartsInPart
    {
        get => _allowPartsInPart;
        set
        {
            if (_allowPartsInPart == value) return;
            _allowPartsInPart = value;
            OnPropertyChanged();
        }
    }

    public bool UseLeftoverPieces
    {
        get => _useLeftoverPieces;
        set
        {
            if (_useLeftoverPieces == value) return;
            _useLeftoverPieces = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeftoverHint));
        }
    }

    public string LeftoverXMmText
    {
        get => _leftoverXMmText;
        set
        {
            if (_leftoverXMmText == value) return;
            _leftoverXMmText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeftoverHint));
        }
    }

    public string LeftoverYMmText
    {
        get => _leftoverYMmText;
        set
        {
            if (_leftoverYMmText == value) return;
            _leftoverYMmText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LeftoverHint));
        }
    }

    public string LeftoverHint =>
        !UseLeftoverPieces
            ? "第一张用余料（贴原点）；放不下再开整张大板"
            : HasLeftoverSheet
                ? $"第一张 {LeftoverXMm:0.#}×{LeftoverYMm:0.#} 贴原点 · 大板边距只落在外沿"
                : "填余料 X / Y，第一张贴原点";

    public bool HasLeftoverSheet =>
        UseLeftoverPieces && LeftoverXMm > 0 && LeftoverYMm > 0;

    public double WidthMm => ParsePositive(_widthMmText, 1200);
    public double LengthMm => ParsePositive(_lengthMmText, 2400);
    public double SpacingMm => ParseNonNegative(_spacingMmText, 12);
    public double BorderMm => ParseNonNegative(_borderMmText, 15);
    public double LeftoverXMm => ParsePositive(_leftoverXMmText, 0);
    public double LeftoverYMm => ParsePositive(_leftoverYMmText, 0);

    static double ParsePositive(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : fallback;

    static double ParseNonNegative(string text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : fallback;

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
