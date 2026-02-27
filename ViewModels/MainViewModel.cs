using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using TxtEditorSkeleton.Commands;

namespace TxtEditorSkeleton.ViewModels;

public interface ITextEditorService
{
    event Action? FormattingStateChanged;

    bool CanEdit { get; }
    bool IsBoldActive { get; }
    bool IsItalicActive { get; }
    bool IsUnderlineActive { get; }
    bool IsStrikethroughActive { get; }
    TextAlignment CurrentParagraphAlignment { get; }

    void ToggleBold();
    void ToggleItalic();
    void ToggleUnderline();
    void ToggleStrikethrough();
    void SetParagraphAlignment(TextAlignment alignment);
}

public class MainViewModel : INotifyPropertyChanged
{
    private string _statusMessage = "Готово";
    private bool _isDocumentLoaded = true;
    private bool _isBoldAtCursor;
    private bool _isItalicAtCursor;
    private bool _isUnderlineAtCursor;
    private bool _isStrikeAtCursor;
    private bool _hasSelection;
    private string _paragraphAlignment = "Left";

    private readonly RelayCommand _newCommand;
    private readonly RelayCommand _saveCommand;
    private readonly RelayCommand _boldCommand;
    private readonly RelayCommand _italicCommand;
    private readonly RelayCommand _underlineCommand;
    private readonly RelayCommand _strikethroughCommand;
    private readonly RelayCommand _alignLeftCommand;
    private readonly RelayCommand _alignCenterCommand;
    private readonly RelayCommand _alignRightCommand;
    private ITextEditorService? _editorService;
    private bool _isBoldActive;
    private bool _isItalicActive;
    private bool _isUnderlineActive;
    private bool _isStrikethroughActive;
    private bool _isAlignLeftActive;
    private bool _isAlignCenterActive;
    private bool _isAlignRightActive;

    public MainViewModel()
    {
        _newCommand = new RelayCommand(_ => ExecuteAction("Создать новый документ"));
        _saveCommand = new RelayCommand(_ => ExecuteAction("Сохранить документ"), _ => IsDocumentLoaded);
        _boldCommand = new RelayCommand(_ => ExecuteBold(), _ => CanExecuteTextFormat());
        _italicCommand = new RelayCommand(_ => ExecuteItalic(), _ => CanExecuteTextFormat());
        _underlineCommand = new RelayCommand(_ => ExecuteUnderline(), _ => CanExecuteTextFormat());
        _strikethroughCommand = new RelayCommand(_ => ExecuteStrikethrough(), _ => CanExecuteTextFormat());
        _alignLeftCommand = new RelayCommand(_ => ExecuteAlignLeft(), _ => CanExecuteTextFormat());
        _alignCenterCommand = new RelayCommand(_ => ExecuteAlignCenter(), _ => CanExecuteTextFormat());
        _alignRightCommand = new RelayCommand(_ => ExecuteAlignRight(), _ => CanExecuteTextFormat());

        NewCommand = _newCommand;
        SaveCommand = _saveCommand;
        BoldCommand = _boldCommand;
        ItalicCommand = _italicCommand;
        UnderlineCommand = _underlineCommand;
        StrikethroughCommand = _strikethroughCommand;
        AlignLeftCommand = _alignLeftCommand;
        AlignCenterCommand = _alignCenterCommand;
        AlignRightCommand = _alignRightCommand;
        ToggleDocumentStateCommand = new RelayCommand(_ => IsDocumentLoaded = !IsDocumentLoaded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BoldCommand { get; }
    public ICommand ItalicCommand { get; }
    public ICommand UnderlineCommand { get; }
    public ICommand StrikethroughCommand { get; }
    public ICommand AlignLeftCommand { get; }
    public ICommand AlignCenterCommand { get; }
    public ICommand AlignRightCommand { get; }

    // Плейсхолдер для демонстрации динамического обновления состояния кнопок.
    public ICommand ToggleDocumentStateCommand { get; }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage == value)
            {
                return;
            }

            _statusMessage = value;
            OnPropertyChanged();
        }
    }

    public bool IsDocumentLoaded
    {
        get => _isDocumentLoaded;
        set
        {
            if (_isDocumentLoaded == value)
            {
                return;
            }

            _isDocumentLoaded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DocumentStateLabel));
            UpdateCommandStates();
        }
    }

    public bool IsBoldActive
    {
        get => _isBoldActive;
        private set => SetStateField(ref _isBoldActive, value);
    }

    public bool IsItalicActive
    {
        get => _isItalicActive;
        private set => SetStateField(ref _isItalicActive, value);
    }

    public bool IsUnderlineActive
    {
        get => _isUnderlineActive;
        private set => SetStateField(ref _isUnderlineActive, value);
    }

    public bool IsStrikethroughActive
    {
        get => _isStrikethroughActive;
        private set => SetStateField(ref _isStrikethroughActive, value);
    }

    public bool IsAlignLeftActive
    {
        get => _isAlignLeftActive;
        private set => SetStateField(ref _isAlignLeftActive, value);
    }

    public bool IsAlignCenterActive
    {
        get => _isAlignCenterActive;
        private set => SetStateField(ref _isAlignCenterActive, value);
    }

    public bool IsAlignRightActive
    {
        get => _isAlignRightActive;
        private set => SetStateField(ref _isAlignRightActive, value);
    }

    public bool IsBoldAtCursor
    {
        get => _isBoldAtCursor;
        private set => SetDebugStateField(ref _isBoldAtCursor, value);
    }

    public bool IsItalicAtCursor
    {
        get => _isItalicAtCursor;
        private set => SetDebugStateField(ref _isItalicAtCursor, value);
    }

    public bool IsUnderlineAtCursor
    {
        get => _isUnderlineAtCursor;
        private set => SetDebugStateField(ref _isUnderlineAtCursor, value);
    }

    public bool IsStrikeAtCursor
    {
        get => _isStrikeAtCursor;
        private set => SetDebugStateField(ref _isStrikeAtCursor, value);
    }

    public bool HasSelection
    {
        get => _hasSelection;
        private set => SetDebugStateField(ref _hasSelection, value);
    }

    public string ParagraphAlignment
    {
        get => _paragraphAlignment;
        private set => SetDebugStateField(ref _paragraphAlignment, value);
    }

    public string DocumentStateLabel => IsDocumentLoaded ? "Документ активен" : "Документ отключен";

    public string DebugPanelText =>
        $"Debug: Bold={IsBoldAtCursor}, Italic={IsItalicAtCursor}, Underline={IsUnderlineAtCursor}, Strike={IsStrikeAtCursor}, Align={ParagraphAlignment}, Selection={HasSelection}";

    public void UpdateEditorDebugState(
        bool isBold,
        bool isItalic,
        bool isUnderline,
        bool isStrike,
        string paragraphAlignment,
        bool hasSelection)
    {
        IsBoldAtCursor = isBold;
        IsItalicAtCursor = isItalic;
        IsUnderlineAtCursor = isUnderline;
        IsStrikeAtCursor = isStrike;
        ParagraphAlignment = paragraphAlignment;
        HasSelection = hasSelection;
    }

    public void AttachEditorService(ITextEditorService editorService)
    {
        _editorService = editorService;
        _editorService.FormattingStateChanged += OnEditorFormattingChanged;
        UpdateTextFormattingStateFromEditor();
        UpdateCommandStates();
    }

    private void ExecuteAction(string actionName)
    {
        StatusMessage = $"{actionName} ({DateTime.Now:HH:mm:ss})";
    }

    private void UpdateCommandStates()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _boldCommand.RaiseCanExecuteChanged();
        _italicCommand.RaiseCanExecuteChanged();
        _underlineCommand.RaiseCanExecuteChanged();
        _strikethroughCommand.RaiseCanExecuteChanged();
        _alignLeftCommand.RaiseCanExecuteChanged();
        _alignCenterCommand.RaiseCanExecuteChanged();
        _alignRightCommand.RaiseCanExecuteChanged();
    }

    private void ExecuteBold()
    {
        _editorService?.ToggleBold();
        ExecuteAction("Переключить жирный текст");
    }

    private bool CanExecuteTextFormat() => IsDocumentLoaded && _editorService?.CanEdit == true;

    private void OnEditorFormattingChanged()
    {
        UpdateTextFormattingStateFromEditor();
        UpdateCommandStates();
    }

    private void ExecuteItalic()
    {
        _editorService?.ToggleItalic();
        ExecuteAction("Переключить курсив");
    }

    private void ExecuteUnderline()
    {
        _editorService?.ToggleUnderline();
        ExecuteAction("Переключить подчеркивание");
    }

    private void ExecuteStrikethrough()
    {
        _editorService?.ToggleStrikethrough();
        ExecuteAction("Переключить зачёркивание");
    }

    private void ExecuteAlignLeft()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Left);
        ExecuteAction("Выравнивание по левому краю");
    }

    private void ExecuteAlignCenter()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Center);
        ExecuteAction("Выравнивание по центру");
    }

    private void ExecuteAlignRight()
    {
        _editorService?.SetParagraphAlignment(TextAlignment.Right);
        ExecuteAction("Выравнивание по правому краю");
    }

    private void UpdateTextFormattingStateFromEditor()
    {
        IsBoldActive = _editorService?.IsBoldActive == true;
        IsItalicActive = _editorService?.IsItalicActive == true;
        IsUnderlineActive = _editorService?.IsUnderlineActive == true;
        IsStrikethroughActive = _editorService?.IsStrikethroughActive == true;

        var alignment = _editorService?.CurrentParagraphAlignment ?? TextAlignment.Left;
        IsAlignLeftActive = alignment == TextAlignment.Left;
        IsAlignCenterActive = alignment == TextAlignment.Center;
        IsAlignRightActive = alignment == TextAlignment.Right;
    }

    private void SetStateField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void SetDebugStateField(ref bool field, bool value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DebugPanelText));
    }

    private void SetDebugStateField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(DebugPanelText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
