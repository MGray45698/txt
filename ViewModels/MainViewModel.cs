using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using TxtEditorSkeleton.Commands;

namespace TxtEditorSkeleton.ViewModels;

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

    public MainViewModel()
    {
        _newCommand = new RelayCommand(_ => ExecuteAction("Создать новый документ"));
        _saveCommand = new RelayCommand(_ => ExecuteAction("Сохранить документ"), _ => IsDocumentLoaded);
        _boldCommand = new RelayCommand(_ => ExecuteAction("Переключить жирный текст"), _ => IsDocumentLoaded);

        NewCommand = _newCommand;
        SaveCommand = _saveCommand;
        BoldCommand = _boldCommand;
        ToggleDocumentStateCommand = new RelayCommand(_ => IsDocumentLoaded = !IsDocumentLoaded);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand BoldCommand { get; }

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

    private void ExecuteAction(string actionName)
    {
        StatusMessage = $"{actionName} ({DateTime.Now:HH:mm:ss})";
    }

    private void UpdateCommandStates()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _boldCommand.RaiseCanExecuteChanged();
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
