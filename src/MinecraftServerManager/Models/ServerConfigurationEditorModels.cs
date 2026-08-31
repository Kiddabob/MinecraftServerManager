using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace MinecraftServerManager.Models;

public enum ServerConfigurationFieldKind
{
    Boolean,
    Integer,
    Number,
    Choice,
    Text
}

public enum ServerConfigurationChoicePresentation
{
    DropDown,
    Radio
}

public sealed record ServerConfigurationChoiceOption(string Value, string DisplayName);

public sealed class ServerConfigurationField : INotifyPropertyChanged
{
    private bool _booleanValue;
    private double _numericValue;
    private string _numericText;
    private string _textValue;
    private ServerConfigurationChoiceOption? _selectedOption;
    private string _validationText = string.Empty;

    internal ServerConfigurationField(
        string key,
        string displayName,
        string section,
        string description,
        ServerConfigurationFieldKind kind,
        ServerConfigurationChoicePresentation choicePresentation,
        double? declaredMinimum,
        double? declaredMaximum,
        double step,
        IReadOnlyList<ServerConfigurationChoiceOption> options,
        bool booleanValue,
        double numericValue,
        string textValue,
        ServerConfigurationChoiceOption? selectedOption,
        string limitsText,
        int valueStartOffset,
        int valueLength,
        ServerConfigurationValueEncoding valueEncoding)
    {
        Key = key;
        DisplayName = displayName;
        Section = section;
        Description = description;
        Kind = kind;
        ChoicePresentation = choicePresentation;
        DeclaredMinimum = declaredMinimum;
        DeclaredMaximum = declaredMaximum;
        Step = step;
        Options = options;
        _booleanValue = booleanValue;
        _numericValue = numericValue;
        _numericText = FormatNumericValue(numericValue, kind);
        _textValue = textValue;
        _selectedOption = selectedOption;
        LimitsText = limitsText;
        ValueStartOffset = valueStartOffset;
        ValueLength = valueLength;
        ValueEncoding = valueEncoding;
        Validate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? ValueChanged;

    public string Key { get; }

    public string DisplayName { get; }

    public string Section { get; }

    public string Description { get; }

    public ServerConfigurationFieldKind Kind { get; }

    public bool IsBoolean => Kind == ServerConfigurationFieldKind.Boolean;

    public bool IsNumber => Kind is ServerConfigurationFieldKind.Integer or ServerConfigurationFieldKind.Number;

    public bool IsDropDown =>
        Kind == ServerConfigurationFieldKind.Choice
        && ChoicePresentation == ServerConfigurationChoicePresentation.DropDown;

    public bool IsRadio =>
        Kind == ServerConfigurationFieldKind.Choice
        && ChoicePresentation == ServerConfigurationChoicePresentation.Radio;

    public bool IsText => Kind == ServerConfigurationFieldKind.Text;

    public ServerConfigurationChoicePresentation ChoicePresentation { get; }

    public double? DeclaredMinimum { get; }

    public double? DeclaredMaximum { get; }

    public double Minimum => DeclaredMinimum ?? double.MinValue;

    public double Maximum => DeclaredMaximum ?? double.MaxValue;

    public double Step { get; }

    public IReadOnlyList<ServerConfigurationChoiceOption> Options { get; }

    public string LimitsText { get; }

    public bool BooleanValue
    {
        get => _booleanValue;
        set => SetValue(ref _booleanValue, value);
    }

    public double NumericValue
    {
        get => _numericValue;
        set
        {
            var formatted = FormatNumericValue(value, Kind);
            if (_numericValue.Equals(value) && _numericText == formatted)
            {
                return;
            }

            _numericValue = value;
            _numericText = formatted;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NumericText));
            Validate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string NumericText
    {
        get => _numericText;
        set
        {
            value ??= string.Empty;
            if (_numericText == value)
            {
                return;
            }

            _numericText = value;
            OnPropertyChanged();
            if (TryParseNumericText(value, out var parsedValue) && !_numericValue.Equals(parsedValue))
            {
                _numericValue = parsedValue;
                OnPropertyChanged(nameof(NumericValue));
            }

            Validate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string TextValue
    {
        get => _textValue;
        set => SetValue(ref _textValue, value ?? string.Empty);
    }

    public ServerConfigurationChoiceOption? SelectedOption
    {
        get => _selectedOption;
        set => SetValue(ref _selectedOption, value);
    }

    public string ValidationText
    {
        get => _validationText;
        private set
        {
            if (_validationText == value)
            {
                return;
            }

            _validationText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsValid));
        }
    }

    public bool IsValid => string.IsNullOrEmpty(ValidationText);

    internal int ValueStartOffset { get; }

    internal int ValueLength { get; }

    internal ServerConfigurationValueEncoding ValueEncoding { get; }

    private void SetValue<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        Validate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Validate()
    {
        if (Kind is ServerConfigurationFieldKind.Integer or ServerConfigurationFieldKind.Number)
        {
            if (!TryParseNumericText(NumericText, out var parsedValue)
                || double.IsNaN(parsedValue)
                || double.IsInfinity(parsedValue))
            {
                ValidationText = "Enter a valid number.";
                return;
            }

            if (Kind == ServerConfigurationFieldKind.Integer && parsedValue != Math.Truncate(parsedValue))
            {
                ValidationText = "Enter a whole number.";
                return;
            }

            if (DeclaredMinimum is not null && parsedValue < DeclaredMinimum)
            {
                ValidationText = $"The minimum value is {DeclaredMinimum:0.########}.";
                return;
            }

            if (DeclaredMaximum is not null && parsedValue > DeclaredMaximum)
            {
                ValidationText = $"The maximum value is {DeclaredMaximum:0.########}.";
                return;
            }

            if (!_numericValue.Equals(parsedValue))
            {
                _numericValue = parsedValue;
                OnPropertyChanged(nameof(NumericValue));
            }
        }

        if (Kind == ServerConfigurationFieldKind.Choice && SelectedOption is null)
        {
            ValidationText = "Choose one of the available values.";
            return;
        }

        ValidationText = string.Empty;
    }

    private static bool TryParseNumericText(string value, out double result) =>
        double.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.CurrentCulture,
            out result)
        || double.TryParse(
            value,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out result);

    private static string FormatNumericValue(double value, ServerConfigurationFieldKind kind) =>
        kind == ServerConfigurationFieldKind.Integer
            ? Math.Truncate(value).ToString("0", CultureInfo.InvariantCulture)
            : value.ToString("0.################", CultureInfo.InvariantCulture);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ServerConfigurationFriendlyDocument
{
    internal ServerConfigurationFriendlyDocument(
        string sourceText,
        IReadOnlyList<ServerConfigurationField> fields,
        string summary,
        string guidance)
    {
        SourceText = sourceText;
        Fields = fields;
        Summary = summary;
        Guidance = guidance;
    }

    public IReadOnlyList<ServerConfigurationField> Fields { get; }

    public string Summary { get; }

    public string Guidance { get; }

    internal string SourceText { get; }
}

internal enum ServerConfigurationValueEncoding
{
    Raw,
    JsonString,
    SingleQuoted,
    DoubleQuoted
}
