using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CabinetNC.Domain.Nesting;

namespace CabinetNC.Desktop;

public sealed record GrainChoice(string Key, string Label);

public sealed class StockPanelGrainRow : INotifyPropertyChanged
{
    string _grainKey = "none";

    public required string PanelId { get; init; }
    public required string DisplayName { get; init; }
    public IReadOnlyList<GrainChoice> GrainChoices { get; } =
    [
        new("none", "无"),
        new("X", "X"),
        new("Y", "Y"),
    ];

    public string GrainKey
    {
        get => _grainKey;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "none" : value;
            if (_grainKey == next) return;
            _grainKey = next;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Editable stock parameters for one cnjob material kind (stock-stage card).</summary>
public sealed class StockMaterialKindVm : INotifyPropertyChanged
{
    string _widthMmText = "1200";
    string _lengthMmText = "2400";
    string _spacingMmText = "12";
    string _borderMmText = "15";
    bool _allowRotate90 = true;
    string _sheetGrainKey = "none";
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

    public IReadOnlyList<GrainChoice> SheetGrainChoices { get; } =
    [
        new("none", "无纹理"),
        new("length", "沿长度"),
        new("width", "沿宽度"),
    ];

    public string SheetGrainKey
    {
        get => _sheetGrainKey;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "none" : value;
            if (_sheetGrainKey == next) return;
            _sheetGrainKey = next;
            OnPropertyChanged();
        }
    }

    public SheetGrainKind SheetGrain => GrainAlign.ParseSheet(_sheetGrainKey);

    public List<StockPanelGrainRow> PanelGrains { get; set; } = [];

    bool _panelGrainsExpanded;

    public bool PanelGrainsExpanded
    {
        get => _panelGrainsExpanded;
        set
        {
            if (_panelGrainsExpanded == value) return;
            _panelGrainsExpanded = value;
            OnPropertyChanged();
        }
    }

    public string PanelGrainHeader
    {
        get
        {
            if (PanelGrains.Count == 0) return "板件木纹";
            var x = 0;
            var y = 0;
            var none = 0;
            foreach (var row in PanelGrains)
            {
                if (row.GrainKey == "X") x++;
                else if (row.GrainKey == "Y") y++;
                else none++;
            }
            if (x == PanelGrains.Count) return $"板件木纹（{x} 块 X）";
            if (y == PanelGrains.Count) return $"板件木纹（{y} 块 Y）";
            if (none == PanelGrains.Count) return $"板件木纹（{none} 无）";
            return $"板件木纹（{PanelGrains.Count}）";
        }
    }

    public void NotifyPanelGrainHeader() => OnPropertyChanged(nameof(PanelGrainHeader));

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
